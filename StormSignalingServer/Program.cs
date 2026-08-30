using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

// Maps DeviceId -> WebSocket
var connections = new ConcurrentDictionary<string, WebSocket>();
// Maps DeviceId -> Endpoint string
var endpoints = new ConcurrentDictionary<string, string>();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var ws = await context.WebSockets.AcceptWebSocketAsync();
        string deviceId = "";

        var buffer = new byte[1024 * 4];
        var receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        while (!receiveResult.CloseStatus.HasValue)
        {
            if (receiveResult.MessageType == WebSocketMessageType.Text)
            {
                var msg = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";

                    if (type == "register")
                    {
                        deviceId = root.GetProperty("id").GetString() ?? "";
                        string endpoint = root.GetProperty("endpoint").GetString() ?? "";
                        
                        if (!string.IsNullOrEmpty(deviceId))
                        {
                            connections[deviceId] = ws;
                            endpoints[deviceId] = endpoint;
                            Console.WriteLine($"[Signaling] Device {deviceId} registered with endpoint {endpoint}");
                        }
                    }
                    else if (type == "request")
                    {
                        string target = root.GetProperty("target").GetString() ?? "";
                        string from = root.GetProperty("from").GetString() ?? "";
                        
                        Console.WriteLine($"[Signaling] Device {from} requesting {target}");
                        
                        if (connections.TryGetValue(target, out var peerWs) && peerWs.State == WebSocketState.Open)
                        {
                            // Send target's endpoint to 'from'
                            if (endpoints.TryGetValue(target, out string targetEp))
                            {
                                var response = JsonSerializer.Serialize(new { type = "offer", from = target, endpoint = targetEp });
                                await ws.SendAsync(Encoding.UTF8.GetBytes(response), WebSocketMessageType.Text, true, CancellationToken.None);
                            }

                            // Send 'from's endpoint to target (bidirectional holepunching setup)
                            if (endpoints.TryGetValue(from, out string fromEp))
                            {
                                var offerToTarget = JsonSerializer.Serialize(new { type = "offer", from = from, endpoint = fromEp });
                                await peerWs.SendAsync(Encoding.UTF8.GetBytes(offerToTarget), WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                        }
                        else
                        {
                            var errorResponse = JsonSerializer.Serialize(new { type = "error", message = "Peer not found or disconnected" });
                            await ws.SendAsync(Encoding.UTF8.GetBytes(errorResponse), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    else if (type == "ice")
                    {
                        string target = root.GetProperty("target").GetString() ?? "";
                        if (connections.TryGetValue(target, out var peerWs) && peerWs.State == WebSocketState.Open)
                        {
                            await peerWs.SendAsync(new ArraySegment<byte>(buffer, 0, receiveResult.Count), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Signaling] Error parsing message: {ex.Message}");
                }
            }

            receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        }

        if (!string.IsNullOrEmpty(deviceId))
        {
            connections.TryRemove(deviceId, out _);
            endpoints.TryRemove(deviceId, out _);
            Console.WriteLine($"[Signaling] Device {deviceId} disconnected.");
        }
        await ws.CloseAsync(receiveResult.CloseStatus.Value, receiveResult.CloseStatusDescription, CancellationToken.None);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run();
