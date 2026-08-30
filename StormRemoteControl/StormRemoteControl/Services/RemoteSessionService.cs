using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StormRemoteControl.Core;
using StormRemoteControl.Models;
using QualityProfile = StormRemoteControl.Core.QualityProfile;

namespace StormRemoteControl.Services
{
    /// <summary>
    /// Master orchestrator for a STORM remote desktop session.
    /// Coordinates screen capture, video encoding, audio, network transport,
    /// input injection, file transfer, and connection management.
    /// Operates in dual mode: Host (shares screen) and Client (views remote).
    /// </summary>
    public sealed class RemoteSessionService : IDisposable
    {
        // ── Core engines ──
        private readonly CryptoEngine _crypto;
        private readonly NetworkTransport _transport;
        private readonly ConnectionManager _connectionManager;
        private readonly FileTransferService _fileTransfer;
        private readonly DiscoveryService _discovery;
        private readonly SignalingClient _signaling;
        private ScreenCaptureService? _screenCapture;
        private VideoEncoderService? _videoEncoder;
        private AudioCaptureService? _audioCapture;

        // ── State ──
        private CancellationTokenSource? _sessionCts;
        private SessionState _currentState = SessionState.Disconnected;
        private QualityProfile _activeProfile = QualityProfile.Auto;
        private SessionRole _role = SessionRole.Client;

        // ── Metrics ──
        private int _frameCounter;
        private readonly Stopwatch _metricsStopwatch = new();

        // ── Public events ──

        /// <summary>Fired when session state changes.</summary>
        public event Action<SessionState>? StateChanged;

        /// <summary>Fired approximately every second with network performance metrics.</summary>
        public event Action<double, double, int>? MetricsUpdated;

        /// <summary>Fired when a chat message is received from the remote peer.</summary>
        public event Action<string>? ChatMessageReceived;

        /// <summary>Fired when a decoded video frame is ready for display (Client mode).</summary>
        public event Action<byte[], int, int>? VideoFrameReceived;

        /// <summary>Fired when audio data is received from the remote peer (Client mode).</summary>
        public event Action<byte[]>? AudioDataReceived;

        /// <summary>Fired when a file transfer offer arrives.</summary>
        public event Action<Guid, string, long>? FileOfferReceived;

        /// <summary>Fired when file transfer progress updates.</summary>
        public event Action<Guid, double>? FileTransferProgress;

        /// <summary>Fired when file transfer completes.</summary>
        public event Action<Guid, bool>? FileTransferCompleted;

        /// <summary>Fired when our public endpoint is discovered via STUN.</summary>
        public event Action<IPEndPoint>? PublicEndpointDiscovered;

        // ── Properties ──

        public SessionState CurrentState
        {
            get => _currentState;
            private set
            {
                if (_currentState != value)
                {
                    _currentState = value;
                    StateChanged?.Invoke(_currentState);
                }
            }
        }

        public QualityProfile ActiveProfile
        {
            get => _activeProfile;
            set => _activeProfile = value;
        }

        public SessionRole Role => _role;
        public IPEndPoint? PublicEndpoint => _connectionManager.PublicEndpoint;
        public double CurrentLatencyMs => _transport.CurrentLatencyMs;
        public double CurrentBitrateMbps => _transport.CurrentBitrateMbps;
        public int CurrentFps => _frameCounter;

        public RemoteSessionService()
        {
            _crypto = new CryptoEngine();
            _transport = new NetworkTransport(_crypto);
            _connectionManager = new ConnectionManager(_crypto, _transport);
            _fileTransfer = new FileTransferService(_transport);
            _discovery = new DiscoveryService();
            _signaling = new SignalingClient();

            // Wire up connection events
            _connectionManager.StateChanged += OnConnectionStateChanged;
            _connectionManager.PeerConnected += OnPeerConnected;
            _connectionManager.PeerDisconnected += OnPeerDisconnected;
            _connectionManager.PublicEndpointDiscovered += ep => PublicEndpointDiscovered?.Invoke(ep);

            // Wire up transport events for data packets
            _transport.PacketReceived += OnPacketReceived;

            // Wire up file transfer events
            _fileTransfer.FileOfferReceived += (id, name, size) => FileOfferReceived?.Invoke(id, name, size);
            _fileTransfer.TransferProgressUpdated += (id, progress) => FileTransferProgress?.Invoke(id, progress);
            _fileTransfer.TransferCompleted += (id, success) => FileTransferCompleted?.Invoke(id, success);
        }

        // ═══════════════════════════════════════════════════════════
        //  HOST MODE — Share our screen and accept incoming control
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize host mode: start listening for connections, perform STUN discovery.
        /// </summary>
        public async Task<bool> InitializeHostAsync(int port = StormProtocol.DefaultPort, CancellationToken ct = default)
        {
            _role = SessionRole.Host;
            CurrentState = SessionState.Connecting;

            bool result = await _connectionManager.InitializeHostAsync(port, ct);
            if (result)
            {
                // Start LAN discovery broadcast so clients can find us by ID
                string deviceId = Models.AppSettings.DeviceId;
                string publicEp = _connectionManager.PublicEndpoint?.ToString() ?? "";
                _discovery.StartHostAnnounce(deviceId, port, publicEp);
                _ = _signaling.ConnectAsync(deviceId, publicEp, ct);

                CurrentState = SessionState.WaitingForPeer;
            }
            else
            {
                CurrentState = SessionState.Failed;
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════
        //  CLIENT MODE — Connect to a remote host
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Connect to a remote host by IP address (direct or after STUN exchange).
        /// </summary>
        public async Task<bool> ConnectAsync(string connectionId, string accessPassword = "")
        {
            if (CurrentState == SessionState.Connected) return true;

            _role = SessionRole.Client;
            CurrentState = SessionState.Connecting;
            _sessionCts = new CancellationTokenSource();

            try
            {
                string ipAddress;
                int port = StormProtocol.DefaultPort;

                if (connectionId.Contains(':'))
                {
                    // Format: IP:port — direct connection
                    var parts = connectionId.Split(':');
                    ipAddress = parts[0];
                    port = int.Parse(parts[1]);
                }
                else if (connectionId.Contains('.'))
                {
                    // Plain IP address — direct connection
                    ipAddress = connectionId;
                }
                else
                {
                    // Numeric ID — discover host via LAN broadcast
                    Debug.WriteLine($"[STORM] Discovering host by ID: {connectionId}");

                    using var discoverClient = new DiscoveryService();
                    var discovered = await discoverClient.DiscoverHostAsync(
                        connectionId, _sessionCts.Token);

                    if (discovered.HasValue)
                    {
                        ipAddress = discovered.Value.IpAddress;
                        port = discovered.Value.Port;
                        Debug.WriteLine($"[STORM] Discovered host at {ipAddress}:{port}");
                    }
                    else
                    {
                        Debug.WriteLine($"[STORM] Host with ID {connectionId} not found on LAN, trying Signaling Server");
                        await _signaling.ConnectAsync(Models.AppSettings.DeviceId, _connectionManager.PublicEndpoint?.ToString() ?? "", _sessionCts.Token);
                        string? peerEndpoint = await _signaling.RequestPeerEndpointAsync(connectionId, 10000, _sessionCts.Token);
                        _signaling.Disconnect();

                        if (!string.IsNullOrEmpty(peerEndpoint) && IPEndPoint.TryParse(peerEndpoint, out var peerEp))
                        {
                            bool punchConnected = await _connectionManager.ConnectWithHolePunchAsync(peerEp, _sessionCts.Token);
                            if (punchConnected)
                            {
                                CurrentState = SessionState.Connected;
                                StartMetricsLoop();
                                return true;
                            }
                        }

                        Debug.WriteLine($"[STORM] Host with ID {connectionId} not found on network");
                        CurrentState = SessionState.Failed;
                        return false;
                    }
                }

                // Connect directly to discovered/specified address with retries
                bool connected = false;
                for (int attempt = 1; attempt <= 3 && !connected; attempt++)
                {
                    Debug.WriteLine($"[STORM] Connection attempt {attempt}/3 to {ipAddress}:{port}");
                    connected = await _connectionManager.ConnectDirectAsync(ipAddress, port, _sessionCts.Token);
                    if (!connected && attempt < 3)
                    {
                        Debug.WriteLine($"[STORM] Attempt {attempt} failed, retrying in {attempt * 2}s...");
                        await Task.Delay(attempt * 2000, _sessionCts.Token);
                    }
                }

                if (connected)
                {
                    CurrentState = SessionState.Connected;
                    StartMetricsLoop();
                    return true;
                }
                else
                {
                    CurrentState = SessionState.Failed;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] ConnectAsync failed: {ex.Message}");
                CurrentState = SessionState.Failed;
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  SESSION MANAGEMENT
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Disconnect from the current session gracefully.
        /// </summary>
        public void Disconnect()
        {
            _sessionCts?.Cancel();
            _screenCapture?.StopCapture();
            _audioCapture?.StopCapture();
            _discovery.StopAnnounce();
            _signaling.Disconnect();
            _connectionManager.Disconnect();
            CurrentState = SessionState.Disconnected;
        }

        /// <summary>
        /// Send an input event to the remote host (Client mode).
        /// </summary>
        public async Task SendInputAsync(int actionType, int x, int y, uint keyData)
        {
            if (CurrentState != SessionState.Connected || _role != SessionRole.Client) return;

            byte[] inputPayload = new byte[13];
            inputPayload[0] = (byte)actionType;
            BitConverter.GetBytes(x).CopyTo(inputPayload, 1);
            BitConverter.GetBytes(y).CopyTo(inputPayload, 5);
            BitConverter.GetBytes(keyData).CopyTo(inputPayload, 9);

            StormPacketType packetType = actionType switch
            {
                0x01 or 0x02 or 0x03 => StormPacketType.MouseInput,
                0x04 => StormPacketType.SystemHotkey,
                _ => StormPacketType.KeyboardInput
            };

            await _transport.SendAsync(packetType, inputPayload, reliable: packetType == StormPacketType.SystemHotkey);
        }

        /// <summary>
        /// Send a chat message to the remote peer.
        /// </summary>
        public async Task SendChatMessageAsync(string message)
        {
            if (CurrentState != SessionState.Connected) return;

            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            await _transport.SendAsync(StormPacketType.ChatMessage, messageBytes, reliable: true);
        }

        /// <summary>
        /// Initiate a file transfer to the remote peer.
        /// </summary>
        public async Task<Guid> SendFileAsync(string filePath, CancellationToken ct = default)
        {
            return await _fileTransfer.SendFileAsync(filePath, ct);
        }

        /// <summary>Accept an incoming file offer.</summary>
        public void AcceptFileOffer(Guid transferId, string savePath) =>
            _fileTransfer.AcceptFileOffer(transferId, savePath);

        /// <summary>Reject an incoming file offer.</summary>
        public void RejectFileOffer(Guid transferId) =>
            _fileTransfer.RejectFileOffer(transferId);

        // ═══════════════════════════════════════════════════════════
        //  PRIVATE — Event handlers and internal loops
        // ═══════════════════════════════════════════════════════════

        private void OnConnectionStateChanged(ConnectionState state)
        {
            CurrentState = state switch
            {
                ConnectionState.Connected => SessionState.Connected,
                ConnectionState.Connecting or ConnectionState.HolePunching or ConnectionState.Initializing => SessionState.Connecting,
                ConnectionState.Authenticating => SessionState.Authenticating,
                ConnectionState.WaitingForPeer => SessionState.WaitingForPeer,
                ConnectionState.Failed => SessionState.Failed,
                _ => SessionState.Disconnected
            };
        }

        private void OnPeerConnected(IPEndPoint peer)
        {
            Debug.WriteLine($"[STORM] Peer connected: {peer}");
            CurrentState = SessionState.Connected;

            _sessionCts = new CancellationTokenSource();

            if (_role == SessionRole.Host)
            {
                // Start screen capture and encoding pipeline
                StartHostPipeline();
            }

            StartMetricsLoop();
        }

        private void OnPeerDisconnected(string reason)
        {
            Debug.WriteLine($"[STORM] Peer disconnected: {reason}");
            _sessionCts?.Cancel();
            _screenCapture?.StopCapture();
            _audioCapture?.StopCapture();
            CurrentState = SessionState.Disconnected;
        }

        /// <summary>
        /// Process incoming data packets (video frames, audio, input, chat).
        /// </summary>
        private void OnPacketReceived(StormPacketHeader header, byte[] payload, IPEndPoint sender)
        {
            switch (header.PacketType)
            {
                case StormPacketType.VideoFrame:
                case StormPacketType.VideoFrameFragment:
                    HandleVideoFrame(payload);
                    break;

                case StormPacketType.AudioData:
                    AudioDataReceived?.Invoke(payload);
                    break;

                case StormPacketType.MouseInput:
                case StormPacketType.KeyboardInput:
                case StormPacketType.SystemHotkey:
                    HandleInputPacket(header.PacketType, payload);
                    break;

                case StormPacketType.ChatMessage:
                    string message = Encoding.UTF8.GetString(payload);
                    ChatMessageReceived?.Invoke(message);
                    break;

                case StormPacketType.FileOffer:
                case StormPacketType.FileAccept:
                case StormPacketType.FileReject:
                case StormPacketType.FileChunk:
                case StormPacketType.FileChunkAck:
                case StormPacketType.FileComplete:
                    _fileTransfer.ProcessPacket(header, payload);
                    break;
            }
        }

        /// <summary>
        /// Handle incoming video frame data (Client mode) — decode JPEG and fire event.
        /// </summary>
        private void HandleVideoFrame(byte[] jpegData)
        {
            if (_role != SessionRole.Client) return;

            Interlocked.Increment(ref _frameCounter);

            // Pass raw JPEG data to the UI layer for decoding and display
            // The SessionPage will handle BitmapImage creation on the UI thread
            VideoFrameReceived?.Invoke(jpegData, 0, 0);
        }

        /// <summary>
        /// Handle incoming input packets (Host mode) — inject mouse/keyboard via Win32.
        /// </summary>
        private void HandleInputPacket(StormPacketType type, byte[] payload)
        {
            if (_role != SessionRole.Host) return;

            try
            {
                InputInjectorService.ProcessInputPacket(payload);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Input injection error: {ex.Message}");
            }
        }

        private int _isEncoding = 0;

        /// <summary>
        /// Start the host-side capture → encode → transmit pipeline.
        /// </summary>
        private void StartHostPipeline()
        {
            var token = _sessionCts!.Token;

            // Initialize screen capture
            _screenCapture = new ScreenCaptureService();
            _videoEncoder = new VideoEncoderService();

            _screenCapture.FrameCaptured += (bgraPixels, width, height) =>
            {
                if (token.IsCancellationRequested) return;

                // Drop frame if encoder is busy
                if (Interlocked.CompareExchange(ref _isEncoding, 1, 0) != 0) return;

                // Encode and send in background to avoid blocking capture
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (quality, maxW, maxH) = _videoEncoder.GetProfileSettings(_activeProfile);

                        byte[] jpegData;
                        if (width > maxW || height > maxH)
                        {
                            jpegData = await _videoEncoder.EncodeFrameResizedAsync(
                                bgraPixels, width, height, maxW, maxH, quality);
                        }
                        else
                        {
                            jpegData = await _videoEncoder.EncodeFrameAsync(
                                bgraPixels, width, height, quality);
                        }

                        // Send fragmented if too large for single packet
                        await _transport.SendFragmentedAsync(StormPacketType.VideoFrame, jpegData);

                        Interlocked.Increment(ref _frameCounter);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STORM] Encode/send error: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isEncoding, 0);
                    }
                }, token);
            };

            // Start screen capture (primary monitor)
            try
            {
                _screenCapture.StartCapture();
                Debug.WriteLine("[STORM] Host pipeline started — capturing screen");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Screen capture start failed: {ex.Message}");
            }

            // Start audio capture
            _ = Task.Run(async () =>
            {
                try
                {
                    _audioCapture = new AudioCaptureService();
                    _audioCapture.AudioDataCaptured += async (audioData) =>
                    {
                        if (token.IsCancellationRequested) return;
                        await _transport.SendAsync(StormPacketType.AudioData, audioData);
                    };

                    bool audioStarted = await _audioCapture.StartCaptureAsync();
                    Debug.WriteLine($"[STORM] Audio capture: {(audioStarted ? "active" : "unavailable")}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM] Audio capture error: {ex.Message}");
                }
            }, token);
        }

        /// <summary>
        /// Start periodic metrics collection and reporting.
        /// </summary>
        private void StartMetricsLoop()
        {
            var token = _sessionCts!.Token;
            _metricsStopwatch.Restart();

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && CurrentState == SessionState.Connected)
                {
                    await Task.Delay(1000, token);

                    int fps = Interlocked.Exchange(ref _frameCounter, 0);

                    MetricsUpdated?.Invoke(
                        _transport.CurrentLatencyMs,
                        _transport.CurrentBitrateMbps,
                        fps);
                }
            }, token);
        }

        public void Dispose()
        {
            Disconnect();
            _discovery?.Dispose();
            _signaling?.Dispose();
            _screenCapture?.Dispose();
            _audioCapture?.Dispose();
            _fileTransfer?.Dispose();
            _transport?.Dispose();
            _connectionManager?.Dispose();
            _crypto?.Dispose();
            _sessionCts?.Dispose();
        }
    }

    /// <summary>Session lifecycle state.</summary>
    public enum SessionState
    {
        Disconnected,
        Connecting,
        Authenticating,
        WaitingForPeer,
        Connected,
        Failed
    }
}
