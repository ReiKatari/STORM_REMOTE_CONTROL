// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core;

/// <summary>
/// Production UDP transport layer for the STORM protocol.
/// Handles packet send/receive, fragmentation/reassembly, keepalive,
/// reliable delivery, and per-session encryption via <see cref="CryptoEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// All I/O is performed asynchronously. A background receive loop processes
/// inbound datagrams, validates headers, decrypts payloads, reassembles
/// fragments, and fires <see cref="PacketReceived"/>.
/// </para>
/// <para>
/// Call <see cref="StartListening"/> to bind a local port and start the
/// receive loop, then <see cref="SetRemotePeer"/> once a peer address is
/// known (after NAT hole-punching or direct connection). Outbound packets
/// are encrypted automatically when the <see cref="CryptoEngine"/> has an
/// established session key.
/// </para>
/// </remarks>
public sealed class NetworkTransport : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────

    /// <summary>Raised when a valid, complete packet is received.</summary>
    public event Action<StormPacketHeader, byte[], IPEndPoint>? PacketReceived;

    /// <summary>Raised when a new peer endpoint is registered.</summary>
    public event Action<IPEndPoint>? PeerConnected;

    /// <summary>Raised when the peer is considered disconnected (keepalive timeout).</summary>
    public event Action? PeerDisconnected;

    /// <summary>Raised when a ping/pong RTT measurement completes.</summary>
    public event Action<double>? LatencyMeasured;

    // ── Constants ─────────────────────────────────────────────────────

    /// <summary>Maximum number of retransmission attempts for reliable packets.</summary>
    private const int MaxReliableRetries = 3;

    /// <summary>Milliseconds to wait for an ACK before retransmitting.</summary>
    private const int ReliableRetransmitMs = 200;

    /// <summary>Size of the fragment reassembly window.</summary>
    private const int FragmentExpirationMs = 10_000;

    // ── Fields ────────────────────────────────────────────────────────

    private readonly CryptoEngine _crypto;
    private readonly ConcurrentDictionary<uint, FragmentAssembly> _fragmentBuffers = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<bool>> _pendingAcks = new();
    private readonly ConcurrentDictionary<long, long> _pendingPings = new(); // seq → timestamp ticks

    private UdpClient? _udpClient;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private Task? _keepAliveTask;
    private Task? _metricsTask;
    private Task? _fragmentCleanupTask;
    private IPEndPoint? _remotePeer;

    private uint _nextSequence;
    private long _bytesSentSinceLastMetric;
    private long _framesReceivedSinceLastMetric;
    private long _lastPeerActivityTicks;

    private volatile bool _disposed;
    private volatile bool _isListening;
    private int _localPort;

    private double _currentLatencyMs;
    private double _currentBitrateMbps;
    private int _currentFps;

    // ── Properties ────────────────────────────────────────────────────

    /// <summary>Gets a value indicating whether the transport is actively listening.</summary>
    public bool IsListening => _isListening;

    /// <summary>Gets the local UDP port the transport is bound to (0 if not listening).</summary>
    public int LocalPort => _localPort;

    /// <summary>Gets the currently registered remote peer, or <see langword="null"/>.</summary>
    public IPEndPoint? RemotePeer => _remotePeer;

    /// <summary>Gets the last measured round-trip latency in milliseconds.</summary>
    public double CurrentLatencyMs => _currentLatencyMs;

    /// <summary>Gets the current outbound bitrate in megabits per second.</summary>
    public double CurrentBitrateMbps => _currentBitrateMbps;

    /// <summary>Gets the number of complete video frames received in the last second.</summary>
    public int CurrentFps => _currentFps;

    // ── Constructor ───────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new <see cref="NetworkTransport"/> with the specified
    /// cryptographic engine for payload encryption/decryption.
    /// </summary>
    /// <param name="crypto">
    /// The <see cref="CryptoEngine"/> instance used to encrypt outbound
    /// payloads and decrypt inbound payloads. Encryption is only applied
    /// when <see cref="CryptoEngine.IsSessionEstablished"/> is
    /// <see langword="true"/>.
    /// </param>
    public NetworkTransport(CryptoEngine crypto)
    {
        ArgumentNullException.ThrowIfNull(crypto);
        _crypto = crypto;
    }

    // ── Listening ─────────────────────────────────────────────────────

    /// <summary>
    /// Binds to the specified local UDP port and starts the background
    /// receive loop, keepalive timer, and metrics collector.
    /// </summary>
    /// <param name="port">
    /// The local UDP port to bind to. Defaults to <see cref="StormProtocol.DefaultPort"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the transport is already listening.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the transport has been disposed.
    /// </exception>
    public void StartListening(int port = StormProtocol.DefaultPort)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isListening)
        {
            throw new InvalidOperationException("Transport is already listening.");
        }

        _udpClient = new UdpClient(port);
        
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            _udpClient.Client.ReceiveBufferSize = 1024 * 1024 * 8; // 8 MB
            _udpClient.Client.SendBufferSize = 1024 * 1024 * 8; // 8 MB
        }
        catch { /* Fallback for unsupported platforms */ }

        _localPort = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;
        _receiveCts = new CancellationTokenSource();
        _lastPeerActivityTicks = Stopwatch.GetTimestamp();

        CancellationToken token = _receiveCts.Token;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(token), token);
        _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(token), token);
        _metricsTask = Task.Run(() => MetricsLoopAsync(token), token);
        _fragmentCleanupTask = Task.Run(() => FragmentCleanupLoopAsync(token), token);

        _isListening = true;
    }

    /// <summary>
    /// Stops listening, cancels the background receive loop, and closes
    /// the underlying UDP socket.
    /// </summary>
    public void StopListening()
    {
        if (!_isListening)
        {
            return;
        }

        _isListening = false;
        _receiveCts?.Cancel();

        // Wait briefly for background tasks to wind down.
        try
        {
            Task.WhenAll(
                _receiveTask ?? Task.CompletedTask,
                _keepAliveTask ?? Task.CompletedTask,
                _metricsTask ?? Task.CompletedTask,
                _fragmentCleanupTask ?? Task.CompletedTask
            ).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Swallow cancellation exceptions from background loops.
        }

        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
        _keepAliveTask = null;
        _metricsTask = null;
        _fragmentCleanupTask = null;
    }

    /// <summary>
    /// Resets the connection state without full dispose — allows reconnection.
    /// </summary>
    public void ResetConnection()
    {
        _remotePeer = null;
        _fragmentBuffers.Clear();
        _pendingAcks.Clear();
        _pendingPings.Clear();
        _currentLatencyMs = 0;
        _currentBitrateMbps = 0;
        _currentFps = 0;
        _nextSequence = 0;
        _bytesSentSinceLastMetric = 0;
        _framesReceivedSinceLastMetric = 0;
        _lastPeerActivityTicks = Stopwatch.GetTimestamp();
    }

    // ── Peer Management ───────────────────────────────────────────────

    /// <summary>
    /// Registers the remote peer endpoint (typically after NAT hole-punching
    /// or a direct-connect handshake succeeds).
    /// </summary>
    /// <param name="peerEndpoint">The remote peer's IP endpoint.</param>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the transport has been disposed.
    /// </exception>
    public void SetRemotePeer(IPEndPoint peerEndpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(peerEndpoint);

        _remotePeer = peerEndpoint;
        _lastPeerActivityTicks = Stopwatch.GetTimestamp();
        PeerConnected?.Invoke(peerEndpoint);
    }

    // ── Sending ───────────────────────────────────────────────────────

    /// <summary>
    /// Sends raw bytes to a specific endpoint without encryption or
    /// STORM header framing. Used for NAT hole-punch probes.
    /// </summary>
    /// <param name="data">The raw bytes to send.</param>
    /// <param name="destination">The target endpoint.</param>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the transport has been disposed.
    /// </exception>
    public async Task SendRawAsync(byte[] data, IPEndPoint destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(destination);

        if (_udpClient is null)
        {
            throw new InvalidOperationException("Transport is not listening.");
        }

        await _udpClient.SendAsync(data, data.Length, destination).ConfigureAwait(false);
        Interlocked.Add(ref _bytesSentSinceLastMetric, data.Length);
    }

    /// <summary>
    /// Sends an encrypted STORM packet to the currently registered remote peer.
    /// </summary>
    /// <param name="type">The packet type.</param>
    /// <param name="payload">The plaintext payload (will be encrypted if session is established).</param>
    /// <param name="reliable">
    /// If <see langword="true"/>, the packet is retransmitted up to
    /// <see cref="MaxReliableRetries"/> times until an ACK is received.
    /// </param>
    /// <param name="fragIndex">Fragment index (0 for non-fragmented).</param>
    /// <param name="fragTotal">Total fragment count (1 for non-fragmented).</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no remote peer has been set or the transport is not listening.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the transport has been disposed.
    /// </exception>
    public async Task SendAsync(
        StormPacketType type,
        byte[] payload,
        bool reliable = false,
        ushort fragIndex = 0,
        ushort fragTotal = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(payload);

        if (_udpClient is null)
        {
            throw new InvalidOperationException("Transport is not listening.");
        }

        if (_remotePeer is null)
        {
            throw new InvalidOperationException("No remote peer has been set.");
        }

        byte[] encryptedPayload = _crypto.IsSessionEstablished
            ? _crypto.Encrypt(payload)
            : payload;

        uint seq = (uint)Interlocked.Increment(ref Unsafe.As<uint, int>(ref _nextSequence));
        byte[] packet = StormProtocol.BuildPacket(type, seq, encryptedPayload, fragIndex, fragTotal);

        if (reliable)
        {
            await SendReliableAsync(packet, seq).ConfigureAwait(false);
        }
        else
        {
            await SendPacketInternalAsync(packet).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a large payload that exceeds <see cref="StormProtocol.MaxPayloadPerPacket"/>
    /// by automatically splitting it into multiple <see cref="StormPacketType.VideoFrameFragment"/>
    /// packets.
    /// </summary>
    /// <param name="type">The logical packet type for the complete payload.</param>
    /// <param name="payload">The complete plaintext payload to fragment and send.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no remote peer has been set or the transport is not listening.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the transport has been disposed.
    /// </exception>
    public async Task SendFragmentedAsync(StormPacketType type, byte[] payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(payload);

        if (_udpClient is null)
        {
            throw new InvalidOperationException("Transport is not listening.");
        }

        if (_remotePeer is null)
        {
            throw new InvalidOperationException("No remote peer has been set.");
        }

        int maxChunk = StormProtocol.MaxPayloadPerPacket;
        int totalFragments = (payload.Length + maxChunk - 1) / maxChunk;

        if (totalFragments <= 1)
        {
            // Fits in a single packet — no fragmentation needed.
            await SendAsync(type, payload).ConfigureAwait(false);
            return;
        }

        // All fragments share the same base sequence number so the
        // reassembly logic can correlate them.
        uint baseSeq = (uint)Interlocked.Increment(ref Unsafe.As<uint, int>(ref _nextSequence));

        for (int i = 0; i < totalFragments; i++)
        {
            int offset = i * maxChunk;
            int length = Math.Min(maxChunk, payload.Length - offset);

            byte[] chunk = new byte[length];
            Buffer.BlockCopy(payload, offset, chunk, 0, length);

            byte[] encryptedChunk = _crypto.IsSessionEstablished
                ? _crypto.Encrypt(chunk)
                : chunk;

            byte[] packet = StormProtocol.BuildPacket(
                StormPacketType.VideoFrameFragment,
                baseSeq,
                encryptedChunk,
                (ushort)i,
                (ushort)totalFragments);

            await SendPacketInternalAsync(packet).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a keepalive ping to the remote peer. The RTT is measured
    /// when the corresponding <see cref="StormPacketType.Pong"/> arrives.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no remote peer has been set.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the transport has been disposed.
    /// </exception>
    public async Task SendPingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        uint seq = (uint)Interlocked.Increment(ref Unsafe.As<uint, int>(ref _nextSequence));
        long ticks = Stopwatch.GetTimestamp();
        _pendingPings[seq] = ticks;

        // Payload carries the sequence so the pong can echo it back.
        byte[] seqBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(seqBytes, seq);

        byte[] packet = StormProtocol.BuildPacket(StormPacketType.Ping, seq, seqBytes);

        if (_udpClient is not null && _remotePeer is not null)
        {
            await SendPacketInternalAsync(packet).ConfigureAwait(false);
        }
    }

    // ── Private: sending helpers ──────────────────────────────────────

    /// <summary>Sends a fully built packet to the remote peer.</summary>
    private async Task SendPacketInternalAsync(byte[] packet)
    {
        Debug.Assert(_udpClient is not null);
        Debug.Assert(_remotePeer is not null);

        await _udpClient!.SendAsync(packet, packet.Length, _remotePeer).ConfigureAwait(false);
        Interlocked.Add(ref _bytesSentSinceLastMetric, packet.Length);
    }

    /// <summary>Sends a packet with reliability: retransmit up to <see cref="MaxReliableRetries"/> times.</summary>
    private async Task SendReliableAsync(byte[] packet, uint sequence)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[sequence] = tcs;

        try
        {
            for (int attempt = 0; attempt <= MaxReliableRetries; attempt++)
            {
                await SendPacketInternalAsync(packet).ConfigureAwait(false);

                using var delayCts = new CancellationTokenSource(ReliableRetransmitMs);
                try
                {
                    // Wait for ACK or timeout.
                    await tcs.Task.WaitAsync(delayCts.Token).ConfigureAwait(false);
                    return; // ACK received.
                }
                catch (OperationCanceledException) when (!tcs.Task.IsCompleted)
                {
                    // Timeout — retry.
                    Debug.WriteLine($"[STORM] Reliable packet seq={sequence} retry {attempt + 1}/{MaxReliableRetries}");
                }
            }

            Debug.WriteLine($"[STORM] Reliable packet seq={sequence} delivery failed after {MaxReliableRetries} retries.");
        }
        finally
        {
            _pendingAcks.TryRemove(sequence, out _);
        }
    }

    // ── Private: receive loop ─────────────────────────────────────────

    /// <summary>Background loop that continuously reads UDP datagrams.</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        Debug.Assert(_udpClient is not null);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await _udpClient!.ReceiveAsync(ct).ConfigureAwait(false);
                ProcessReceivedDatagram(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"[STORM] Socket error in receive loop: {ex.SocketErrorCode}");
                // ICMP port-unreachable on Windows causes this; swallow and continue.
                if (ct.IsCancellationRequested) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    /// <summary>Processes a single received datagram.</summary>
    private void ProcessReceivedDatagram(byte[] datagram, IPEndPoint sender)
    {
        if (datagram.Length < StormProtocol.HeaderSize)
        {
            Debug.WriteLine($"[STORM] Undersized datagram ({datagram.Length} bytes) from {sender}.");
            return;
        }

        StormPacketHeader header = StormProtocol.ReadHeader(datagram);

        if (!StormProtocol.ValidateHeader(in header))
        {
            Debug.WriteLine($"[STORM] Invalid magic/version from {sender}.");
            return;
        }

        int expectedTotal = StormProtocol.HeaderSize + (int)header.PayloadLength;
        if (datagram.Length < expectedTotal)
        {
            Debug.WriteLine($"[STORM] Truncated datagram from {sender} " +
                            $"(have {datagram.Length}, need {expectedTotal}).");
            return;
        }

        // Extract and verify the payload.
        byte[] encryptedPayload = new byte[header.PayloadLength];
        Buffer.BlockCopy(datagram, StormProtocol.HeaderSize, encryptedPayload, 0, (int)header.PayloadLength);

        if (!StormProtocol.VerifyChecksum(in header, encryptedPayload))
        {
            Debug.WriteLine($"[STORM] CRC-32 mismatch from {sender}.");
            return;
        }

        // Record peer activity timestamp.
        _lastPeerActivityTicks = Stopwatch.GetTimestamp();

        // Decrypt payload.
        byte[] payload;
        try
        {
            payload = _crypto.IsSessionEstablished
                ? _crypto.Decrypt(encryptedPayload)
                : encryptedPayload;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Debug.WriteLine($"[STORM] Decryption failed from {sender}: {ex.Message}");
            return;
        }

        // Handle control packets internally.
        switch (header.PacketType)
        {
            case StormPacketType.Ping:
                HandlePing(header, payload, sender);
                return;

            case StormPacketType.Pong:
                HandlePong(header, payload);
                break;

            case StormPacketType.Ack:
                HandleAck(header, payload);
                return;

            case StormPacketType.Disconnect:
                _remotePeer = null;
                PeerDisconnected?.Invoke();
                return;
        }

        // Handle fragmented packets.
        if (header.FragmentTotal > 1)
        {
            HandleFragment(header, payload, sender);
            return;
        }

        // Track video frames for FPS calculation.
        if (header.PacketType is StormPacketType.VideoFrame)
        {
            Interlocked.Increment(ref _framesReceivedSinceLastMetric);
        }

        PacketReceived?.Invoke(header, payload, sender);
    }

    // ── Private: ping / pong ──────────────────────────────────────────

    /// <summary>Handles an incoming Ping by replying with a Pong.</summary>
    private async void HandlePing(StormPacketHeader header, byte[] payload, IPEndPoint sender)
    {
        if (_udpClient is null) return;

        // Echo the payload back as a Pong.
        byte[] encryptedPong = _crypto.IsSessionEstablished
            ? _crypto.Encrypt(payload)
            : payload;

        byte[] pong = StormProtocol.BuildPacket(
            StormPacketType.Pong,
            header.SequenceNumber,
            encryptedPong);

        try
        {
            await _udpClient.SendAsync(pong, pong.Length, sender).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STORM] Failed to send Pong: {ex.Message}");
        }
    }

    /// <summary>Handles an incoming Pong by computing RTT.</summary>
    private void HandlePong(StormPacketHeader header, byte[] payload)
    {
        if (payload.Length >= 4)
        {
            uint pingSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload);
            if (_pendingPings.TryRemove(pingSeq, out long sentTicks))
            {
                long elapsed = Stopwatch.GetTimestamp() - sentTicks;
                double rttMs = (elapsed * 1000.0) / Stopwatch.Frequency;
                _currentLatencyMs = rttMs;
                LatencyMeasured?.Invoke(rttMs);
            }
        }
    }

    /// <summary>Handles an incoming ACK by completing the corresponding reliable-send TCS.</summary>
    private void HandleAck(StormPacketHeader header, byte[] payload)
    {
        // ACK payload carries the acknowledged sequence number.
        if (payload.Length >= 4)
        {
            uint ackedSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload);
            if (_pendingAcks.TryRemove(ackedSeq, out TaskCompletionSource<bool>? tcs))
            {
                tcs.TrySetResult(true);
            }
        }
    }

    // ── Private: fragmentation/reassembly ─────────────────────────────

    /// <summary>
    /// Accumulates an incoming fragment. When all fragments for a base
    /// sequence number have arrived, the complete payload is reassembled
    /// and <see cref="PacketReceived"/> is raised.
    /// </summary>
    private void HandleFragment(StormPacketHeader header, byte[] payload, IPEndPoint sender)
    {
        FragmentAssembly assembly = _fragmentBuffers.GetOrAdd(
            header.SequenceNumber,
            _ => new FragmentAssembly(header.FragmentTotal, header.PacketType));

        assembly.AddFragment(header.FragmentIndex, payload);

        if (assembly.IsComplete)
        {
            _fragmentBuffers.TryRemove(header.SequenceNumber, out _);
            byte[] reassembled = assembly.Reassemble();

            // Count the reassembled frame for FPS if applicable.
            if (assembly.OriginalType is StormPacketType.VideoFrame
                or StormPacketType.VideoFrameFragment)
            {
                Interlocked.Increment(ref _framesReceivedSinceLastMetric);
            }

            // Raise the event with the original (non-fragment) type.
            StormPacketHeader completedHeader = header;
            completedHeader.PacketType = assembly.OriginalType;
            completedHeader.FragmentIndex = 0;
            completedHeader.FragmentTotal = 1;
            completedHeader.PayloadLength = (uint)reassembled.Length;

            PacketReceived?.Invoke(completedHeader, reassembled, sender);
        }
    }

    // ── Private: keepalive loop ───────────────────────────────────────

    /// <summary>Periodically sends Ping and checks for peer timeout.</summary>
    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StormProtocol.KeepAliveIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_remotePeer is null)
            {
                continue;
            }

            // Check for timeout.
            long elapsed = Stopwatch.GetTimestamp() - _lastPeerActivityTicks;
            double elapsedMs = (elapsed * 1000.0) / Stopwatch.Frequency;

            if (elapsedMs > StormProtocol.ConnectionTimeoutMs)
            {
                Debug.WriteLine("[STORM] Peer timed out.");
                _remotePeer = null;
                PeerDisconnected?.Invoke();
                continue;
            }

            try
            {
                await SendPingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Failed to send keepalive ping: {ex.Message}");
            }
        }
    }

    // ── Private: metrics loop ─────────────────────────────────────────

    /// <summary>Samples bitrate and FPS counters every second.</summary>
    private async Task MetricsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long bytes = Interlocked.Exchange(ref _bytesSentSinceLastMetric, 0);
            long frames = Interlocked.Exchange(ref _framesReceivedSinceLastMetric, 0);

            _currentBitrateMbps = (bytes * 8.0) / (1024.0 * 1024.0);
            _currentFps = (int)frames;
        }
    }

    // ── Private: fragment cleanup loop ────────────────────────────────

    /// <summary>Evicts stale fragment assemblies that will never complete.</summary>
    private async Task FragmentCleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FragmentExpirationMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            long cutoff = Stopwatch.GetTimestamp() - (Stopwatch.Frequency * FragmentExpirationMs / 1000);

            foreach (var kvp in _fragmentBuffers)
            {
                if (kvp.Value.CreatedTicks < cutoff)
                {
                    if (_fragmentBuffers.TryRemove(kvp.Key, out _))
                    {
                        Debug.WriteLine($"[STORM] Evicted stale fragment assembly seq={kvp.Key}.");
                    }
                }
            }
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopListening();
        _fragmentBuffers.Clear();
        _pendingAcks.Clear();
        _pendingPings.Clear();
    }

    // ── Unsafe helper for Interlocked on uint ─────────────────────────

    /// <summary>
    /// Provides <see cref="System.Runtime.CompilerServices.Unsafe"/>
    /// re-export for the <c>Interlocked.Increment</c> pattern on
    /// <see cref="uint"/> fields via <c>Unsafe.As&lt;uint, int&gt;</c>.
    /// </summary>
    private static class Unsafe
    {
        /// <summary>
        /// Reinterprets a managed reference to <typeparamref name="TFrom"/>
        /// as a managed reference to <typeparamref name="TTo"/>.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static ref TTo As<TFrom, TTo>(ref TFrom source)
        {
            return ref System.Runtime.CompilerServices.Unsafe.As<TFrom, TTo>(ref source);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Nested type: FragmentAssembly
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Accumulates fragments for a single base sequence number and
    /// reassembles them into the original payload once all fragments
    /// have arrived.
    /// </summary>
    private sealed class FragmentAssembly
    {
        private readonly byte[]?[] _fragments;
        private readonly int _totalFragments;
        private int _receivedCount;

        /// <summary>The original packet type before fragmentation.</summary>
        public StormPacketType OriginalType { get; }

        /// <summary>Timestamp (Stopwatch ticks) when this assembly was created.</summary>
        public long CreatedTicks { get; } = Stopwatch.GetTimestamp();

        /// <summary>
        /// Gets a value indicating whether all fragments have been received.
        /// </summary>
        public bool IsComplete => _receivedCount >= _totalFragments;

        /// <summary>
        /// Initializes a new fragment assembly expecting <paramref name="totalFragments"/> pieces.
        /// </summary>
        /// <param name="totalFragments">Expected number of fragments.</param>
        /// <param name="originalType">The packet type of the unfragmented payload.</param>
        public FragmentAssembly(int totalFragments, StormPacketType originalType)
        {
            _totalFragments = totalFragments;
            OriginalType = originalType;
            _fragments = new byte[totalFragments][];
        }

        /// <summary>
        /// Stores a fragment. Duplicate indices are silently ignored.
        /// </summary>
        /// <param name="index">The zero-based fragment index.</param>
        /// <param name="data">The fragment payload bytes.</param>
        public void AddFragment(int index, byte[] data)
        {
            if (index < 0 || index >= _totalFragments) return;

            if (_fragments[index] is null)
            {
                _fragments[index] = data;
                Interlocked.Increment(ref _receivedCount);
            }
        }

        /// <summary>
        /// Reassembles all fragments into a single contiguous byte array.
        /// </summary>
        /// <returns>The complete reassembled payload.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown if not all fragments have been received yet.
        /// </exception>
        public byte[] Reassemble()
        {
            if (!IsComplete)
            {
                throw new InvalidOperationException(
                    $"Cannot reassemble: only {_receivedCount}/{_totalFragments} fragments received.");
            }

            int totalSize = 0;
            for (int i = 0; i < _totalFragments; i++)
            {
                totalSize += _fragments[i]!.Length;
            }

            byte[] result = new byte[totalSize];
            int offset = 0;

            for (int i = 0; i < _totalFragments; i++)
            {
                byte[] frag = _fragments[i]!;
                Buffer.BlockCopy(frag, 0, result, offset, frag.Length);
                offset += frag.Length;
            }

            return result;
        }
    }
}
