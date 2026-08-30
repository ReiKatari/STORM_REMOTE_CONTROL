// =============================================================================
// StunClient.cs
//
// RFC 5389 STUN (Session Traversal Utilities for NAT) client for discovering
// the public-facing IP and port of a UDP socket behind NAT.
// =============================================================================

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Lightweight STUN client implementing the Binding Request flow from RFC 5389.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sends a STUN Binding Request to a public STUN server and parses the response
    /// to discover the reflexive transport address (public IP and port) as seen by the
    /// server. This is essential for UDP hole-punching in peer-to-peer connections.
    /// </para>
    /// <para>
    /// The client handles both <c>XOR-MAPPED-ADDRESS</c> (preferred, 0x0020) and
    /// the legacy <c>MAPPED-ADDRESS</c> (0x0001) attribute types.
    /// </para>
    /// </remarks>
    public static class StunClient
    {
        // =====================================================================
        // RFC 5389 Constants
        // =====================================================================

        /// <summary>STUN Binding Request message type.</summary>
        private const ushort BindingRequest = 0x0001;

        /// <summary>STUN Binding Success Response message type.</summary>
        private const ushort BindingSuccessResponse = 0x0101;

        /// <summary>STUN magic cookie (network byte order constant).</summary>
        private const uint MagicCookie = 0x2112A442;

        /// <summary>STUN header size in bytes: type(2) + length(2) + cookie(4) + txnId(12).</summary>
        private const int HeaderSize = 20;

        /// <summary>Size of the STUN transaction ID in bytes.</summary>
        private const int TransactionIdSize = 12;

        // Attribute types
        private const ushort AttrMappedAddress = 0x0001;
        private const ushort AttrXorMappedAddress = 0x0020;

        // Address families
        private const byte FamilyIPv4 = 0x01;
        private const byte FamilyIPv6 = 0x02;

        /// <summary>Default timeout per attempt in milliseconds.</summary>
        private const int DefaultTimeoutMs = 3000;

        /// <summary>Default number of retry attempts.</summary>
        private const int DefaultRetries = 2;

        /// <summary>
        /// Queries a STUN server to discover the public endpoint of the given
        /// <see cref="UdpClient"/> socket.
        /// </summary>
        /// <param name="socket">
        /// An existing <see cref="UdpClient"/> whose public-facing endpoint is to be discovered.
        /// The socket must already be bound to a local port.
        /// </param>
        /// <param name="server">
        /// The STUN server hostname. Defaults to <c>stun.l.google.com</c>.
        /// </param>
        /// <param name="port">
        /// The STUN server port. Defaults to <c>19302</c>.
        /// </param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>
        /// A tuple of the discovered public <see cref="IPAddress"/> and port number,
        /// or <see langword="null"/> if discovery failed after all retries.
        /// </returns>
        public static async Task<(IPAddress PublicIP, int PublicPort)?> DiscoverPublicEndpointAsync(
            UdpClient socket,
            string server = "stun.l.google.com",
            int port = 19302,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentException.ThrowIfNullOrWhiteSpace(server);

            // Resolve STUN server address
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(server, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return null;
            }

            // Prefer IPv4 for maximum compatibility
            IPAddress serverAddress = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork)
                                      ?? addresses[0];

            IPEndPoint stunEndpoint = new(serverAddress, port);

            // Build Binding Request
            byte[] transactionId = new byte[TransactionIdSize];
            RandomNumberGenerator.Fill(transactionId);
            byte[] request = BuildBindingRequest(transactionId);

            // Attempt with retries
            int totalAttempts = 1 + DefaultRetries;
            for (int attempt = 0; attempt < totalAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await socket.SendAsync(request, request.Length, stunEndpoint).ConfigureAwait(false);

                    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(DefaultTimeoutMs);

                    UdpReceiveResult result = await socket.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);

                    (IPAddress PublicIP, int PublicPort)? parsed = ParseBindingResponse(
                        result.Buffer, transactionId);

                    if (parsed.HasValue)
                    {
                        return parsed;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout on this attempt — retry
                }
                catch (SocketException)
                {
                    // Network error on this attempt — retry
                }
            }

            return null;
        }

        /// <summary>
        /// Queries multiple STUN servers using a temporary <see cref="UdpClient"/>
        /// for standalone discovery with fallback.
        /// </summary>
        public static async Task<(IPAddress PublicIP, int PublicPort)?> DiscoverPublicEndpointAsync(
            string server = "stun.l.google.com",
            int port = 19302,
            CancellationToken ct = default)
        {
            // Try multiple STUN servers for resilience
            var stunServers = new[]
            {
                (Host: server, Port: port),
                (Host: "stun1.l.google.com", Port: 19302),
                (Host: "stun.cloudflare.com", Port: 3478),
                (Host: "stun.stunprotocol.org", Port: 3478),
            };

            using UdpClient socket = new();
            foreach (var (stunHost, stunPort) in stunServers)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await DiscoverPublicEndpointAsync(socket, stunHost, stunPort, ct).ConfigureAwait(false);
                    if (result.HasValue)
                    {
                        System.Diagnostics.Debug.WriteLine($"[STORM STUN] Discovered via {stunHost}:{stunPort}");
                        return result;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[STORM STUN] {stunHost}:{stunPort} failed: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Builds a 20-byte STUN Binding Request with no attributes.
        /// </summary>
        /// <param name="transactionId">12-byte transaction ID for request correlation.</param>
        /// <returns>The serialized STUN Binding Request.</returns>
        private static byte[] BuildBindingRequest(byte[] transactionId)
        {
            byte[] request = new byte[HeaderSize];
            Span<byte> span = request;

            // Message Type: Binding Request (0x0001)
            BinaryPrimitives.WriteUInt16BigEndian(span, BindingRequest);

            // Message Length: 0 (no attributes)
            BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0);

            // Magic Cookie
            BinaryPrimitives.WriteUInt32BigEndian(span[4..], MagicCookie);

            // Transaction ID (12 bytes)
            transactionId.AsSpan().CopyTo(span[8..]);

            return request;
        }

        /// <summary>
        /// Parses a STUN Binding Success Response and extracts the mapped address.
        /// </summary>
        /// <param name="response">The raw STUN response bytes.</param>
        /// <param name="expectedTransactionId">
        /// The transaction ID from the original request for validation.
        /// </param>
        /// <returns>
        /// The discovered public endpoint, or <see langword="null"/> if the response
        /// is invalid or does not contain a usable mapped address.
        /// </returns>
        private static (IPAddress PublicIP, int PublicPort)? ParseBindingResponse(
            byte[] response,
            byte[] expectedTransactionId)
        {
            if (response.Length < HeaderSize)
            {
                return null;
            }

            ReadOnlySpan<byte> data = response;

            // Validate message type
            ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(data);
            if (messageType != BindingSuccessResponse)
            {
                return null;
            }

            // Validate message length
            ushort messageLength = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            if (HeaderSize + messageLength > data.Length)
            {
                return null;
            }

            // Validate magic cookie
            uint cookie = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            if (cookie != MagicCookie)
            {
                return null;
            }

            // Validate transaction ID
            if (!data.Slice(8, TransactionIdSize).SequenceEqual(expectedTransactionId))
            {
                return null;
            }

            // Parse attributes — prefer XOR-MAPPED-ADDRESS over MAPPED-ADDRESS
            (IPAddress PublicIP, int PublicPort)? xorMapped = null;
            (IPAddress PublicIP, int PublicPort)? mapped = null;

            int offset = HeaderSize;
            int attributesEnd = HeaderSize + messageLength;

            while (offset + 4 <= attributesEnd)
            {
                ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                ushort attrLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
                int attrValueOffset = offset + 4;

                if (attrValueOffset + attrLength > data.Length)
                {
                    break;
                }

                ReadOnlySpan<byte> attrValue = data.Slice(attrValueOffset, attrLength);

                switch (attrType)
                {
                    case AttrXorMappedAddress:
                        xorMapped = ParseXorMappedAddress(attrValue);
                        break;

                    case AttrMappedAddress:
                        mapped = ParseMappedAddress(attrValue);
                        break;
                }

                // Attributes are padded to 4-byte boundaries
                int paddedLength = (attrLength + 3) & ~3;
                offset = attrValueOffset + paddedLength;
            }

            return xorMapped ?? mapped;
        }

        /// <summary>
        /// Parses an XOR-MAPPED-ADDRESS attribute value (RFC 5389 §15.2).
        /// </summary>
        /// <param name="attrValue">The raw attribute value bytes.</param>
        /// <returns>The decoded endpoint, or <see langword="null"/> if unsupported.</returns>
        private static (IPAddress PublicIP, int PublicPort)? ParseXorMappedAddress(
            ReadOnlySpan<byte> attrValue)
        {
            // Minimum: 1 (reserved) + 1 (family) + 2 (port) + 4 (IPv4) = 8 bytes
            if (attrValue.Length < 8)
            {
                return null;
            }

            byte family = attrValue[1];
            if (family != FamilyIPv4)
            {
                // IPv6 not supported for remote desktop NAT traversal
                return null;
            }

            // XOR port with top 16 bits of magic cookie
            ushort xorPort = BinaryPrimitives.ReadUInt16BigEndian(attrValue[2..]);
            int port = xorPort ^ (ushort)(MagicCookie >> 16);

            // XOR IPv4 address with magic cookie
            uint xorIp = BinaryPrimitives.ReadUInt32BigEndian(attrValue[4..]);
            uint ip = xorIp ^ MagicCookie;

            byte[] ipBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ipBytes, ip);
            IPAddress address = new(ipBytes);

            return (address, port);
        }

        /// <summary>
        /// Parses a MAPPED-ADDRESS attribute value (RFC 5389 §15.1).
        /// </summary>
        /// <param name="attrValue">The raw attribute value bytes.</param>
        /// <returns>The decoded endpoint, or <see langword="null"/> if unsupported.</returns>
        private static (IPAddress PublicIP, int PublicPort)? ParseMappedAddress(
            ReadOnlySpan<byte> attrValue)
        {
            if (attrValue.Length < 8)
            {
                return null;
            }

            byte family = attrValue[1];
            if (family != FamilyIPv4)
            {
                return null;
            }

            int port = BinaryPrimitives.ReadUInt16BigEndian(attrValue[2..]);
            IPAddress address = new(attrValue.Slice(4, 4));

            return (address, port);
        }
    }
}
