using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Top-level shell window with native Win32 system tray icon.
    ///
    /// Behaviour:
    ///   • Close (X) → hides to system tray, app keeps running
    ///   • Minimize (—) → standard minimize to taskbar
    ///   • Tray double-click → restore window
    ///   • Tray right-click → context menu: "Развернуть" / "Выход"
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private bool _isExplicitExit;
        private bool _trayNotificationShown;
        private TrayIcon? _tray;

        public MainWindow()
        {
            try 
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash_log.txt"), "INNER EXCEPTION:\n" + ex.ToString() + "\n\n" + (ex.InnerException?.ToString() ?? "NO INNER"));
                throw;
            }

            try
            {
                if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                {
                    this.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
                    {
                        Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt
                    };
                }
                else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
                {
                    this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                }
            }
            catch { }

            ConfigureWindowDimensions();
            ExtendsContentIntoTitleBar = true;
            RootFrame.Navigate(typeof(MainPage));

            // Create native tray icon
            _tray = new TrayIcon(this);
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        private const int SW_RESTORE = 9;

        internal void RestoreWindow()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AppWindow.Show();
                if (AppWindow.Presenter is OverlappedPresenter p &&
                    p.State == OverlappedPresenterState.Minimized)
                {
                    p.Restore();
                }
                Activate();

                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                catch { }
            });
        }

        internal void ExitApplication()
        {
            _isExplicitExit = true;
            _tray?.Dispose();
            _tray = null;
            DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            if (!_isExplicitExit)
            {
                args.Handled = true;
                AppWindow.Hide();

                if (!_trayNotificationShown && _tray != null)
                {
                    _trayNotificationShown = true;
                    _tray.ShowBalloon(
                        "STORM REMOTE CONTROL",
                        "Приложение свёрнуто в системный трей.\nДважды кликните по значку для восстановления.");
                }
            }
        }

        private void ConfigureWindowDimensions()
        {
            try
            {
                AppWindow.SetIcon("Assets/AppIcon.ico");
                AppWindow.Resize(new Windows.Graphics.SizeInt32(1150, 720));

                var da = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                if (da != null)
                {
                    var cx = (da.WorkArea.Width - AppWindow.Size.Width) / 2;
                    var cy = (da.WorkArea.Height - AppWindow.Size.Height) / 2;
                    AppWindow.Move(new Windows.Graphics.PointInt32(cx, cy));
                }
            }
            catch { }
        }
    }

    #region Native Win32 Tray Icon

    /// <summary>
    /// Lightweight Win32 Shell_NotifyIcon wrapper — no WPF/WinForms dependencies.
    /// Creates a hidden message-only window to receive tray callbacks.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private const uint WM_APP_TRAY = 0x8001;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_LBUTTONDBLCLK = 0x0203;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_DESTROY = 0x0002;

        private const uint NIF_MESSAGE = 0x01;
        private const uint NIF_ICON = 0x02;
        private const uint NIF_TIP = 0x04;
        private const uint NIF_INFO = 0x10;
        private const uint NIM_ADD = 0x00;
        private const uint NIM_MODIFY = 0x01;
        private const uint NIM_DELETE = 0x02;
        private const uint NIIF_INFO = 0x01;

        private const uint IDM_SHOW = 1001;
        private const uint IDM_EXIT = 1002;

        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_NONOTIFY = 0x0080;
        private const uint TPM_RETURNCMD = 0x0100;

        private readonly MainWindow _owner;
        private readonly IntPtr _hwnd;
        private readonly WndProcDelegate _wndProc;
        private bool _disposed;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
            uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
            int nReserved, IntPtr hwnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type,
            int cx, int cy, uint fuLoad);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x0010;
        private const uint LR_DEFAULTSIZE = 0x0040;
        private const uint MF_STRING = 0x0000;
        private const uint MF_SEPARATOR = 0x0800;

        public TrayIcon(MainWindow owner)
        {
            _owner = owner;

            // Must prevent GC of the delegate while the window is alive
            _wndProc = WndProc;

            var hInstance = GetModuleHandle(null);
            var className = "StormTrayWnd_" + Environment.TickCount;

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = className
            };
            RegisterClassEx(ref wc);

            // Message-only window (HWND_MESSAGE parent)
            _hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0,
                new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, hInstance, IntPtr.Zero);

            // Load .ico from app directory
            IntPtr hIcon = IntPtr.Zero;
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icoPath))
            {
                hIcon = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0,
                    LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }

            var nid = MakeNid();
            nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
            nid.uCallbackMessage = WM_APP_TRAY;
            nid.hIcon = hIcon;
            nid.szTip = "STORM REMOTE CONTROL";
            Shell_NotifyIcon(NIM_ADD, ref nid);
        }

        public void ShowBalloon(string title, string text)
        {
            var nid = MakeNid();
            nid.uFlags = NIF_INFO;
            nid.szInfoTitle = title;
            nid.szInfo = text;
            nid.dwInfoFlags = NIIF_INFO;
            nid.uTimeoutOrVersion = 3000;
            Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }

        private NOTIFYICONDATA MakeNid()
        {
            var nid = new NOTIFYICONDATA();
            nid.cbSize = Marshal.SizeOf<NOTIFYICONDATA>();
            nid.hWnd = _hwnd;
            nid.uID = 1;
            return nid;
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_APP_TRAY)
            {
                uint trayMsg = (uint)(lParam.ToInt64() & 0xFFFF);

                if (trayMsg == WM_LBUTTONDBLCLK || trayMsg == WM_LBUTTONUP)
                {
                    _owner.RestoreWindow();
                }
                else if (trayMsg == WM_RBUTTONUP)
                {
                    ShowContextMenu();
                }
                return IntPtr.Zero;
            }

            if (msg == WM_COMMAND)
            {
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (id == IDM_SHOW) _owner.RestoreWindow();
                if (id == IDM_EXIT) _owner.ExitApplication();
                return IntPtr.Zero;
            }

            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            var hMenu = CreatePopupMenu();
            AppendMenu(hMenu, MF_STRING, IDM_SHOW, "Развернуть");
            AppendMenu(hMenu, MF_SEPARATOR, 0, "");
            AppendMenu(hMenu, MF_STRING, IDM_EXIT, "Выход");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.x, pt.y, 0, _hwnd, IntPtr.Zero);
            DestroyMenu(hMenu);

            if (cmd == IDM_SHOW) _owner.RestoreWindow();
            else if (cmd == IDM_EXIT) _owner.ExitApplication();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var nid = MakeNid();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        }
    }

    #endregion
}
