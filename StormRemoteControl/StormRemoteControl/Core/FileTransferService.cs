// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core;

/// <summary>
/// Implements chunked, acknowledged file transfer over <see cref="NetworkTransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// Files are split into 32 KB chunks and sent using a sliding window of 8
/// unacknowledged chunks for throughput. Each chunk is acknowledged individually
/// by the receiver, and the transfer is verified with a SHA-256 digest on
/// completion.
/// </para>
/// <para>
/// <b>Wire formats:</b>
/// </para>
/// <list type="table">
///   <item>
///     <term>FileOffer</term>
///     <description><c>[GUID 16][size 8][SHA256 32][UTF-8 filename N]</c></description>
///   </item>
///   <item>
///     <term>FileChunk</term>
///     <description><c>[GUID 16][chunkIndex 4][data N]</c></description>
///   </item>
///   <item>
///     <term>FileChunkAck</term>
///     <description><c>[GUID 16][chunkIndex 4]</c></description>
///   </item>
///   <item>
///     <term>FileAccept / FileReject / FileComplete</term>
///     <description><c>[GUID 16]</c></description>
///   </item>
/// </list>
/// </remarks>
public sealed class FileTransferService : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a remote peer offers to send a file.
    /// </summary>
    /// <remarks>
    /// Parameters: <c>transferId</c>, <c>fileName</c>, <c>fileSize</c>.
    /// The recipient should call <see cref="AcceptFileOffer"/> or
    /// <see cref="RejectFileOffer"/> in response.
    /// </remarks>
    public event Action<Guid, string, long>? FileOfferReceived;

    /// <summary>
    /// Raised periodically as chunks are sent or received.
    /// </summary>
    /// <remarks>
    /// Parameters: <c>transferId</c>, <c>progress</c> (0.0 – 1.0).
    /// </remarks>
    public event Action<Guid, double>? TransferProgressUpdated;

    /// <summary>
    /// Raised when a transfer finishes.
    /// </summary>
    /// <remarks>
    /// Parameters: <c>transferId</c>, <c>success</c>.
    /// </remarks>
    public event Action<Guid, bool>? TransferCompleted;

    // ── Constants ─────────────────────────────────────────────────────

    /// <summary>Chunk size for file data (32 KB).</summary>
    private const int ChunkSize = 32 * 1024;

    /// <summary>
    /// Maximum number of unacknowledged chunks in flight at once.
    /// A higher window improves throughput at the cost of memory.
    /// </summary>
    private const int SlidingWindowSize = 8;

    /// <summary>Fixed byte-size of a transfer GUID on the wire.</summary>
    private const int GuidSize = 16;

    /// <summary>SHA-256 digest length in bytes.</summary>
    private const int Sha256Size = 32;

    /// <summary>Time to wait for a chunk ACK before retrying, in milliseconds.</summary>
    private const int ChunkAckTimeoutMs = 2000;

    /// <summary>Maximum retransmission attempts per chunk.</summary>
    private const int MaxChunkRetries = 5;

    // ── Fields ────────────────────────────────────────────────────────

    private readonly NetworkTransport _transport;

    /// <summary>Outgoing (sender-side) active transfers.</summary>
    private readonly ConcurrentDictionary<Guid, OutboundTransfer> _outbound = new();

    /// <summary>Inbound (receiver-side) active transfers.</summary>
    private readonly ConcurrentDictionary<Guid, InboundTransfer> _inbound = new();

    /// <summary>Pending offers we have received but not yet accepted/rejected.</summary>
    private readonly ConcurrentDictionary<Guid, FileOfferMetadata> _pendingOffers = new();

    private bool _disposed;

    // ── Constructor ───────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new <see cref="FileTransferService"/> that transmits
    /// file data over the supplied <see cref="NetworkTransport"/>.
    /// </summary>
    /// <param name="transport">The UDP transport to send and receive packets through.</param>
    public FileTransferService(NetworkTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    // ── Public API: sender side ───────────────────────────────────────

    /// <summary>
    /// Initiates sending a file to the remote peer.
    /// </summary>
    /// <param name="filePath">Full path to the file to send.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>
    /// The <see cref="Guid"/> identifying this transfer session. The caller
    /// can monitor progress via <see cref="TransferProgressUpdated"/> and
    /// completion via <see cref="TransferCompleted"/>.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown if <paramref name="filePath"/> does not exist.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the service has been disposed.
    /// </exception>
    public async Task<Guid> SendFileAsync(string filePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The specified file does not exist.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        Guid transferId = Guid.NewGuid();

        // Pre-compute SHA-256 hash of the entire file.
        byte[] hash = await ComputeFileHashAsync(filePath, ct).ConfigureAwait(false);

        int totalChunks = (int)((fileInfo.Length + ChunkSize - 1) / ChunkSize);
        if (totalChunks == 0) totalChunks = 1; // Zero-length file still gets one "empty" chunk.

        var transfer = new OutboundTransfer(
            transferId, filePath, fileInfo.Length, hash, totalChunks);
        _outbound[transferId] = transfer;

        // Send the offer packet.
        byte[] offerPayload = BuildFileOfferPayload(
            transferId, fileInfo.Length, hash, fileInfo.Name);

        await _transport.SendAsync(StormPacketType.FileOffer, offerPayload, reliable: true)
            .ConfigureAwait(false);

        // Actual chunk sending begins when we receive a FileAccept.
        return transferId;
    }

    // ── Public API: receiver side ─────────────────────────────────────

    /// <summary>
    /// Accepts an incoming file offer and begins receiving chunks.
    /// </summary>
    /// <param name="transferId">The transfer ID from <see cref="FileOfferReceived"/>.</param>
    /// <param name="savePath">Full local path to save the received file.</param>
    /// <exception cref="ArgumentException">
    /// Thrown if the <paramref name="transferId"/> does not correspond to
    /// a pending offer.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the service has been disposed.
    /// </exception>
    public void AcceptFileOffer(Guid transferId, string savePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);

        if (!_pendingOffers.TryRemove(transferId, out FileOfferMetadata? offer))
        {
            throw new ArgumentException(
                $"No pending file offer with ID {transferId}.", nameof(transferId));
        }

        int totalChunks = (int)((offer.FileSize + ChunkSize - 1) / ChunkSize);
        if (totalChunks == 0) totalChunks = 1;

        var inbound = new InboundTransfer(
            transferId, savePath, offer.FileSize, offer.Sha256Hash, totalChunks);
        _inbound[transferId] = inbound;

        // Send accept notification.
        byte[] payload = transferId.ToByteArray();
        _ = _transport.SendAsync(StormPacketType.FileAccept, payload, reliable: true);
    }

    /// <summary>
    /// Rejects an incoming file offer.
    /// </summary>
    /// <param name="transferId">The transfer ID from <see cref="FileOfferReceived"/>.</param>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the service has been disposed.
    /// </exception>
    public void RejectFileOffer(Guid transferId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pendingOffers.TryRemove(transferId, out _);

        byte[] payload = transferId.ToByteArray();
        _ = _transport.SendAsync(StormPacketType.FileReject, payload, reliable: true);
    }

    /// <summary>
    /// Cancels an active transfer (either sending or receiving).
    /// </summary>
    /// <param name="transferId">The transfer to cancel.</param>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the service has been disposed.
    /// </exception>
    public void CancelTransfer(Guid transferId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_outbound.TryRemove(transferId, out OutboundTransfer? outbound))
        {
            outbound.Cancel();
            TransferCompleted?.Invoke(transferId, false);
        }

        if (_inbound.TryRemove(transferId, out InboundTransfer? inbound))
        {
            inbound.Dispose();
            TransferCompleted?.Invoke(transferId, false);
        }

        _pendingOffers.TryRemove(transferId, out _);
    }

    // ── Packet processing (called by RemoteSessionService) ────────────

    /// <summary>
    /// Dispatches an incoming file-related packet to the appropriate handler.
    /// This method should be called by the session orchestrator for every
    /// packet whose <see cref="StormPacketHeader.PacketType"/> falls in the
    /// <c>0x40–0x45</c> range.
    /// </summary>
    /// <param name="header">The parsed packet header.</param>
    /// <param name="payload">The decrypted payload bytes.</param>
    public void ProcessPacket(StormPacketHeader header, byte[] payload)
    {
        if (_disposed) return;

        switch (header.PacketType)
        {
            case StormPacketType.FileOffer:
                HandleFileOffer(payload);
                break;

            case StormPacketType.FileAccept:
                HandleFileAccept(payload);
                break;

            case StormPacketType.FileReject:
                HandleFileReject(payload);
                break;

            case StormPacketType.FileChunk:
                HandleFileChunk(payload);
                break;

            case StormPacketType.FileChunkAck:
                HandleFileChunkAck(payload);
                break;

            case StormPacketType.FileComplete:
                HandleFileComplete(payload);
                break;

            default:
                Debug.WriteLine($"[STORM-FILE] Unhandled packet type: {header.PacketType}");
                break;
        }
    }

    // ── Handlers: offer flow ──────────────────────────────────────────

    /// <summary>Parses a FileOffer payload and raises <see cref="FileOfferReceived"/>.</summary>
    private void HandleFileOffer(byte[] payload)
    {
        // [GUID 16][size 8][SHA256 32][UTF-8 filename N]
        int minLength = GuidSize + 8 + Sha256Size;
        if (payload.Length < minLength)
        {
            Debug.WriteLine("[STORM-FILE] FileOffer payload too short.");
            return;
        }

        Guid transferId = new(payload.AsSpan(0, GuidSize));
        long fileSize = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(GuidSize));
        byte[] sha256 = new byte[Sha256Size];
        Buffer.BlockCopy(payload, GuidSize + 8, sha256, 0, Sha256Size);
        string fileName = Encoding.UTF8.GetString(
            payload, GuidSize + 8 + Sha256Size, payload.Length - minLength);

        var offer = new FileOfferMetadata(transferId, fileName, fileSize, sha256);
        _pendingOffers[transferId] = offer;

        FileOfferReceived?.Invoke(transferId, fileName, fileSize);
    }

    /// <summary>The remote peer accepted our offer — begin sending chunks.</summary>
    private void HandleFileAccept(byte[] payload)
    {
        if (payload.Length < GuidSize) return;

        Guid transferId = new(payload.AsSpan(0, GuidSize));
        if (_outbound.TryGetValue(transferId, out OutboundTransfer? transfer))
        {
            _ = Task.Run(() => SendChunksAsync(transfer));
        }
    }

    /// <summary>The remote peer rejected our offer.</summary>
    private void HandleFileReject(byte[] payload)
    {
        if (payload.Length < GuidSize) return;

        Guid transferId = new(payload.AsSpan(0, GuidSize));
        if (_outbound.TryRemove(transferId, out _))
        {
            TransferCompleted?.Invoke(transferId, false);
        }
    }

    // ── Handlers: chunk flow ──────────────────────────────────────────

    /// <summary>Receives a file data chunk and writes it to disk.</summary>
    private void HandleFileChunk(byte[] payload)
    {
        // [GUID 16][chunkIndex 4][data N]
        int minLength = GuidSize + 4;
        if (payload.Length < minLength) return;

        Guid transferId = new(payload.AsSpan(0, GuidSize));
        int chunkIndex = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(GuidSize));
        int dataOffset = GuidSize + 4;
        int dataLength = payload.Length - dataOffset;

        if (!_inbound.TryGetValue(transferId, out InboundTransfer? transfer)) return;

        // Write chunk to file at the correct offset.
        byte[] chunkData = new byte[dataLength];
        Buffer.BlockCopy(payload, dataOffset, chunkData, 0, dataLength);

        transfer.WriteChunk(chunkIndex, chunkData);

        // Send ACK.
        byte[] ackPayload = BuildChunkAckPayload(transferId, chunkIndex);
        _ = _transport.SendAsync(StormPacketType.FileChunkAck, ackPayload, reliable: false);

        // Fire progress.
        double progress = (double)(chunkIndex + 1) / transfer.TotalChunks;
        TransferProgressUpdated?.Invoke(transferId, Math.Min(progress, 1.0));
    }

    /// <summary>Marks a sent chunk as acknowledged.</summary>
    private void HandleFileChunkAck(byte[] payload)
    {
        if (payload.Length < GuidSize + 4) return;

        Guid transferId = new(payload.AsSpan(0, GuidSize));
        int chunkIndex = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(GuidSize));

        if (_outbound.TryGetValue(transferId, out OutboundTransfer? transfer))
        {
            transfer.AcknowledgeChunk(chunkIndex);
        }
    }

    /// <summary>Handles the FileComplete packet on the receiver side.</summary>
    private void HandleFileComplete(byte[] payload)
    {
        if (payload.Length < GuidSize) return;

        Guid transferId = new(payload.AsSpan(0, GuidSize));
        if (!_inbound.TryRemove(transferId, out InboundTransfer? transfer)) return;

        // Verify SHA-256.
        bool valid = transfer.VerifyHash();
        transfer.Dispose();

        TransferCompleted?.Invoke(transferId, valid);
    }

    // ── Sender: sliding-window chunk pump ─────────────────────────────

    /// <summary>
    /// Reads the source file and transmits chunks using a sliding window
    /// of <see cref="SlidingWindowSize"/> unacknowledged chunks.
    /// </summary>
    private async Task SendChunksAsync(OutboundTransfer transfer)
    {
        try
        {
            using FileStream fs = new(
                transfer.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: ChunkSize,
                useAsync: true);

            byte[] readBuffer = new byte[ChunkSize];
            int chunkIndex = 0;

            while (chunkIndex < transfer.TotalChunks && !transfer.IsCancelled)
            {
                // Throttle: wait until the window has room.
                while (transfer.InFlightCount >= SlidingWindowSize && !transfer.IsCancelled)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }

                if (transfer.IsCancelled) break;

                // Read the next chunk from disk.
                long fileOffset = (long)chunkIndex * ChunkSize;
                fs.Position = fileOffset;
                int bytesRead = await fs.ReadAsync(
                    readBuffer.AsMemory(0, ChunkSize),
                    transfer.CancellationToken).ConfigureAwait(false);

                if (bytesRead == 0) break;

                byte[] chunkData = new byte[bytesRead];
                Buffer.BlockCopy(readBuffer, 0, chunkData, 0, bytesRead);

                byte[] chunkPayload = BuildFileChunkPayload(
                    transfer.TransferId, chunkIndex, chunkData);

                transfer.MarkInFlight(chunkIndex);

                await _transport.SendAsync(StormPacketType.FileChunk, chunkPayload, reliable: false)
                    .ConfigureAwait(false);

                // Fire progress.
                double progress = (double)(chunkIndex + 1) / transfer.TotalChunks;
                TransferProgressUpdated?.Invoke(transfer.TransferId, Math.Min(progress, 1.0));

                chunkIndex++;
            }

            if (transfer.IsCancelled) return;

            // Wait for all remaining ACKs.
            int waitAttempts = 0;
            while (transfer.InFlightCount > 0 && waitAttempts < 300) // 30 seconds max
            {
                await Task.Delay(100).ConfigureAwait(false);
                waitAttempts++;
            }

            // Send completion.
            byte[] completePayload = transfer.TransferId.ToByteArray();
            await _transport.SendAsync(StormPacketType.FileComplete, completePayload, reliable: true)
                .ConfigureAwait(false);

            _outbound.TryRemove(transfer.TransferId, out _);
            TransferCompleted?.Invoke(transfer.TransferId, true);
        }
        catch (OperationCanceledException)
        {
            _outbound.TryRemove(transfer.TransferId, out _);
            TransferCompleted?.Invoke(transfer.TransferId, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STORM-FILE] SendChunksAsync error: {ex.Message}");
            _outbound.TryRemove(transfer.TransferId, out _);
            TransferCompleted?.Invoke(transfer.TransferId, false);
        }
    }

    // ── Payload builders ──────────────────────────────────────────────

    /// <summary>Builds a FileOffer payload.</summary>
    private static byte[] BuildFileOfferPayload(
        Guid transferId, long fileSize, byte[] sha256Hash, string fileName)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);
        byte[] payload = new byte[GuidSize + 8 + Sha256Size + nameBytes.Length];

        transferId.ToByteArray().CopyTo(payload, 0);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(GuidSize), fileSize);
        sha256Hash.CopyTo(payload.AsSpan(GuidSize + 8));
        nameBytes.CopyTo(payload.AsSpan(GuidSize + 8 + Sha256Size));

        return payload;
    }

    /// <summary>Builds a FileChunk payload.</summary>
    private static byte[] BuildFileChunkPayload(Guid transferId, int chunkIndex, byte[] data)
    {
        byte[] payload = new byte[GuidSize + 4 + data.Length];
        transferId.ToByteArray().CopyTo(payload, 0);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(GuidSize), chunkIndex);
        data.CopyTo(payload.AsSpan(GuidSize + 4));
        return payload;
    }

    /// <summary>Builds a FileChunkAck payload.</summary>
    private static byte[] BuildChunkAckPayload(Guid transferId, int chunkIndex)
    {
        byte[] payload = new byte[GuidSize + 4];
        transferId.ToByteArray().CopyTo(payload, 0);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(GuidSize), chunkIndex);
        return payload;
    }

    // ── Hashing helper ────────────────────────────────────────────────

    /// <summary>Computes the SHA-256 hash of a file asynchronously.</summary>
    private static async Task<byte[]> ComputeFileHashAsync(
        string filePath, CancellationToken ct)
    {
        using FileStream fs = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
    }

    // ── Dispose ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _outbound)
        {
            kvp.Value.Cancel();
        }
        _outbound.Clear();

        foreach (var kvp in _inbound)
        {
            kvp.Value.Dispose();
        }
        _inbound.Clear();

        _pendingOffers.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Nested types
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Metadata for a received but not-yet-accepted file offer.</summary>
    private sealed record FileOfferMetadata(
        Guid TransferId,
        string FileName,
        long FileSize,
        byte[] Sha256Hash);

    /// <summary>Tracks the state of an outgoing (sender-side) file transfer.</summary>
    private sealed class OutboundTransfer
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<int, bool> _inFlightChunks = new();

        /// <summary>Unique transfer identifier.</summary>
        public Guid TransferId { get; }

        /// <summary>Full local path of the source file.</summary>
        public string FilePath { get; }

        /// <summary>Total file size in bytes.</summary>
        public long FileSize { get; }

        /// <summary>SHA-256 hash of the complete file.</summary>
        public byte[] Sha256Hash { get; }

        /// <summary>Total number of chunks.</summary>
        public int TotalChunks { get; }

        /// <summary>Whether the transfer has been cancelled.</summary>
        public bool IsCancelled => _cts.IsCancellationRequested;

        /// <summary>Cancellation token for cooperative cancellation.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        /// <summary>Current number of unacknowledged in-flight chunks.</summary>
        public int InFlightCount => _inFlightChunks.Count;

        public OutboundTransfer(
            Guid transferId, string filePath, long fileSize, byte[] sha256Hash, int totalChunks)
        {
            TransferId = transferId;
            FilePath = filePath;
            FileSize = fileSize;
            Sha256Hash = sha256Hash;
            TotalChunks = totalChunks;
        }

        /// <summary>Records a chunk as in-flight (sent, awaiting ACK).</summary>
        public void MarkInFlight(int chunkIndex) => _inFlightChunks[chunkIndex] = true;

        /// <summary>Removes a chunk from the in-flight set upon ACK receipt.</summary>
        public void AcknowledgeChunk(int chunkIndex) => _inFlightChunks.TryRemove(chunkIndex, out _);

        /// <summary>Cancels the transfer.</summary>
        public void Cancel() => _cts.Cancel();
    }

    /// <summary>Tracks the state of an incoming (receiver-side) file transfer.</summary>
    private sealed class InboundTransfer : IDisposable
    {
        private readonly FileStream _fileStream;
        private readonly byte[] _expectedHash;
        private readonly object _writeLock = new();

        /// <summary>Unique transfer identifier.</summary>
        public Guid TransferId { get; }

        /// <summary>Full local path where the file is being saved.</summary>
        public string SavePath { get; }

        /// <summary>Total expected file size in bytes.</summary>
        public long FileSize { get; }

        /// <summary>Total number of expected chunks.</summary>
        public int TotalChunks { get; }

        public InboundTransfer(
            Guid transferId, string savePath, long fileSize, byte[] expectedHash, int totalChunks)
        {
            TransferId = transferId;
            SavePath = savePath;
            FileSize = fileSize;
            TotalChunks = totalChunks;
            _expectedHash = expectedHash;

            // Ensure directory exists.
            string? directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileStream = new FileStream(
                savePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: ChunkSize,
                useAsync: false); // Synchronous writes under lock for correctness.

            // Pre-allocate file size for sparse random writes.
            if (fileSize > 0)
            {
                _fileStream.SetLength(fileSize);
            }
        }

        /// <summary>
        /// Writes a chunk's data to the correct file offset.
        /// Thread-safe; may be called from any thread.
        /// </summary>
        /// <param name="chunkIndex">Zero-based chunk index.</param>
        /// <param name="data">The chunk payload bytes.</param>
        public void WriteChunk(int chunkIndex, byte[] data)
        {
            long offset = (long)chunkIndex * ChunkSize;

            lock (_writeLock)
            {
                _fileStream.Position = offset;
                _fileStream.Write(data, 0, data.Length);
                _fileStream.Flush();
            }
        }

        /// <summary>
        /// Verifies the SHA-256 hash of the completed file against the
        /// expected hash from the offer.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the hashes match; otherwise
        /// <see langword="false"/>.
        /// </returns>
        public bool VerifyHash()
        {
            lock (_writeLock)
            {
                _fileStream.Flush();
                _fileStream.Position = 0;

                byte[] actualHash = SHA256.HashData(_fileStream);
                return CryptographicOperations.FixedTimeEquals(actualHash, _expectedHash);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _fileStream.Dispose();
        }
    }
}
