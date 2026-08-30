using System;
using System.Net;
using StormRemoteControl.Core;

namespace StormRemoteControl.Models
{
    /// <summary>
    /// Configuration for a remote session including connection parameters,
    /// quality settings, and security options.
    /// </summary>
    public sealed class SessionConfig
    {
        /// <summary>Target host IP address or hostname.</summary>
        public string TargetAddress { get; set; } = string.Empty;

        /// <summary>Target host port.</summary>
        public int TargetPort { get; set; } = StormProtocol.DefaultPort;

        /// <summary>Access password (optional, for pre-shared key authentication).</summary>
        public string AccessPassword { get; set; } = string.Empty;

        /// <summary>Video quality profile.</summary>
        public QualityProfile Quality { get; set; } = QualityProfile.Auto;

        /// <summary>Target capture FPS.</summary>
        public int TargetFps { get; set; } = 30;

        /// <summary>Whether to capture and transmit system audio.</summary>
        public bool EnableAudio { get; set; } = true;

        /// <summary>Whether to allow incoming file transfers.</summary>
        public bool AllowFileTransfer { get; set; } = true;

        /// <summary>Maximum file transfer size in bytes (default 1GB).</summary>
        public long MaxFileTransferSize { get; set; } = 1024L * 1024 * 1024;

        /// <summary>Whether to use NAT traversal (hole punching) vs direct connection.</summary>
        public bool UseNatTraversal { get; set; } = false;

        /// <summary>Peer's public endpoint for NAT traversal (discovered via STUN exchange).</summary>
        public IPEndPoint? PeerPublicEndpoint { get; set; }

        /// <summary>Whether this instance is acting as Host (sharing screen) or Client (viewing).</summary>
        public SessionRole Role { get; set; } = SessionRole.Client;
    }

    /// <summary>Session role — whether this instance is sharing or viewing.</summary>
    public enum SessionRole
    {
        /// <summary>Host mode: capturing and sharing our screen.</summary>
        Host,

        /// <summary>Client mode: viewing and controlling the remote host.</summary>
        Client
    }

    /// <summary>
    /// Represents a saved recent connection entry.
    /// </summary>
    public sealed class RecentConnection
    {
        /// <summary>Display name for the connection.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Connection ID or IP address.</summary>
        public string ConnectionId { get; set; } = string.Empty;

        /// <summary>Last known online status.</summary>
        public bool IsOnline { get; set; }

        /// <summary>Timestamp of last connection.</summary>
        public DateTimeOffset LastConnected { get; set; }

        /// <summary>Icon glyph from Segoe MDL2 Assets.</summary>
        public string IconGlyph { get; set; } = "\uE7F4";
    }
}
