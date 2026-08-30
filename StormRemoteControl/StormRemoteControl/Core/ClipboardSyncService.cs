using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Bidirectional clipboard synchronization between local and remote machines.
    /// Monitors local clipboard for changes and sends content to remote peer.
    /// </summary>
    public sealed class ClipboardSyncService : IDisposable
    {
        private readonly NetworkTransport _transport;
        private Timer? _pollTimer;
        private string _lastClipboardHash = "";
        private bool _isMonitoring;
        private bool _suppressNextChange;

        /// <summary>Fires when clipboard content is received from remote peer.</summary>
        public event Action<string>? RemoteClipboardReceived;

        public ClipboardSyncService(NetworkTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>Start monitoring local clipboard for changes (poll every 500ms).</summary>
        public void StartMonitoring()
        {
            if (_isMonitoring) return;
            _isMonitoring = true;
            _pollTimer = new Timer(PollClipboard, null, 0, 500);
            Debug.WriteLine("[STORM Clipboard] Monitoring started");
        }

        /// <summary>Stop clipboard monitoring.</summary>
        public void StopMonitoring()
        {
            _isMonitoring = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            Debug.WriteLine("[STORM Clipboard] Monitoring stopped");
        }

        /// <summary>Send current clipboard content to remote peer.</summary>
        public async Task SendClipboardAsync()
        {
            try
            {
                // Must run on UI thread for clipboard access
                string? text = await GetClipboardTextAsync();
                if (string.IsNullOrEmpty(text)) return;

                byte[] payload = Encoding.UTF8.GetBytes(text);
                await _transport.SendAsync(StormPacketType.ClipboardSync, payload);
                Debug.WriteLine($"[STORM Clipboard] Sent {payload.Length} bytes");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM Clipboard] Send failed: {ex.Message}");
            }
        }

        /// <summary>Process incoming clipboard packet from remote peer.</summary>
        public void ProcessPacket(byte[] payload)
        {
            try
            {
                string text = Encoding.UTF8.GetString(payload);
                Debug.WriteLine($"[STORM Clipboard] Received {payload.Length} bytes from remote");

                _suppressNextChange = true;
                SetClipboardText(text);
                RemoteClipboardReceived?.Invoke(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM Clipboard] Process failed: {ex.Message}");
            }
        }

        private void PollClipboard(object? state)
        {
            if (!_isMonitoring) return;
            if (_suppressNextChange)
            {
                _suppressNextChange = false;
                return;
            }

            try
            {
                string? text = GetClipboardTextSync();
                if (text == null) return;

                string hash = ComputeHash(text);
                if (hash != _lastClipboardHash)
                {
                    _lastClipboardHash = hash;
                    _ = SendClipboardContentAsync(text);
                }
            }
            catch { /* Clipboard may be locked by another app */ }
        }

        private async Task SendClipboardContentAsync(string text)
        {
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(text);
                await _transport.SendAsync(StormPacketType.ClipboardSync, payload);
            }
            catch { }
        }

        private static string? GetClipboardTextSync()
        {
            // Clipboard access must be done carefully; may throw if locked
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var task = content.GetTextAsync().AsTask();
                    task.Wait(500);
                    return task.IsCompletedSuccessfully ? task.Result : null;
                }
            }
            catch { }
            return null;
        }

        private static async Task<string?> GetClipboardTextAsync()
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    return await content.GetTextAsync();
                }
            }
            catch { }
            return null;
        }

        private static void SetClipboardText(string text)
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
            catch { }
        }

        private static string ComputeHash(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        public void Dispose()
        {
            StopMonitoring();
        }
    }
}
