// =============================================================================
// InputInjectorService.cs
//
// Win32 SendInput P/Invoke wrapper for injecting mouse and keyboard events
// on the controlled machine in STORM REMOTE CONTROL.
// =============================================================================

using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Specifies mouse button press and release actions.
    /// </summary>
    public enum MouseButtonAction : byte
    {
        /// <summary>Left mouse button pressed.</summary>
        LeftDown = 1,

        /// <summary>Left mouse button released.</summary>
        LeftUp = 2,

        /// <summary>Right mouse button pressed.</summary>
        RightDown = 3,

        /// <summary>Right mouse button released.</summary>
        RightUp = 4,

        /// <summary>Middle mouse button pressed.</summary>
        MiddleDown = 5,

        /// <summary>Middle mouse button released.</summary>
        MiddleUp = 6
    }

    /// <summary>
    /// Provides low-level mouse and keyboard input injection via the Win32 <c>SendInput</c> API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All mouse coordinates use absolute normalized values in the range 0–65535,
    /// mapping to the full virtual desktop. Keyboard events use Windows virtual key codes.
    /// </para>
    /// <para>
    /// Network input packets are deserialized via <see cref="ProcessInputPacket"/> in the
    /// format <c>[1 byte type][4 bytes X][4 bytes Y][4 bytes data]</c>.
    /// </para>
    /// </remarks>
    public static class InputInjectorService
    {
        // =====================================================================
        // Win32 Constants
        // =====================================================================

        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;

        // Mouse event flags
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x1000;
        private const uint MOUSEEVENTF_MOVE_NOCOALESCE = 0x2000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        // Keyboard event flags
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // Virtual key codes for special combinations
        private const ushort VK_MENU = 0x12;      // Alt
        private const ushort VK_CONTROL = 0x11;    // Ctrl
        private const ushort VK_DELETE = 0x2E;     // Delete
        private const ushort VK_END = 0x23;        // End
        private const ushort VK_TAB = 0x09;        // Tab
        private const ushort VK_LWIN = 0x5B;       // Left Windows key

        // Input packet types
        private const byte PacketTypeMouseMove = 1;
        private const byte PacketTypeMouseButton = 2;
        private const byte PacketTypeMouseWheel = 3;
        private const byte PacketTypeKeyboard = 4;

        /// <summary>Minimum size of a network input packet in bytes.</summary>
        private const int MinPacketSize = 13;

        // =====================================================================
        // Win32 P/Invoke Structures
        // =====================================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public nint dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT_UNION
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;

            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public INPUT_UNION u;
        }

        // =====================================================================
        // Win32 P/Invoke Declarations
        // =====================================================================

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// Moves the mouse cursor to the specified absolute normalized coordinates.
        /// </summary>
        /// <param name="normalizedX">
        /// Horizontal position in the range 0–65535, where 0 is the left edge
        /// and 65535 is the right edge of the virtual desktop.
        /// </param>
        /// <param name="normalizedY">
        /// Vertical position in the range 0–65535, where 0 is the top edge
        /// and 65535 is the bottom edge of the virtual desktop.
        /// </param>
        public static void InjectMouseMove(int normalizedX, int normalizedY)
        {
            INPUT input = CreateMouseInput(
                normalizedX,
                normalizedY,
                MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE_NOCOALESCE);

            SendSingleInput(input);
        }

        /// <summary>
        /// Injects a mouse button press or release event at the current cursor position.
        /// </summary>
        /// <param name="action">The button action to inject.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="action"/> is not a valid <see cref="MouseButtonAction"/> value.
        /// </exception>
        public static void InjectMouseButton(MouseButtonAction action)
        {
            uint flags = action switch
            {
                MouseButtonAction.LeftDown => MOUSEEVENTF_LEFTDOWN,
                MouseButtonAction.LeftUp => MOUSEEVENTF_LEFTUP,
                MouseButtonAction.RightDown => MOUSEEVENTF_RIGHTDOWN,
                MouseButtonAction.RightUp => MOUSEEVENTF_RIGHTUP,
                MouseButtonAction.MiddleDown => MOUSEEVENTF_MIDDLEDOWN,
                MouseButtonAction.MiddleUp => MOUSEEVENTF_MIDDLEUP,
                _ => throw new ArgumentOutOfRangeException(nameof(action), action,
                    "Invalid mouse button action.")
            };

            INPUT input = CreateMouseInput(0, 0, flags);
            SendSingleInput(input);
        }

        /// <summary>
        /// Injects a mouse wheel scroll event.
        /// </summary>
        /// <param name="delta">
        /// The scroll amount. Positive values scroll up/forward,
        /// negative values scroll down/backward. One wheel "click" is typically 120.
        /// </param>
        public static void InjectMouseWheel(int delta)
        {
            INPUT input = CreateMouseInput(0, 0, MOUSEEVENTF_WHEEL, mouseData: delta);
            SendSingleInput(input);
        }

        /// <summary>
        /// Injects a keyboard key press or release event.
        /// </summary>
        /// <param name="virtualKeyCode">
        /// The Windows virtual key code (e.g., 0x41 for 'A', 0x0D for Enter).
        /// </param>
        /// <param name="isKeyDown">
        /// <see langword="true"/> for a key press; <see langword="false"/> for a key release.
        /// </param>
        /// <param name="isExtended">
        /// <see langword="true"/> if this is an extended key (e.g., right Ctrl/Alt, arrow keys,
        /// Insert, Delete, Home, End, Page Up/Down, Num Lock).
        /// </param>
        public static void InjectKeyEvent(ushort virtualKeyCode, bool isKeyDown, bool isExtended = false)
        {
            uint flags = 0;
            if (!isKeyDown)
            {
                flags |= KEYEVENTF_KEYUP;
            }

            if (isExtended)
            {
                flags |= KEYEVENTF_EXTENDEDKEY;
            }

            INPUT input = CreateKeyboardInput(virtualKeyCode, flags);
            SendSingleInput(input);
        }

        /// <summary>
        /// Attempts to inject the Ctrl+Alt+Del secure attention sequence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The true Ctrl+Alt+Del sequence is intercepted by the Windows kernel (Winlogon)
        /// and cannot be injected via <c>SendInput</c>. This requires the Secure Attention
        /// Sequence (SAS) service or running as a service in Session 0.
        /// </para>
        /// <para>
        /// As a practical alternative, this method injects <b>Ctrl+Alt+End</b>, which
        /// triggers the security options screen in Remote Desktop sessions and is the
        /// standard equivalent for remote scenarios.
        /// </para>
        /// </remarks>
        public static void InjectCtrlAltDel()
        {
            Debug.WriteLine(
                "[InputInjector] WARNING: True Ctrl+Alt+Del requires SAS (Secure Attention Sequence) " +
                "privileges and cannot be sent via SendInput. Injecting Ctrl+Alt+End as the " +
                "standard remote desktop alternative.");

            INPUT[] inputs =
            [
                CreateKeyboardInput(VK_CONTROL, 0),                               // Ctrl down
                CreateKeyboardInput(VK_MENU, 0),                                  // Alt down
                CreateKeyboardInput(VK_END, KEYEVENTF_EXTENDEDKEY),               // End down
                CreateKeyboardInput(VK_END, KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY), // End up
                CreateKeyboardInput(VK_MENU, KEYEVENTF_KEYUP),                    // Alt up
                CreateKeyboardInput(VK_CONTROL, KEYEVENTF_KEYUP),                 // Ctrl up
            ];

            SendMultipleInputs(inputs);
        }

        /// <summary>
        /// Injects the Alt+Tab key combination to switch windows.
        /// </summary>
        public static void InjectAltTab()
        {
            INPUT[] inputs =
            [
                CreateKeyboardInput(VK_MENU, 0),              // Alt down
                CreateKeyboardInput(VK_TAB, 0),               // Tab down
                CreateKeyboardInput(VK_TAB, KEYEVENTF_KEYUP), // Tab up
                CreateKeyboardInput(VK_MENU, KEYEVENTF_KEYUP) // Alt up
            ];

            SendMultipleInputs(inputs);
        }

        /// <summary>
        /// Injects a Windows key press and release.
        /// </summary>
        public static void InjectWinKey()
        {
            INPUT[] inputs =
            [
                CreateKeyboardInput(VK_LWIN, 0),                // Win down
                CreateKeyboardInput(VK_LWIN, KEYEVENTF_KEYUP)   // Win up
            ];

            SendMultipleInputs(inputs);
        }

        /// <summary>
        /// Deserializes a network input packet and injects the corresponding input event.
        /// </summary>
        /// <param name="packetData">
        /// The raw packet in the format <c>[1 byte type][4 bytes X][4 bytes Y][4 bytes data]</c>.
        /// <list type="table">
        ///   <listheader>
        ///     <term>Type</term>
        ///     <description>Meaning</description>
        ///   </listheader>
        ///   <item>
        ///     <term>1 (MouseMove)</term>
        ///     <description>X = normalizedX, Y = normalizedY, data = unused</description>
        ///   </item>
        ///   <item>
        ///     <term>2 (MouseButton)</term>
        ///     <description>X = unused, Y = unused, data = <see cref="MouseButtonAction"/> value</description>
        ///   </item>
        ///   <item>
        ///     <term>3 (MouseWheel)</term>
        ///     <description>X = unused, Y = unused, data = wheel delta (signed)</description>
        ///   </item>
        ///   <item>
        ///     <term>4 (Keyboard)</term>
        ///     <description>
        ///       X = virtual key code (lower 16 bits), Y = unused,
        ///       data = flags (bit 0 = isKeyDown, bit 1 = isExtended)
        ///     </description>
        ///   </item>
        /// </list>
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="packetData"/> is too short to contain a valid input packet.
        /// </exception>
        public static void ProcessInputPacket(ReadOnlySpan<byte> packetData)
        {
            if (packetData.Length < MinPacketSize)
            {
                throw new ArgumentException(
                    $"Input packet must be at least {MinPacketSize} bytes, got {packetData.Length}.",
                    nameof(packetData));
            }

            byte packetType = packetData[0];
            int x = BinaryPrimitives.ReadInt32BigEndian(packetData[1..]);
            int y = BinaryPrimitives.ReadInt32BigEndian(packetData[5..]);
            int data = BinaryPrimitives.ReadInt32BigEndian(packetData[9..]);

            switch (packetType)
            {
                case PacketTypeMouseMove:
                    InjectMouseMove(x, y);
                    break;

                case PacketTypeMouseButton:
                    if (!Enum.IsDefined((MouseButtonAction)(byte)data))
                    {
                        Debug.WriteLine($"[InputInjector] Unknown mouse button action: {data}");
                        return;
                    }
                    InjectMouseButton((MouseButtonAction)(byte)data);
                    break;

                case PacketTypeMouseWheel:
                    InjectMouseWheel(data);
                    break;

                case PacketTypeKeyboard:
                    ushort vk = (ushort)x;
                    bool isKeyDown = (data & 0x01) != 0;
                    bool isExtended = (data & 0x02) != 0;
                    InjectKeyEvent(vk, isKeyDown, isExtended);
                    break;

                default:
                    Debug.WriteLine($"[InputInjector] Unknown input packet type: {packetType}");
                    break;
            }
        }

        // =====================================================================
        // Private Helpers
        // =====================================================================

        /// <summary>
        /// Creates a mouse <see cref="INPUT"/> structure.
        /// </summary>
        private static INPUT CreateMouseInput(int dx, int dy, uint flags, int mouseData = 0)
        {
            return new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUT_UNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        mouseData = mouseData,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = nint.Zero
                    }
                }
            };
        }

        /// <summary>
        /// Creates a keyboard <see cref="INPUT"/> structure.
        /// </summary>
        private static INPUT CreateKeyboardInput(ushort virtualKeyCode, uint flags)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUT_UNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKeyCode,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = nint.Zero
                    }
                }
            };
        }

        /// <summary>
        /// Sends a single input event via <c>SendInput</c>.
        /// </summary>
        /// <param name="input">The input event to send.</param>
        /// <exception cref="Win32Exception">
        /// <c>SendInput</c> failed to inject the event.
        /// </exception>
        private static void SendSingleInput(INPUT input)
        {
            INPUT[] inputs = [input];
            uint sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
            if (sent == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine(
                    $"[InputInjector] SendInput failed. Win32 error: {error} " +
                    $"(this may require UIPI elevation or the process may be blocked by a higher-IL window).");
            }
        }

        /// <summary>
        /// Sends multiple input events atomically via a single <c>SendInput</c> call.
        /// </summary>
        /// <param name="inputs">The array of input events to send.</param>
        private static void SendMultipleInputs(INPUT[] inputs)
        {
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != (uint)inputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine(
                    $"[InputInjector] SendInput sent {sent}/{inputs.Length} events. " +
                    $"Win32 error: {error}.");
            }
        }
    }
}
