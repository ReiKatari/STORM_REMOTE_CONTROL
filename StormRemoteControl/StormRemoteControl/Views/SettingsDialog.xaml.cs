// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StormRemoteControl.Models;
using Windows.UI;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Premium settings dialog for STORM REMOTE CONTROL.
    /// Built entirely in code-behind — no companion .xaml file required.
    /// Sections: Connection, Video, Security, About.
    /// </summary>
    public sealed class SettingsDialog : ContentDialog
    {
        // ── Design tokens ────────────────────────────────────────────────

        private static readonly SolidColorBrush CardBg       = new(ColorHelper.FromArgb(255, 24, 24, 28));   // #18181C
        private static readonly SolidColorBrush BorderColor   = new(ColorHelper.FromArgb(255, 40, 40, 46));   // #28282E
        private static readonly SolidColorBrush AccentBrush   = new(ColorHelper.FromArgb(255, 211, 47, 47));  // #D32F2F
        private static readonly SolidColorBrush SubtleText    = new(ColorHelper.FromArgb(180, 255, 255, 255));
        private static readonly SolidColorBrush DimText       = new(ColorHelper.FromArgb(100, 255, 255, 255));
        private static readonly SolidColorBrush GreenBrush    = new(ColorHelper.FromArgb(255, 16, 185, 129)); // #10B981
        private static readonly FontFamily      AppFont       = new("Century Gothic, Segoe UI, Arial");
        private const double SectionSpacing   = 24;
        private const double ItemSpacing      = 14;
        private const double CardPadding      = 20;
        private const double CardCornerRadius = 10;

        // ── Controls we need to read back ────────────────────────────────

        private ComboBox    _profileCombo      = null!;
        private TextBox     _portTextBox       = null!;
        private PasswordBox _passwordBox       = null!;
        private TextBox     _signalingUrlTextBox = null!;
        private ComboBox    _fpsCombo          = null!;
        private ComboBox    _resolutionCombo   = null!;
        private ComboBox    _monitorCombo      = null!;
        private ToggleSwitch _confirmToggle    = null!;

        // ── Constructor ──────────────────────────────────────────────────

        public SettingsDialog()
        {
            // Dialog chrome
            Title                = BuildDialogTitle();
            PrimaryButtonText    = "СОХРАНИТЬ";
            CloseButtonText      = "ОТМЕНА";
            DefaultButton        = ContentDialogButton.Primary;
            RequestedTheme       = ElementTheme.Dark;

            // Apply app font to button text
            Resources["ContentDialogButtonFontFamily"] = AppFont;

            // Build content
            Content = BuildContent();

            // Wire save
            PrimaryButtonClick += OnSaveClicked;
        }

        // ── Dialog title with STORM branding ─────────────────────────────

        private static StackPanel BuildDialogTitle()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };

            panel.Children.Add(new FontIcon
            {
                Glyph      = "\uE713",  // Settings gear
                FontSize   = 20,
                Foreground = AccentBrush
            });

            panel.Children.Add(new TextBlock
            {
                Text         = "НАСТРОЙКИ",
                FontFamily   = AppFont,
                FontWeight   = FontWeights.Bold,
                FontSize     = 20,
                Foreground   = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            return panel;
        }

        // ── Main content builder ─────────────────────────────────────────

        private ScrollViewer BuildContent()
        {
            var root = new StackPanel { Spacing = SectionSpacing, Width = 440 };

            root.Children.Add(BuildConnectionSection());
            root.Children.Add(BuildVideoSection());
            root.Children.Add(BuildSecuritySection());
            root.Children.Add(BuildAboutSection());

            return new ScrollViewer
            {
                Content              = root,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight            = 580,
                Padding              = new Thickness(0, 4, 12, 0)
            };
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 1 — ПОДКЛЮЧЕНИЕ (Connection)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildConnectionSection()
        {
            var stack = new StackPanel { Spacing = ItemSpacing };

            // — Profile
            _profileCombo = new ComboBox
            {
                FontFamily        = AppFont,
                FontWeight        = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _profileCombo.Items.Add("Производительность");
            _profileCombo.Items.Add("Баланс");
            _profileCombo.Items.Add("Качество");
            _profileCombo.SelectedItem = AppSettings.ConnectionProfile;
            if (_profileCombo.SelectedIndex < 0) _profileCombo.SelectedIndex = 1;

            stack.Children.Add(MakeField("ПРОФИЛЬ ПОДКЛЮЧЕНИЯ", _profileCombo));

            // — Port
            _portTextBox = new TextBox
            {
                Text              = AppSettings.Port.ToString(),
                FontFamily        = AppFont,
                FontWeight        = FontWeights.Bold,
                PlaceholderText   = "17700",
                MaxLength         = 5
            };
            stack.Children.Add(MakeField("ПОРТ", _portTextBox));

            // — Password
            _passwordBox = new PasswordBox
            {
                Password          = AppSettings.ConnectionPassword,
                FontFamily        = AppFont,
                PlaceholderText   = "НЕ ЗАДАН",
                PasswordRevealMode = PasswordRevealMode.Peek
            };
            stack.Children.Add(MakeField("ПАРОЛЬ ПОДКЛЮЧЕНИЯ", _passwordBox));

            // — Signaling URL
            _signalingUrlTextBox = new TextBox
            {
                Text              = AppSettings.SignalingUrl,
                FontFamily        = AppFont,
                FontWeight        = FontWeights.Bold,
                PlaceholderText   = "wss://storm-signal-prod.loca.lt/ws"
            };
            stack.Children.Add(MakeField("АДРЕС СИГНАЛЬНОГО СЕРВЕРА", _signalingUrlTextBox));

            return WrapInCard("\uE8AF", "ПОДКЛЮЧЕНИЕ", stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 2 — ВИДЕО (Video)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildVideoSection()
        {
            var stack = new StackPanel { Spacing = ItemSpacing };

            // — Monitor selector
            _monitorCombo = new ComboBox
            {
                FontFamily        = AppFont,
                FontWeight        = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var monitors = EnumerateMonitors();
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                string label = $"Монитор {i + 1}: {m.Width}x{m.Height}";
                if (m.IsPrimary) label += " (ОСНОВНОЙ)";
                _monitorCombo.Items.Add(label);
            }
            if (_monitorCombo.Items.Count == 0)
                _monitorCombo.Items.Add("Монитор 1: Не определён");
            int savedIdx = AppSettings.SelectedMonitor;
            _monitorCombo.SelectedIndex = savedIdx < _monitorCombo.Items.Count ? savedIdx : 0;

            stack.Children.Add(MakeField("МОНИТОР ДЛЯ ЗАХВАТА", _monitorCombo));

            // — Max FPS
            _fpsCombo = new ComboBox
            {
                FontFamily        = AppFont,
                FontWeight        = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _fpsCombo.Items.Add("15");
            _fpsCombo.Items.Add("30");
            _fpsCombo.Items.Add("60");
            _fpsCombo.Items.Add("90");
            _fpsCombo.Items.Add("120");
            _fpsCombo.Items.Add("144");
            _fpsCombo.SelectedItem = AppSettings.MaxFps.ToString();
            if (_fpsCombo.SelectedIndex < 0) _fpsCombo.SelectedIndex = 1;

            stack.Children.Add(MakeField("МАКС. FPS", _fpsCombo));

            // — Max Resolution
            _resolutionCombo = new ComboBox
            {
                FontFamily        = AppFont,
                FontWeight        = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _resolutionCombo.Items.Add("Нативное");
            _resolutionCombo.Items.Add("3840x2160 (4K)");
            _resolutionCombo.Items.Add("2560x1440 (2K)");
            _resolutionCombo.Items.Add("1920x1200 (WUXGA)");
            _resolutionCombo.Items.Add("1920x1080 (Full HD)");
            _resolutionCombo.Items.Add("1600x900 (HD+)");
            _resolutionCombo.Items.Add("1366x768 (WXGA)");
            _resolutionCombo.Items.Add("1280x720 (HD)");
            _resolutionCombo.Items.Add("960x540 (qHD)");
            _resolutionCombo.SelectedItem = AppSettings.MaxResolution;
            // Fallback: try matching just the resolution part
            if (_resolutionCombo.SelectedIndex < 0)
            {
                for (int i = 0; i < _resolutionCombo.Items.Count; i++)
                {
                    if (_resolutionCombo.Items[i] is string s && s.StartsWith(AppSettings.MaxResolution))
                    {
                        _resolutionCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (_resolutionCombo.SelectedIndex < 0) _resolutionCombo.SelectedIndex = 0;

            stack.Children.Add(MakeField("МАКС. РАЗРЕШЕНИЕ", _resolutionCombo));

            return WrapInCard("\uE714", "ВИДЕО", stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 3 — БЕЗОПАСНОСТЬ (Security)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildSecuritySection()
        {
            var stack = new StackPanel { Spacing = ItemSpacing };

            // — Encryption (always on, read-only)
            var encryptionToggle = new ToggleSwitch
            {
                IsOn       = true,
                IsEnabled  = false,
                OnContent  = MakeToggleLabel("AES-256-GCM"),
                OffContent = MakeToggleLabel("ВЫКЛ")
            };
            stack.Children.Add(MakeToggleRow("ШИФРОВАНИЕ", encryptionToggle));

            // — Confirm before accepting
            _confirmToggle = new ToggleSwitch
            {
                IsOn       = AppSettings.RequireConfirmation,
                OnContent  = MakeToggleLabel("ВКЛ"),
                OffContent = MakeToggleLabel("ВЫКЛ")
            };
            stack.Children.Add(MakeToggleRow("ПОДТВЕРЖДЕНИЕ ПОДКЛЮЧЕНИЯ", _confirmToggle));

            return WrapInCard("\uE72E", "БЕЗОПАСНОСТЬ", stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 4 — О ПРОГРАММЕ (About)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildAboutSection()
        {
            var stack = new StackPanel { Spacing = 8 };

            // App name
            stack.Children.Add(new TextBlock
            {
                Text       = "STORM REMOTE CONTROL",
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize   = 16,
                Foreground = AccentBrush
            });

            // Version
            string version = "0.6.1";
            stack.Children.Add(new TextBlock
            {
                Text       = $"ВЕРСИЯ {version}",
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize   = 12,
                Foreground = SubtleText
            });

            // Build info
            string buildDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
            stack.Children.Add(new TextBlock
            {
                Text       = $"BUILD {buildDate} • {arch} • .NET {Environment.Version}",
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize   = 11,
                Foreground = DimText
            });

            // Device ID
            AppSettings.InitializeDeviceId();
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 6,
                Margin      = new Thickness(0, 6, 0, 0),
                Children    =
                {
                    new TextBlock
                    {
                        Text       = "ID:",
                        FontFamily = AppFont,
                        FontWeight = FontWeights.Bold,
                        FontSize   = 12,
                        Foreground = DimText,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text       = AppSettings.DeviceId,
                        FontFamily = AppFont,
                        FontWeight = FontWeights.Bold,
                        FontSize   = 13,
                        Foreground = GreenBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTextSelectionEnabled = true
                    }
                }
            });

            return WrapInCard("\uE946", "О ПРОГРАММЕ", stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Save handler
        // ══════════════════════════════════════════════════════════════════

        private void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // Profile
            if (_profileCombo.SelectedItem is string profile)
            {
                AppSettings.ConnectionProfile = profile;
            }

            // Port
            if (int.TryParse(_portTextBox.Text, out int port) && port > 0 && port <= 65535)
            {
                AppSettings.Port = port;
            }

            // Password
            AppSettings.ConnectionPassword = _passwordBox.Password ?? "";

            // Signaling URL
            AppSettings.SignalingUrl = _signalingUrlTextBox.Text;

            // FPS
            if (_fpsCombo.SelectedItem is string fpsStr && int.TryParse(fpsStr, out int fps))
            {
                AppSettings.MaxFps = fps;
            }

            // Resolution
            if (_resolutionCombo.SelectedItem is string res)
            {
                AppSettings.MaxResolution = res;
            }

            // Monitor
            AppSettings.SelectedMonitor = _monitorCombo.SelectedIndex;

            // Confirmation toggle
            AppSettings.RequireConfirmation = _confirmToggle.IsOn;

            // Persist to disk
            AppSettings.Save();
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI helper methods
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Wraps a section body in a dark card with icon + title header.</summary>
        private Border WrapInCard(string iconGlyph, string title, StackPanel body)
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                Margin      = new Thickness(0, 0, 0, 12)
            };

            header.Children.Add(new FontIcon
            {
                Glyph      = iconGlyph,
                FontSize   = 14,
                Foreground = AccentBrush
            });

            header.Children.Add(new TextBlock
            {
                Text       = title,
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize   = 13,
                Foreground = AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var wrapper = new StackPanel();
            wrapper.Children.Add(header);
            wrapper.Children.Add(body);

            return new Border
            {
                Background   = CardBg,
                BorderBrush  = BorderColor,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(CardCornerRadius),
                Padding      = new Thickness(CardPadding),
                Child        = wrapper
            };
        }

        /// <summary>Creates a labelled field (label + control stacked vertically).</summary>
        private static StackPanel MakeField(string label, UIElement control)
        {
            return new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text       = label,
                        FontFamily = AppFont,
                        FontWeight = FontWeights.Bold,
                        FontSize   = 11,
                        Foreground = SubtleText
                    },
                    control
                }
            };
        }

        /// <summary>Creates a horizontal row with label on the left and toggle on the right.</summary>
        private static Grid MakeToggleRow(string label, ToggleSwitch toggle)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text              = label,
                FontFamily        = AppFont,
                FontWeight        = FontWeights.Bold,
                FontSize          = 12,
                Foreground        = SubtleText,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping      = TextWrapping.Wrap
            };

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(toggle, 1);

            grid.Children.Add(labelBlock);
            grid.Children.Add(toggle);

            return grid;
        }

        /// <summary>Creates a styled text label for ToggleSwitch on/off content.</summary>
        private static TextBlock MakeToggleLabel(string text)
        {
            return new TextBlock
            {
                Text       = text,
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize   = 11
            };
        }

        // ══════════════════════════════════════════════════════════════════
        //  Monitor enumeration via Win32 P/Invoke
        // ══════════════════════════════════════════════════════════════════

        private record MonitorInfo(int Width, int Height, bool IsPrimary, string DeviceName);

        private static List<MonitorInfo> EnumerateMonitors()
        {
            var monitors = new List<MonitorInfo>();

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                    (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                    {
                        var mi = new MONITORINFOEX();
                        mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();
                        if (GetMonitorInfo(hMonitor, ref mi))
                        {
                            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                            int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                            bool primary = (mi.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
                            monitors.Add(new MonitorInfo(w, h, primary, mi.szDevice ?? ""));
                        }
                        return true;
                    }, IntPtr.Zero);
            }
            catch
            {
                // Fallback if enumeration fails
            }

            // Sort: primary first
            monitors.Sort((a, b) => b.IsPrimary.CompareTo(a.IsPrimary));
            return monitors;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }
    }
}
