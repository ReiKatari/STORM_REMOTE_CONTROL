using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// WebSocket signaling client for WAN P2P connections.
    /// Registers our device ID, queries peer endpoints, and relays ICE candidates.
    /// Falls back gracefully if signaling server is unreachable (LAN-only mode).
    /// </summary>
    public sealed class SignalingClient : IDisposable
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private TaskCompletionSource<string?>? _peerEndpointTcs;
        private string _deviceId = "";
        private string _lastLocalEndpoint = "";
        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 3;

        public event Action<string, string>? PeerEndpointReceived;
        public event Action<bool>? ConnectionStateChanged;
        public event Action<string>? ErrorOccurred;

        public bool IsConnected => _ws?.State == WebSocketState.Open;

        /// <summary>Check if signaling server is reachable (3-second timeout).</summary>
        public static async Task<bool> IsSignalingAvailableAsync()
        {
            try
            {
                using var ws = new ClientWebSocket();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.ConnectAsync(new Uri(Models.AppSettings.SignalingUrl), cts.Token);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Connect to signaling server and register our device ID.</summary>
        public async Task<bool> ConnectAsync(string deviceId, string localEndpoint, CancellationToken ct = default)
        {
            _deviceId = deviceId.Replace(" ", "");
            _lastLocalEndpoint = localEndpoint;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                _ws = new ClientWebSocket();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeout.Token);

                await _ws.ConnectAsync(new Uri(Models.AppSettings.SignalingUrl), linked.Token);
                ConnectionStateChanged?.Invoke(true);

                // Register our device ID
                var registerMsg = JsonSerializer.Serialize(new
                {
                    type = "register",
                    id = _deviceId,
                    endpoint = localEndpoint
                });
                await SendTextAsync(registerMsg);

                // Start receive loop and heartbeat
                _ = ReceiveLoopAsync(_cts.Token);
                _ = HeartbeatLoopAsync(_cts.Token);
                _reconnectAttempts = 0;

                Debug.WriteLine($"[STORM Signaling] Connected and registered as {_deviceId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM Signaling] Connection failed: {ex.Message}");
                ErrorOccurred?.Invoke(ex.Message);
                ConnectionStateChanged?.Invoke(false);
                return false;
            }
        }

        /// <summary>Request a peer's endpoint by their device ID.</summary>
        public async Task<string?> RequestPeerEndpointAsync(string peerDeviceId, int timeoutMs = 10000, CancellationToken ct = default)
        {
            if (!IsConnected) return null;

            string cleanId = peerDeviceId.Replace(" ", "");
            _peerEndpointTcs = new TaskCompletionSource<string?>();

            var requestMsg = JsonSerializer.Serialize(new
            {
                type = "request",
                target = cleanId,
                from = _deviceId
            });
            await SendTextAsync(requestMsg);

            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            linked.Token.Register(() => _peerEndpointTcs.TrySetResult(null));

            return await _peerEndpointTcs.Task;
        }

        /// <summary>Send ICE candidate to peer.</summary>
        public async Task SendIceCandidateAsync(string peerDeviceId, string candidate)
        {
            if (!IsConnected) return;
            var msg = JsonSerializer.Serialize(new
            {
                type = "ice",
                target = peerDeviceId.Replace(" ", ""),
                from = _deviceId,
                candidate
            });
            await SendTextAsync(msg);
        }

        public void Disconnect()
        {
            _cts?.Cancel();
            try { _ws?.Abort(); } catch { }
            _ws?.Dispose();
            _ws = null;
            ConnectionStateChanged?.Invoke(false);
        }

        private async Task SendTextAsync(string text)
        {
            if (_ws?.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? default);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(json);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM Signaling] Receive error: {ex.Message}");
                    break;
                }
            }

            // Attempt auto-reconnect if not explicitly disconnected
            if (!ct.IsCancellationRequested && _reconnectAttempts < MaxReconnectAttempts)
            {
                _reconnectAttempts++;
                Debug.WriteLine($"[STORM Signaling] Auto-reconnect attempt {_reconnectAttempts}/{MaxReconnectAttempts}");
                try
                {
                    await Task.Delay(2000 * _reconnectAttempts, ct);
                    _ws?.Dispose();
                    _ws = new ClientWebSocket();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    await _ws.ConnectAsync(new Uri(Models.AppSettings.SignalingUrl), linked.Token);
                    var registerMsg = JsonSerializer.Serialize(new { type = "register", id = _deviceId, endpoint = _lastLocalEndpoint });
                    await SendTextAsync(registerMsg);
                    _reconnectAttempts = 0;
                    ConnectionStateChanged?.Invoke(true);
                    Debug.WriteLine("[STORM Signaling] Reconnected successfully");
                    await ReceiveLoopAsync(ct); // Re-enter receive loop
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM Signaling] Reconnect failed: {ex.Message}");
                }
            }

            ConnectionStateChanged?.Invoke(false);
        }

        private void ProcessMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";

                switch (type)
                {
                    case "offer":
                        string fromId = root.GetProperty("from").GetString() ?? "";
                        string endpoint = root.GetProperty("endpoint").GetString() ?? "";
                        PeerEndpointReceived?.Invoke(fromId, endpoint);
                        _peerEndpointTcs?.TrySetResult(endpoint);
                        break;

                    case "error":
                        string error = root.GetProperty("message").GetString() ?? "Unknown error";
                        ErrorOccurred?.Invoke(error);
                        _peerEndpointTcs?.TrySetResult(null);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM Signaling] Parse error: {ex.Message}");
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    await Task.Delay(30_000, ct);
                    if (IsConnected)
                    {
                        await SendTextAsync(JsonSerializer.Serialize(new { type = "ping" }));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM Signaling] Heartbeat error: {ex.Message}");
                    break;
                }
            }
        }

        public void Dispose()
        {
            Disconnect();
            _cts?.Dispose();
        }
    }
}
