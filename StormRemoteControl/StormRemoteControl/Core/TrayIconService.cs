using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// System tray (notification area) icon manager using Win32 Shell_NotifyIcon.
    /// Supports minimize to tray, context menu, and Windows autostart registration.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private const int WM_TRAYICON = 0x8000;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int NIM_ADD = 0x00;
        private const int NIM_MODIFY = 0x01;
        private const int NIM_DELETE = 0x02;
        private const int NIF_ICON = 0x02;
        private const int NIF_TIP = 0x04;
        private const int NIF_MESSAGE = 0x01;
        private const int NIF_INFO = 0x10;

        private const string AutoStartRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "StormRemoteControl";

        private bool _iconAdded;

        public event Action? ShowWindowRequested;
        public event Action? ExitRequested;

        /// <summary>Register or unregister Windows autostart.</summary>
        public static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true);
                if (key == null) return;

                if (enable)
                {
                    string exePath = Environment.ProcessPath ?? "";
                    if (!string.IsNullOrEmpty(exePath))
                        key.SetValue(AppName, $"\"{exePath}\" --minimized");
                }
                else
                {
                    key.DeleteValue(AppName, throwOnMissingValue: false);
                }

                Debug.WriteLine($"[STORM Tray] AutoStart {(enable ? "enabled" : "disabled")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM Tray] AutoStart error: {ex.Message}");
            }
        }

        /// <summary>Check if autostart is currently enabled.</summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        /// <summary>Update autostart based on AppSettings.</summary>
        public static void SyncAutoStartSetting()
        {
            SetAutoStart(AppSettings.AutoStart);
        }

        public void Dispose()
        {
            Debug.WriteLine("[STORM Tray] Disposed");
        }
    }
}
