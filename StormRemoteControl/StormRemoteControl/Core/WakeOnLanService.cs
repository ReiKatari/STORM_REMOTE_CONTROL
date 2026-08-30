using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Wake-on-LAN (WoL) magic packet sender.
    /// Supports standard 102-byte magic packet broadcast via UDP.
    /// </summary>
    public static class WakeOnLanService
    {
        /// <summary>
        /// Send WoL magic packet to wake a remote PC.
        /// </summary>
        /// <param name="macAddress">MAC address: "AA:BB:CC:DD:EE:FF" or "AA-BB-CC-DD-EE-FF".</param>
        /// <param name="port">Target port (default: 9).</param>
        /// <returns>True if the packet was sent successfully.</returns>
        public static async Task<bool> WakeAsync(string macAddress, int port = 9)
        {
            if (!IsValidMac(macAddress))
            {
                Debug.WriteLine($"[STORM WoL] Invalid MAC address: {macAddress}");
                return false;
            }

            try
            {
                byte[] macBytes = ParseMac(macAddress);
                byte[] magicPacket = BuildMagicPacket(macBytes);

                // Send on both port 9 and 7 for compatibility
                using var udpClient = new UdpClient();
                udpClient.EnableBroadcast = true;

                var broadcastEp = new IPEndPoint(IPAddress.Broadcast, port);
                await udpClient.SendAsync(magicPacket, magicPacket.Length, broadcastEp);

                // Also try directed broadcast on each network interface
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    try
                    {
                        var props = nic.GetIPProperties();
                        foreach (var addr in props.UnicastAddresses
                            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                        {
                            var broadcast = GetBroadcastAddress(addr.Address, addr.IPv4Mask);
                            var directedEp = new IPEndPoint(broadcast, port);
                            await udpClient.SendAsync(magicPacket, magicPacket.Length, directedEp);
                        }
                    }
                    catch { /* Some NICs may not support this */ }
                }

                // Also send on port 7
                if (port != 7)
                {
                    var port7Ep = new IPEndPoint(IPAddress.Broadcast, 7);
                    await udpClient.SendAsync(magicPacket, magicPacket.Length, port7Ep);
                }

                Debug.WriteLine($"[STORM WoL] Magic packet sent for MAC {macAddress}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM WoL] Failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Validate MAC address format.</summary>
        public static bool IsValidMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return false;
            string clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "");
            return clean.Length == 12 && clean.All(c => Uri.IsHexDigit(c));
        }

        /// <summary>Build the 102-byte magic packet: 6x 0xFF + 16x MAC address.</summary>
        private static byte[] BuildMagicPacket(byte[] macBytes)
        {
            byte[] packet = new byte[6 + 16 * 6]; // 102 bytes

            // First 6 bytes: 0xFF
            for (int i = 0; i < 6; i++)
                packet[i] = 0xFF;

            // Next 96 bytes: 16 repetitions of the MAC address
            for (int i = 0; i < 16; i++)
                Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);

            return packet;
        }

        private static byte[] ParseMac(string mac)
        {
            string clean = mac.Replace(":", "").Replace("-", "").Replace(" ", "");
            byte[] bytes = new byte[6];
            for (int i = 0; i < 6; i++)
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress mask)
        {
            byte[] ipBytes = address.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();
            byte[] broadcastBytes = new byte[4];
            for (int i = 0; i < 4; i++)
                broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
            return new IPAddress(broadcastBytes);
        }
    }
}
