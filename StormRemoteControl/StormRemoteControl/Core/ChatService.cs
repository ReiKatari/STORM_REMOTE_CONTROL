using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core
{
    /// <summary>Represents a single chat message with sender info and timestamp.</summary>
    public record ChatMessage(string Text, DateTime Timestamp, bool IsLocal);

    /// <summary>
    /// In-session text chat service. Sends and receives UTF-8 messages 
    /// over the encrypted STORM protocol channel.
    /// </summary>
    public sealed class ChatService
    {
        private readonly NetworkTransport _transport;

        /// <summary>Fires when a new message arrives from the remote peer.</summary>
        public event Action<ChatMessage>? MessageReceived;

        /// <summary>Observable message history for UI data binding.</summary>
        public ObservableCollection<ChatMessage> Messages { get; } = new();

        public ChatService(NetworkTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>Send a text message to the remote peer.</summary>
        public async Task SendMessageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var msg = new ChatMessage(text.Trim(), DateTime.Now, IsLocal: true);
            Messages.Add(msg);

            byte[] payload = Encoding.UTF8.GetBytes(text.Trim());
            await _transport.SendAsync(StormPacketType.ChatMessage, payload);

            Debug.WriteLine($"[STORM Chat] Sent: {text.Trim()}");
        }

        /// <summary>Process an incoming chat packet from the remote peer.</summary>
        public void ProcessPacket(byte[] payload)
        {
            try
            {
                string text = Encoding.UTF8.GetString(payload);
                var msg = new ChatMessage(text, DateTime.Now, IsLocal: false);
                Messages.Add(msg);
                MessageReceived?.Invoke(msg);

                Debug.WriteLine($"[STORM Chat] Received: {text}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM Chat] Error processing message: {ex.Message}");
            }
        }

        /// <summary>Clear all message history.</summary>
        public void ClearHistory()
        {
            Messages.Clear();
        }
    }
}
