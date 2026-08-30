using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Orchestrates P2P connection establishment including STUN discovery,
    /// UDP hole punching, ECDH key exchange, and keepalive management.
    /// Supports both direct IP mode (LAN/port-forwarded) and NAT traversal mode.
    /// </summary>
    public sealed class ConnectionManager : IDisposable
    {
        private readonly CryptoEngine _crypto;
        private readonly NetworkTransport _transport;
        private CancellationTokenSource? _connectionCts;
        private CancellationTokenSource? _keepaliveCts;

        private IPEndPoint? _publicEndpoint;
        private ConnectionMode _mode = ConnectionMode.Direct;
        private ConnectionState _state = ConnectionState.Idle;
        private byte[]? _authNonce;
        private TaskCompletionSource<bool>? _authTcs;
        private long _lastPongTicks;

        /// <summary>Fires when connection state changes.</summary>
        public event Action<ConnectionState>? StateChanged;

        /// <summary>Fires when a remote peer successfully connects and keys are exchanged.</summary>
        public event Action<IPEndPoint>? PeerConnected;

        /// <summary>Fires when the peer disconnects or connection times out.</summary>
        public event Action<string>? PeerDisconnected;

        /// <summary>Fires when our public endpoint is discovered via STUN.</summary>
        public event Action<IPEndPoint>? PublicEndpointDiscovered;

        /// <summary>Current connection state.</summary>
        public ConnectionState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    StateChanged?.Invoke(_state);
                }
            }
        }

        /// <summary>Our discovered public IP:port (null until STUN completes).</summary>
        public IPEndPoint? PublicEndpoint => _publicEndpoint;

        /// <summary>The connected peer endpoint.</summary>
        public IPEndPoint? ConnectedPeer => _transport.RemotePeer;

        /// <summary>Whether we have an active encrypted session.</summary>
        public bool IsConnected => State == ConnectionState.Connected && _crypto.IsSessionEstablished;

        /// <summary>Whether we are the host of the session.</summary>
        public bool IsHost { get; private set; }

        public ConnectionManager(CryptoEngine crypto, NetworkTransport transport)
        {
            _crypto = crypto;
            _transport = transport;

            // Subscribe to incoming packets for handshake processing
            _transport.PacketReceived += OnPacketReceived;
            _transport.PeerDisconnected += OnTransportPeerDisconnected;
        }

        /// <summary>
        /// Discover our public IP:port via STUN and start listening for incoming connections.
        /// Called when the app starts in Host mode.
        /// </summary>
        public async Task<bool> InitializeHostAsync(int listenPort = StormProtocol.DefaultPort, CancellationToken ct = default)
        {
            try
            {
                State = ConnectionState.Initializing;

                // Start listening on the UDP port
                _transport.StartListening(listenPort);

                IsHost = true;

                // Discover our public endpoint via STUN (blocking for Host init)
                try
                {
                    var result = await StunClient.DiscoverPublicEndpointAsync(ct: ct);
                    if (result.HasValue)
                    {
                        _publicEndpoint = new IPEndPoint(result.Value.PublicIP, result.Value.PublicPort);
                        PublicEndpointDiscovered?.Invoke(_publicEndpoint);
                        Debug.WriteLine($"[STORM] Public endpoint: {_publicEndpoint}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM] STUN discovery failed: {ex.Message}");
                }

                State = ConnectionState.WaitingForPeer;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Host init failed: {ex.Message}");
                State = ConnectionState.Failed;
                return false;
            }
        }

        /// <summary>
        /// Connect to a remote host by direct IP:port (LAN or port-forwarded).
        /// </summary>
        public async Task<bool> ConnectDirectAsync(string ipAddress, int port = StormProtocol.DefaultPort, CancellationToken ct = default)
        {
            if (State == ConnectionState.Connected) return true;

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _mode = ConnectionMode.Direct;

            try
            {
                State = ConnectionState.Connecting;

                // Ensure we're listening (to receive responses)
                if (!_transport.IsListening)
                {
                    _transport.StartListening(0); // OS-assigned port
                }

                var peerEndpoint = new IPEndPoint(IPAddress.Parse(ipAddress), port);
                _transport.SetRemotePeer(peerEndpoint);

                // Initiate ECDH key exchange
                return await PerformHandshakeAsync(peerEndpoint, _connectionCts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Direct connect failed: {ex.Message}");
                State = ConnectionState.Failed;
                return false;
            }
        }

        /// <summary>
        /// Connect to a remote host using NAT traversal (STUN + hole punching).
        /// Both peers must call this with each other's public endpoints.
        /// </summary>
        public async Task<bool> ConnectWithHolePunchAsync(
            IPEndPoint peerPublicEndpoint,
            CancellationToken ct = default)
        {
            if (State == ConnectionState.Connected) return true;

            _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _mode = ConnectionMode.HolePunch;

            try
            {
                State = ConnectionState.HolePunching;

                // Ensure we're listening
                if (!_transport.IsListening)
                {
                    _transport.StartListening(0);
                }

                // Phase 1: UDP Hole Punching — send probe packets to peer's public endpoint
                // Both peers do this simultaneously to open NAT mappings
                Debug.WriteLine($"[STORM] Starting hole punch to {peerPublicEndpoint}");

                bool punchSucceeded = false;
                var punchTimeout = TimeSpan.FromSeconds(15);
                var punchSw = Stopwatch.StartNew();

                // Send hole punch probes every 100ms for up to 15 seconds
                while (punchSw.Elapsed < punchTimeout && !_connectionCts.Token.IsCancellationRequested)
                {
                    // Build hole punch probe packet
                    byte[] probePayload = BuildHolePunchProbe();
                    var probePacket = StormProtocol.BuildPacket(
                        StormPacketType.HolePunch, 0, probePayload);

                    await _transport.SendRawAsync(probePacket, peerPublicEndpoint);
                    await Task.Delay(100, _connectionCts.Token);

                    // Check if we received a probe from the peer (handled in OnPacketReceived)
                    if (State == ConnectionState.Authenticating)
                    {
                        punchSucceeded = true;
                        break;
                    }
                }

                if (!punchSucceeded)
                {
                    Debug.WriteLine("[STORM] Hole punch failed — no response from peer");
                    State = ConnectionState.Failed;
                    return false;
                }

                // Phase 2: ECDH Key Exchange
                _transport.SetRemotePeer(peerPublicEndpoint);
                return await PerformHandshakeAsync(peerPublicEndpoint, _connectionCts.Token);
            }
            catch (OperationCanceledException)
            {
                State = ConnectionState.Idle;
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Hole punch connect failed: {ex.Message}");
                State = ConnectionState.Failed;
                return false;
            }
        }

        /// <summary>
        /// Disconnect from the current peer gracefully.
        /// </summary>
        public void Disconnect()
        {
            _keepaliveCts?.Cancel();
            _connectionCts?.Cancel();

            if (State == ConnectionState.Connected && _transport.RemotePeer != null)
            {
                try
                {
                    // Send disconnect notification (best-effort, don't await)
                    var disconnectPacket = StormProtocol.BuildPacket(
                        StormPacketType.Disconnect, 0, Array.Empty<byte>());
                    _ = _transport.SendRawAsync(disconnectPacket, _transport.RemotePeer);
                }
                catch { /* Best effort */ }
            }

            _transport.StopListening();
            State = ConnectionState.Idle;
        }

        /// <summary>
        /// Perform ECDH key exchange with the peer.
        /// </summary>
        private async Task<bool> PerformHandshakeAsync(IPEndPoint peerEndpoint, CancellationToken ct)
        {
            State = ConnectionState.Authenticating;

            try
            {
                byte[] ourPublicKey = _crypto.GetPublicKey();
                var handshakePacket = StormProtocol.BuildPacket(
                    StormPacketType.Handshake, 0, ourPublicKey);

                // Retry up to 3 times, waiting 5 seconds each attempt
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    ct.ThrowIfCancellationRequested();

                    await _transport.SendRawAsync(handshakePacket, peerEndpoint);
                    Debug.WriteLine($"[STORM] Sent handshake attempt {attempt}/3 ({ourPublicKey.Length} bytes public key)");

                    using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptTimeout.Token);

                    try
                    {
                        while (!_crypto.IsSessionEstablished && !linked.Token.IsCancellationRequested)
                        {
                            await Task.Delay(100, linked.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Attempt timeout — will retry if attempts remain
                    }

                    if (_crypto.IsSessionEstablished)
                    {
                        State = ConnectionState.Connected;

                        // Send acknowledgment
                        var ackPacket = StormProtocol.BuildPacket(
                            StormPacketType.HandshakeAck, 0, Encoding.UTF8.GetBytes("STORM-OK"));
                        await _transport.SendRawAsync(ackPacket, peerEndpoint);

                        PeerConnected?.Invoke(peerEndpoint);

                        // Start keepalive loop
                        StartKeepalive();

                        Debug.WriteLine("[STORM] Connection established — session encrypted");
                        return true;
                    }

                    Debug.WriteLine($"[STORM] Handshake attempt {attempt}/3 timed out");
                }

                Debug.WriteLine("[STORM] Handshake failed after 3 attempts");
                State = ConnectionState.Failed;
                return false;
            }
            catch (OperationCanceledException)
            {
                State = ConnectionState.Failed;
                return false;
            }
        }

        /// <summary>
        /// Process incoming packets for connection management.
        /// </summary>
        private void OnPacketReceived(StormPacketHeader header, byte[] payload, IPEndPoint sender)
        {
            switch (header.PacketType)
            {
                case StormPacketType.HolePunch:
                    HandleHolePunchProbe(payload, sender);
                    break;

                case StormPacketType.Handshake:
                    HandleHandshake(payload, sender);
                    break;

                case StormPacketType.HandshakeAck:
                    HandleHandshakeAck(payload, sender);
                    break;

                case StormPacketType.Ping:
                    HandlePing(header, sender);
                    break;

                case StormPacketType.Pong:
                    HandlePong(header);
                    break;

                case StormPacketType.Disconnect:
                    HandleDisconnect(sender);
                    break;

                case StormPacketType.AuthChallenge:
                    HandleAuthChallenge(payload, sender);
                    break;

                case StormPacketType.AuthResponse:
                    HandleAuthResponse(payload, sender);
                    break;

                case StormPacketType.AuthResult:
                    HandleAuthResult(payload);
                    break;
            }
        }

        private void HandleHolePunchProbe(byte[] payload, IPEndPoint sender)
        {
            Debug.WriteLine($"[STORM] Received hole punch probe from {sender}");

            // If we're waiting for a peer or actively hole-punching, this means the NAT is traversed
            if (State == ConnectionState.WaitingForPeer || State == ConnectionState.HolePunching)
            {
                State = ConnectionState.Authenticating;
                _transport.SetRemotePeer(sender);
            }
        }

        private void HandleHandshake(byte[] peerPublicKey, IPEndPoint sender)
        {
            Debug.WriteLine($"[STORM] Received handshake from {sender} ({peerPublicKey.Length} bytes)");

            try
            {
                // Derive session key from peer's ECDH public key
                _crypto.DeriveSessionKey(peerPublicKey);

                // Set peer as connected
                _transport.SetRemotePeer(sender);

                if (State == ConnectionState.WaitingForPeer)
                {
                    // We're the host — respond with our public key
                    State = ConnectionState.Authenticating;
                    byte[] ourPublicKey = _crypto.GetPublicKey();
                    var response = StormProtocol.BuildPacket(
                        StormPacketType.Handshake, 0, ourPublicKey);
                    _ = _transport.SendRawAsync(response, sender);
                }

                // If session is now established, perform auth or connect
                if (_crypto.IsSessionEstablished)
                {
                    string password = AppSettings.ConnectionPassword;
                    if (!string.IsNullOrEmpty(password) && State == ConnectionState.Authenticating)
                    {
                        // Host: send auth challenge
                        _authNonce = RandomNumberGenerator.GetBytes(32);
                        var challengePacket = StormProtocol.BuildPacket(
                            StormPacketType.AuthChallenge, 0, _authNonce);
                        _ = _transport.SendRawAsync(challengePacket, sender);
                        Debug.WriteLine("[STORM] Sent auth challenge");
                    }
                    else
                    {
                        // No password — connect immediately
                        State = ConnectionState.Connected;
                        PeerConnected?.Invoke(sender);
                        StartKeepalive();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Handshake processing failed: {ex.Message}");
            }
        }

        private void HandleHandshakeAck(byte[] payload, IPEndPoint sender)
        {
            Debug.WriteLine($"[STORM] Handshake ACK from {sender}");
        }

        // ═══════════════════════════════════════════════════════════
        //  AUTHENTICATION PROTOCOL
        // ═══════════════════════════════════════════════════════════

        /// <summary>Client: received challenge nonce from host. Compute HMAC and respond.</summary>
        private void HandleAuthChallenge(byte[] nonce, IPEndPoint sender)
        {
            Debug.WriteLine("[STORM] Received auth challenge");
            string password = AppSettings.ConnectionPassword;

            byte[] hmac = ComputeAuthHmac(password, nonce);
            var responsePacket = StormProtocol.BuildPacket(
                StormPacketType.AuthResponse, 0, hmac);
            _ = _transport.SendRawAsync(responsePacket, sender);
            Debug.WriteLine("[STORM] Sent auth response");
        }

        /// <summary>Host: received HMAC from client. Verify and send result.</summary>
        private void HandleAuthResponse(byte[] clientHmac, IPEndPoint sender)
        {
            Debug.WriteLine("[STORM] Received auth response");
            string password = AppSettings.ConnectionPassword;

            byte[] expectedHmac = ComputeAuthHmac(password, _authNonce ?? Array.Empty<byte>());
            bool success = CryptographicOperations.FixedTimeEquals(clientHmac, expectedHmac);

            // Send result
            byte[] result = new byte[] { success ? (byte)0x01 : (byte)0x00 };
            var resultPacket = StormProtocol.BuildPacket(
                StormPacketType.AuthResult, 0, result);
            _ = _transport.SendRawAsync(resultPacket, sender);

            if (success)
            {
                Debug.WriteLine("[STORM] Auth SUCCESS — peer authenticated");
                State = ConnectionState.Connected;
                PeerConnected?.Invoke(sender);
                StartKeepalive();
            }
            else
            {
                Debug.WriteLine("[STORM] Auth FAILED — wrong password");
                PeerDisconnected?.Invoke("Неверный пароль");
                Disconnect();
            }
        }

        /// <summary>Client: received auth result from host.</summary>
        private void HandleAuthResult(byte[] payload)
        {
            bool success = payload.Length > 0 && payload[0] == 0x01;
            Debug.WriteLine($"[STORM] Auth result: {(success ? "SUCCESS" : "FAILED")}");
            _authTcs?.TrySetResult(success);

            if (success)
            {
                State = ConnectionState.Connected;
                if (_transport.RemotePeer != null)
                    PeerConnected?.Invoke(_transport.RemotePeer);
                StartKeepalive();
            }
            else
            {
                PeerDisconnected?.Invoke("Неверный пароль");
                Disconnect();
            }
        }

        /// <summary>Compute HMAC-SHA256(password, nonce).</summary>
        private static byte[] ComputeAuthHmac(string password, byte[] nonce)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(password ?? "");
            using var hmac = new HMACSHA256(keyBytes);
            return hmac.ComputeHash(nonce);
        }

        private void HandlePing(StormPacketHeader header, IPEndPoint sender)
        {
            // Respond with Pong using the same timestamp
            byte[] timestampBytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(timestampBytes, header.Timestamp);

            var pong = StormProtocol.BuildPacket(StormPacketType.Pong, 0, timestampBytes);
            _ = _transport.SendRawAsync(pong, sender);
        }

        private void HandlePong(StormPacketHeader header)
        {
            _lastPongTicks = Stopwatch.GetTimestamp();
            Debug.WriteLine($"[STORM] Pong received — latency: {_transport.CurrentLatencyMs:F1}ms");
        }

        private void HandleDisconnect(IPEndPoint sender)
        {
            Debug.WriteLine($"[STORM] Peer {sender} disconnected");
            _keepaliveCts?.Cancel();
            State = ConnectionState.Idle;
            PeerDisconnected?.Invoke("Партнер завершил сессию.");
        }

        private void OnTransportPeerDisconnected()
        {
            _keepaliveCts?.Cancel();
            State = ConnectionState.Idle;
            PeerDisconnected?.Invoke("Соединение потеряно (таймаут).");
        }

        /// <summary>
        /// Build a hole-punch probe payload containing our identity.
        /// </summary>
        private byte[] BuildHolePunchProbe()
        {
            // Simple probe: "STORM-PUNCH" + local port (4 bytes)
            byte[] probe = new byte[15];
            Encoding.ASCII.GetBytes("STORM-PUNCH").CopyTo(probe, 0);
            BinaryPrimitives.WriteInt32BigEndian(probe.AsSpan(11), _transport.LocalPort);
            return probe;
        }

        /// <summary>
        /// Start periodic keepalive pings to maintain NAT mapping and detect disconnection.
        /// </summary>
        private void StartKeepalive()
        {
            _keepaliveCts?.Cancel();
            _keepaliveCts = new CancellationTokenSource();
            var token = _keepaliveCts.Token;
            _lastPongTicks = Stopwatch.GetTimestamp();

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && State == ConnectionState.Connected)
                {
                    try
                    {
                        await Task.Delay(StormProtocol.KeepAliveIntervalMs, token);
                        await _transport.SendPingAsync();

                        // Check elapsed time since last pong received
                        long elapsed = Stopwatch.GetTimestamp() - _lastPongTicks;
                        double elapsedMs = (elapsed * 1000.0) / Stopwatch.Frequency;

                        if (elapsedMs > StormProtocol.ConnectionTimeoutMs)
                        {
                            Debug.WriteLine($"[STORM] Connection timeout — no pong for {elapsedMs:F0}ms");
                            State = ConnectionState.Idle;
                            PeerDisconnected?.Invoke("Соединение потеряно (таймаут keepalive).");
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        public void Dispose()
        {
            Disconnect();
            _transport.PacketReceived -= OnPacketReceived;
            _transport.PeerDisconnected -= OnTransportPeerDisconnected;
            _connectionCts?.Dispose();
            _keepaliveCts?.Dispose();
        }
    }

    /// <summary>Connection mode.</summary>
    public enum ConnectionMode
    {
        /// <summary>Direct IP:port connection (LAN or port-forwarded).</summary>
        Direct,

        /// <summary>NAT traversal via STUN + UDP hole punching.</summary>
        HolePunch
    }

    /// <summary>Connection lifecycle state.</summary>
    public enum ConnectionState
    {
        /// <summary>No connection active.</summary>
        Idle,

        /// <summary>Initializing host mode (starting listener, STUN query).</summary>
        Initializing,

        /// <summary>Host is ready and waiting for incoming connections.</summary>
        WaitingForPeer,

        /// <summary>Attempting to connect to a remote peer.</summary>
        Connecting,

        /// <summary>UDP hole punching in progress.</summary>
        HolePunching,

        /// <summary>Performing ECDH key exchange.</summary>
        Authenticating,

        /// <summary>Fully connected with encrypted session.</summary>
        Connected,

        /// <summary>Connection attempt failed.</summary>
        Failed
    }
}
