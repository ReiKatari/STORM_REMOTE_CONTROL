using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// LAN/WAN device discovery service using UDP broadcast + STUN relay.
    /// Allows hosts to announce their Device ID on the local network,
    /// and clients to discover hosts by ID.
    ///
    /// Protocol:
    ///   Discovery port: 17701 (broadcast/multicast)
    ///   Host announces: "STORM_ANNOUNCE|{DeviceId}|{ListenPort}|{PublicIP}:{PublicPort}"
    ///   Client queries:  "STORM_DISCOVER|{TargetDeviceId}"
    ///   Host responds:   "STORM_FOUND|{DeviceId}|{ListenPort}|{LocalIP}"
    /// </summary>
    public sealed class DiscoveryService : IDisposable
    {
        private const int DiscoveryPort = 17701;
        private const string ProtocolPrefix = "STORM";
        private const string AnnounceTag = "STORM_ANNOUNCE";
        private const string DiscoverTag = "STORM_DISCOVER";
        private const string FoundTag = "STORM_FOUND";

        private UdpClient? _listener;
        private CancellationTokenSource? _cts;
        private string _deviceId = "";
        private int _listenPort = StormProtocol.DefaultPort;
        private string _publicEndpoint = "";

        /// <summary>
        /// Start announcing this host's Device ID on the LAN.
        /// Also listens for discovery queries from clients.
        /// </summary>
        public void StartHostAnnounce(string deviceId, int listenPort, string publicEndpoint = "")
        {
            StopAnnounce();

            _deviceId = deviceId.Replace(" ", "");
            _listenPort = listenPort;
            _publicEndpoint = publicEndpoint;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new UdpClient();
                _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _listener.EnableBroadcast = true;

                // Start listening for discovery queries
                _ = ListenForQueriesAsync(_cts.Token);

                // Start periodic broadcast
                _ = PeriodicAnnounceAsync(_cts.Token);

                Debug.WriteLine($"[STORM Discovery] Host announcing ID={_deviceId} on port {DiscoveryPort}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM Discovery] Failed to start announce: {ex.Message}");
            }
        }

        /// <summary>
        /// Discover a host by its Device ID on the LAN.
        /// Returns the host's IP address and listen port, or null if not found.
        /// Timeout: 5 seconds with 3 broadcast attempts.
        /// </summary>
        public async Task<(string IpAddress, int Port)?> DiscoverHostAsync(
            string targetDeviceId, CancellationToken ct = default)
        {
            string cleanId = targetDeviceId.Replace(" ", "");

            Debug.WriteLine($"[STORM Discovery] Searching for host ID={cleanId}...");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            using var queryClient = new UdpClient();
            queryClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            queryClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            queryClient.EnableBroadcast = true;

            // Send discovery queries
            byte[] queryData = Encoding.UTF8.GetBytes($"{DiscoverTag}|{cleanId}");
            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

            // Send 3 broadcast attempts with 200ms intervals
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (timeoutCts.Token.IsCancellationRequested) break;

                try
                {
                    await queryClient.SendAsync(queryData, queryData.Length, broadcastEp);
                    Debug.WriteLine($"[STORM Discovery] Broadcast query attempt {attempt + 1}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM Discovery] Broadcast send failed: {ex.Message}");
                }

                // Wait for response
                try
                {
                    var receiveTask = queryClient.ReceiveAsync(timeoutCts.Token);
                    var delayTask = Task.Delay(1500, timeoutCts.Token);

                    var completed = await Task.WhenAny(receiveTask.AsTask(), delayTask);

                    if (completed == receiveTask.AsTask() && receiveTask.IsCompletedSuccessfully)
                    {
                        var result = receiveTask.Result;
                        string response = Encoding.UTF8.GetString(result.Buffer);

                        if (response.StartsWith(FoundTag))
                        {
                            // Parse: STORM_FOUND|DeviceId|ListenPort|LocalIP
                            var parts = response.Split('|');
                            if (parts.Length >= 4 && parts[1].Replace(" ", "") == cleanId)
                            {
                                string ip = parts[3];
                                int port = int.TryParse(parts[2], out int p) ? p : StormProtocol.DefaultPort;
                                Debug.WriteLine($"[STORM Discovery] Found host: {ip}:{port}");
                                return (ip, port);
                            }
                        }
                        else if (response.StartsWith(AnnounceTag))
                        {
                            // Parse announce: STORM_ANNOUNCE|DeviceId|ListenPort|PublicEndpoint
                            var parts = response.Split('|');
                            if (parts.Length >= 3 && parts[1].Replace(" ", "") == cleanId)
                            {
                                string ip = result.RemoteEndPoint.Address.ToString();
                                int port = int.TryParse(parts[2], out int p) ? p : StormProtocol.DefaultPort;
                                Debug.WriteLine($"[STORM Discovery] Found host via announce: {ip}:{port}");
                                return (ip, port);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM Discovery] Receive error: {ex.Message}");
                }
            }

            Debug.WriteLine("[STORM Discovery] Host not found");
            return null;
        }

        /// <summary>Listen for incoming discovery queries and respond.</summary>
        private async Task ListenForQueriesAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var result = await _listener.ReceiveAsync(ct);
                    string message = Encoding.UTF8.GetString(result.Buffer);

                    if (message.StartsWith(DiscoverTag))
                    {
                        var parts = message.Split('|');
                        if (parts.Length >= 2)
                        {
                            string requestedId = parts[1].Replace(" ", "");
                            if (requestedId == _deviceId)
                            {
                                // Respond with our info
                                string localIp = GetLocalIpAddress();
                                string response = $"{FoundTag}|{_deviceId}|{_listenPort}|{localIp}";
                                byte[] responseData = Encoding.UTF8.GetBytes(response);

                                await _listener.SendAsync(
                                    responseData, responseData.Length, result.RemoteEndPoint);

                                Debug.WriteLine($"[STORM Discovery] Responded to query from {result.RemoteEndPoint}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM Discovery] Listen error: {ex.Message}");
                    await Task.Delay(500, ct);
                }
            }
        }

        /// <summary>Periodically broadcast our presence on the LAN.</summary>
        private async Task PeriodicAnnounceAsync(CancellationToken ct)
        {
            var broadcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

            while (!ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    string message = $"{AnnounceTag}|{_deviceId}|{_listenPort}|{_publicEndpoint}";
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    await _listener.SendAsync(data, data.Length, broadcastEp);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STORM Discovery] Announce error: {ex.Message}");
                }

                try { await Task.Delay(3000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Stop host announcement.</summary>
        public void StopAnnounce()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _listener?.Close();
            _listener?.Dispose();
            _listener = null;
        }

        /// <summary>Get local IPv4 address.</summary>
        private static string GetLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 80);
                if (socket.LocalEndPoint is IPEndPoint ep)
                    return ep.Address.ToString();
            }
            catch { }

            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        return ip.ToString();
                }
            }
            catch { }

            return "127.0.0.1";
        }

        public void Dispose()
        {
            StopAnnounce();
        }
    }
}
