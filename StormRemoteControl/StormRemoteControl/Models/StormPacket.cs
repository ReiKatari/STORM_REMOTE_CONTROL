// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace StormRemoteControl.Models;

/// <summary>
/// Identifies the type of a STORM protocol packet.
/// Values are grouped by functional channel for routing purposes.
/// </summary>
public enum StormPacketType : byte
{
    // ── Control channel ──────────────────────────────────────────────

    /// <summary>ECDH public-key exchange (initiator → responder).</summary>
    Handshake = 0x01,

    /// <summary>Key-exchange acknowledgment (responder → initiator).</summary>
    HandshakeAck = 0x02,

    /// <summary>Keepalive ping; expects a <see cref="Pong"/> reply.</summary>
    Ping = 0x03,

    /// <summary>Keepalive pong; reply to a <see cref="Ping"/>.</summary>
    Pong = 0x04,

    /// <summary>Graceful session disconnect.</summary>
    Disconnect = 0x05,

    /// <summary>NAT hole-punch probe packet.</summary>
    HolePunch = 0x06,

    /// <summary>Auth challenge: host sends 32-byte random nonce after ECDH.</summary>
    AuthChallenge = 0x07,

    /// <summary>Auth response: client sends HMAC-SHA256(password, nonce).</summary>
    AuthResponse = 0x08,

    /// <summary>Auth result: host sends 1-byte success (0x01) or failure (0x00).</summary>
    AuthResult = 0x09,

    // ── Data channel ─────────────────────────────────────────────────

    /// <summary>Complete encoded video frame (or single-fragment frame).</summary>
    VideoFrame = 0x10,

    /// <summary>Fragment of a large video frame requiring reassembly.</summary>
    VideoFrameFragment = 0x11,

    /// <summary>Audio PCM or encoded audio chunk.</summary>
    AudioData = 0x12,

    // ── Input channel ────────────────────────────────────────────────

    /// <summary>Mouse move, click, or scroll event.</summary>
    MouseInput = 0x20,

    /// <summary>Keyboard key-down or key-up event.</summary>
    KeyboardInput = 0x21,

    /// <summary>System-level hotkey (Ctrl+Alt+Delete, Alt+Tab, etc.).</summary>
    SystemHotkey = 0x22,

    // ── Chat & file ──────────────────────────────────────────────────

    /// <summary>UTF-8 text chat message.</summary>
    ChatMessage = 0x30,

    /// <summary>File transfer offer containing metadata.</summary>
    FileOffer = 0x40,

    /// <summary>Accept a pending file transfer.</summary>
    FileAccept = 0x41,

    /// <summary>Reject a pending file transfer.</summary>
    FileReject = 0x42,

    /// <summary>File data chunk.</summary>
    FileChunk = 0x43,

    /// <summary>Acknowledgment of a successfully received file chunk.</summary>
    FileChunkAck = 0x44,

    /// <summary>File transfer completed successfully.</summary>
    FileComplete = 0x45,

    // ── Clipboard & Monitor ─────────────────────────────────────────

    /// <summary>Clipboard content synchronization (UTF-8 text).</summary>
    ClipboardSync = 0x50,

    /// <summary>Request to switch captured monitor index.</summary>
    MonitorSwitch = 0x51,

    // ── Acknowledgments ──────────────────────────────────────────────

    /// <summary>Generic positive acknowledgment.</summary>
    Ack = 0xF0,

    /// <summary>Negative acknowledgment – requests retransmission.</summary>
    Nack = 0xF1,
}

/// <summary>
/// Fixed-size 32-byte packet header for the STORM wire protocol.
/// All multi-byte fields are serialized in network (big-endian) byte order.
/// </summary>
/// <remarks>
/// <para>Wire layout (offsets in bytes):</para>
/// <list type="table">
///   <item><term>0–1</term><description>Magic (0x5354 = 'ST')</description></item>
///   <item><term>2</term><description>ProtocolVersion</description></item>
///   <item><term>3</term><description>PacketType</description></item>
///   <item><term>4–7</term><description>SequenceNumber</description></item>
///   <item><term>8–15</term><description>Timestamp (Unix ms)</description></item>
///   <item><term>16–19</term><description>PayloadLength</description></item>
///   <item><term>20–21</term><description>FragmentIndex</description></item>
///   <item><term>22–23</term><description>FragmentTotal</description></item>
///   <item><term>24–27</term><description>Checksum (CRC-32 of payload)</description></item>
///   <item><term>28–31</term><description>Reserved (zero-filled)</description></item>
/// </list>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StormPacketHeader
{
    /// <summary>Protocol magic number — always <c>0x5354</c> ('ST').</summary>
    public ushort Magic;

    /// <summary>Protocol version — currently <c>0x01</c>.</summary>
    public byte ProtocolVersion;

    /// <summary>Type of this packet.</summary>
    public StormPacketType PacketType;

    /// <summary>Monotonically increasing sequence number per sender.</summary>
    public uint SequenceNumber;

    /// <summary>Sender timestamp as Unix epoch milliseconds (UTC).</summary>
    public long Timestamp;

    /// <summary>Length in bytes of the (encrypted) payload that follows the header.</summary>
    public uint PayloadLength;

    /// <summary>Zero-based fragment index; <c>0</c> for non-fragmented packets.</summary>
    public ushort FragmentIndex;

    /// <summary>Total number of fragments; <c>1</c> for non-fragmented packets.</summary>
    public ushort FragmentTotal;

    /// <summary>CRC-32 checksum computed over the payload bytes.</summary>
    public uint Checksum;

    /// <summary>Reserved bytes for future use (must be zero).</summary>
    public uint Reserved;
}

/// <summary>
/// Protocol constants, serialization helpers, and packet factory methods for STORM.
/// </summary>
public static class StormProtocol
{
    /// <summary>Protocol magic number — ASCII <c>'ST'</c> = <c>0x5354</c>.</summary>
    public const ushort Magic = 0x5354;

    /// <summary>Current protocol version.</summary>
    public const byte Version = 0x01;

    /// <summary>Fixed header size in bytes (32).</summary>
    public const int HeaderSize = 32;

    /// <summary>
    /// Maximum transmission unit used for STORM packets.
    /// Sized to fit comfortably inside a standard Ethernet frame after
    /// IP (20) and UDP (8) headers, with room to spare.
    /// </summary>
    public const int MaxMTU = 1400;

    /// <summary>Maximum payload bytes per single packet.</summary>
    public const int MaxPayloadPerPacket = MaxMTU - HeaderSize;

    /// <summary>Default UDP port used by STORM.</summary>
    public const int DefaultPort = 17700;

    /// <summary>Interval between keepalive pings in milliseconds.</summary>
    public const int KeepAliveIntervalMs = 5000;

    /// <summary>
    /// Duration in milliseconds after which an unresponsive peer is
    /// considered disconnected.
    /// </summary>
    public const int ConnectionTimeoutMs = 15000;

    /// <summary>
    /// Serializes a <see cref="StormPacketHeader"/> into a 32-byte span
    /// using network (big-endian) byte order.
    /// </summary>
    /// <param name="buffer">
    /// Target span that must be at least <see cref="HeaderSize"/> bytes long.
    /// </param>
    /// <param name="header">The header to serialize.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="HeaderSize"/> bytes.
    /// </exception>
    public static void WriteHeader(Span<byte> buffer, in StormPacketHeader header)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"Buffer must be at least {HeaderSize} bytes, but was {buffer.Length}.",
                nameof(buffer));
        }

        BinaryPrimitives.WriteUInt16BigEndian(buffer[0..], header.Magic);
        buffer[2] = header.ProtocolVersion;
        buffer[3] = (byte)header.PacketType;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], header.SequenceNumber);
        BinaryPrimitives.WriteInt64BigEndian(buffer[8..], header.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[16..], header.PayloadLength);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[20..], header.FragmentIndex);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[22..], header.FragmentTotal);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[24..], header.Checksum);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[28..], header.Reserved);
    }

    /// <summary>
    /// Deserializes a <see cref="StormPacketHeader"/> from a 32-byte span
    /// that was written in network (big-endian) byte order.
    /// </summary>
    /// <param name="buffer">
    /// Source span that must be at least <see cref="HeaderSize"/> bytes long.
    /// </param>
    /// <returns>The deserialized header.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is shorter than <see cref="HeaderSize"/> bytes.
    /// </exception>
    public static StormPacketHeader ReadHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"Buffer must be at least {HeaderSize} bytes, but was {buffer.Length}.",
                nameof(buffer));
        }

        return new StormPacketHeader
        {
            Magic = BinaryPrimitives.ReadUInt16BigEndian(buffer[0..]),
            ProtocolVersion = buffer[2],
            PacketType = (StormPacketType)buffer[3],
            SequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]),
            Timestamp = BinaryPrimitives.ReadInt64BigEndian(buffer[8..]),
            PayloadLength = BinaryPrimitives.ReadUInt32BigEndian(buffer[16..]),
            FragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(buffer[20..]),
            FragmentTotal = BinaryPrimitives.ReadUInt16BigEndian(buffer[22..]),
            Checksum = BinaryPrimitives.ReadUInt32BigEndian(buffer[24..]),
            Reserved = BinaryPrimitives.ReadUInt32BigEndian(buffer[28..]),
        };
    }

    /// <summary>
    /// Builds a complete STORM packet (header + payload) ready for transmission.
    /// </summary>
    /// <param name="type">Packet type identifier.</param>
    /// <param name="sequence">Monotonic sequence number.</param>
    /// <param name="payload">
    /// Payload bytes (typically already encrypted). May be empty.
    /// </param>
    /// <param name="fragIndex">Fragment index (0 for non-fragmented).</param>
    /// <param name="fragTotal">Total fragment count (1 for non-fragmented).</param>
    /// <returns>A byte array containing the serialized header followed by the payload.</returns>
    public static byte[] BuildPacket(
        StormPacketType type,
        uint sequence,
        byte[] payload,
        ushort fragIndex = 0,
        ushort fragTotal = 1)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var header = new StormPacketHeader
        {
            Magic = Magic,
            ProtocolVersion = Version,
            PacketType = type,
            SequenceNumber = sequence,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PayloadLength = (uint)payload.Length,
            FragmentIndex = fragIndex,
            FragmentTotal = fragTotal,
            Checksum = CalculateCrc32(payload),
            Reserved = 0,
        };

        byte[] packet = new byte[HeaderSize + payload.Length];
        WriteHeader(packet.AsSpan(), in header);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>
    /// Computes a CRC-32 checksum over the supplied data using the
    /// ISO 3309 / ITU-T V.42 polynomial (same as used by Ethernet, PKZIP, etc.).
    /// </summary>
    /// <param name="data">The data to checksum.</param>
    /// <returns>The 32-bit CRC value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint CalculateCrc32(ReadOnlySpan<byte> data)
    {
        return Crc32.HashToUInt32(data);
    }

    /// <summary>
    /// Validates the magic number and protocol version of a received header.
    /// </summary>
    /// <param name="header">The header to validate.</param>
    /// <returns>
    /// <see langword="true"/> if the header carries the expected magic number
    /// and a compatible protocol version; otherwise <see langword="false"/>.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ValidateHeader(in StormPacketHeader header)
    {
        return header.Magic == Magic && header.ProtocolVersion == Version;
    }

    /// <summary>
    /// Verifies the CRC-32 checksum stored in a header against the actual payload.
    /// </summary>
    /// <param name="header">The header whose <see cref="StormPacketHeader.Checksum"/> to verify.</param>
    /// <param name="payload">The payload bytes to hash.</param>
    /// <returns>
    /// <see langword="true"/> if the computed CRC-32 matches the header checksum;
    /// otherwise <see langword="false"/>.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool VerifyChecksum(in StormPacketHeader header, ReadOnlySpan<byte> payload)
    {
        return header.Checksum == CalculateCrc32(payload);
    }
}

/// <summary>
/// Metadata describing a file transfer session.
/// </summary>
/// <param name="FileName">Original file name (without path).</param>
/// <param name="FileSize">Total file size in bytes.</param>
/// <param name="Sha256Hash">SHA-256 digest of the complete file (32 bytes).</param>
/// <param name="TransferId">Unique identifier for this transfer session.</param>
public sealed record FileTransferInfo(
    string FileName,
    long FileSize,
    byte[] Sha256Hash,
    Guid TransferId);
