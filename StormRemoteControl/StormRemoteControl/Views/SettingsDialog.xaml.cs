// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StormRemoteControl.Models;
using StormRemoteControl.Services;
using Windows.UI;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Premium settings dialog for STORM REMOTE CONTROL.
    /// Supports 8 visual themes, 6 language options, connection, video and security settings.
    /// </summary>
    public sealed class SettingsDialog : ContentDialog
    {
        private static readonly FontFamily AppFont = new("Century Gothic, Segoe UI, Arial");
        private const double SectionSpacing = 20;
        private const double ItemSpacing = 12;
        private const double CardPadding = 18;
        private const double CardCornerRadius = 10;

        // ── Controls we read back ────────────────────────────────────────

        private ComboBox _languageCombo = null!;
        private ComboBox _themeCombo = null!;
        private ComboBox _profileCombo = null!;
        private TextBox _portTextBox = null!;
        private PasswordBox _passwordBox = null!;
        private TextBox _signalingUrlTextBox = null!;
        private ComboBox _fpsCombo = null!;
        private ComboBox _resolutionCombo = null!;
        private ComboBox _monitorCombo = null!;
        private ToggleSwitch _confirmToggle = null!;

        public SettingsDialog()
        {
            Title = BuildDialogTitle();
            PrimaryButtonText = LocalizationService.Get("Save");
            CloseButtonText = LocalizationService.Get("Cancel");
            DefaultButton = ContentDialogButton.Primary;
            RequestedTheme = ElementTheme.Dark;

            Resources["ContentDialogButtonFontFamily"] = AppFont;

            Content = BuildContent();
            PrimaryButtonClick += OnSaveClicked;
        }

        private static StackPanel BuildDialogTitle()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };

            panel.Children.Add(new FontIcon
            {
                Glyph = "\uE713",
                FontSize = 20,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
            });

            panel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Get("SettingsTitle"),
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize = 19,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });

            return panel;
        }

        private ScrollViewer BuildContent()
        {
            var root = new StackPanel { Spacing = SectionSpacing, Width = 480 };

            root.Children.Add(BuildAppearanceSection());
            root.Children.Add(BuildConnectionSection());
            root.Children.Add(BuildVideoSection());
            root.Children.Add(BuildSecuritySection());
            root.Children.Add(BuildAboutSection());

            return new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 600,
                Padding = new Thickness(0, 4, 12, 0)
            };
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 1 — ОФОРМЛЕНИЕ И ЯЗЫК (Appearance & Language)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildAppearanceSection()
        {
            var stack = new StackPanel { Spacing = ItemSpacing };

            // — Visual Theme Selector
            _themeCombo = new ComboBox
            {
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            foreach (var kv in ThemeManager.Themes)
            {
                _themeCombo.Items.Add($"{kv.Value.Name} — {kv.Value.Description}");
            }

            int themeIdx = 0;
            int curIdx = 0;
            foreach (var key in ThemeManager.Themes.Keys)
            {
                if (key == ThemeManager.CurrentTheme) { themeIdx = curIdx; break; }
                curIdx++;
            }
            _themeCombo.SelectedIndex = themeIdx;

            _themeCombo.SelectionChanged += (s, e) =>
            {
                if (_themeCombo.SelectedIndex >= 0)
                {
                    int i = 0;
                    foreach (var key in ThemeManager.Themes.Keys)
                    {
                        if (i == _themeCombo.SelectedIndex)
                        {
                            ThemeManager.ApplyTheme(key);
                            break;
                        }
                        i++;
                    }
                }
            };

            stack.Children.Add(MakeField(LocalizationService.Get("AppTheme"), _themeCombo));

            // — Language Selector
            _languageCombo = new ComboBox
            {
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            int langIdx = 0;
            int idx = 0;
            foreach (var kv in LocalizationService.SupportedLanguages)
            {
                _languageCombo.Items.Add($"{kv.Value.Flag}  {kv.Value.NativeName} ({kv.Value.Name})");
                if (kv.Key == LocalizationService.CurrentLanguage) langIdx = idx;
                idx++;
            }
            _languageCombo.SelectedIndex = langIdx;

            _languageCombo.SelectionChanged += (s, e) =>
            {
                int i = 0;
                foreach (var key in LocalizationService.SupportedLanguages.Keys)
                {
                    if (i == _languageCombo.SelectedIndex)
                    {
                        LocalizationService.CurrentLanguage = key;
                        break;
                    }
                    i++;
                }
            };

            stack.Children.Add(MakeField(LocalizationService.Get("AppLanguage"), _languageCombo));

            return WrapInCard("\uE771", LocalizationService.Get("SectionAppearance"), stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 2 — ПОДКЛЮЧЕНИЕ (Connection)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildConnectionSection()
        {
            var stack = new StackPanel { Spacing = ItemSpacing };

            // — Profile
            _profileCombo = new ComboBox
            {
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _profileCombo.Items.Add(LocalizationService.Get("ProfilePerf"));
            _profileCombo.Items.Add(LocalizationService.Get("ProfileBalance"));
            _profileCombo.Items.Add(LocalizationService.Get("ProfileQuality"));
            _profileCombo.SelectedItem = AppSettings.ConnectionProfile;
            if (_profileCombo.SelectedIndex < 0) _profileCombo.SelectedIndex = 1;

            stack.Children.Add(MakeField(LocalizationService.Get("ConnectionProfile"), _profileCombo));

            // — Port
            _portTextBox = new TextBox
            {
                Text = AppSettings.Port.ToString(),
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                PlaceholderText = "17700",
                MaxLength = 5
            };
            stack.Children.Add(MakeField(LocalizationService.Get("Port"), _portTextBox));

            // — Password
            _passwordBox = new PasswordBox
            {
                Password = AppSettings.ConnectionPassword,
                FontFamily = AppFont,
                PlaceholderText = LocalizationService.Get("PasswordNotSet"),
                PasswordRevealMode = PasswordRevealMode.Peek
            };
            stack.Children.Add(MakeField(LocalizationService.Get("ConnectionPassword"), _passwordBox));

            // — Signaling URL
            _signalingUrlTextBox = new TextBox
            {
                Text = AppSettings.SignalingUrl,
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                PlaceholderText = "wss://storm-signal-prod.loca.lt/ws"
            };
            stack.Children.Add(MakeField(LocalizationService.Get("SignalingUrl"), _signalingUrlTextBox));

            return WrapInCard("\uE8AF", LocalizationService.Get("SectionConnection"), stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 3 — ВИДЕО (Video)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildVideoSection()
        {
            var stack = new StackPanel { Spacing = ItemSpacing };

            // — Monitor selector
            _monitorCombo = new ComboBox
            {
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var monitors = EnumerateMonitors();
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                string label = $"Монитор {i + 1}: {m.Width}x{m.Height}";
                if (m.IsPrimary) label += $" {LocalizationService.Get("MonitorPrimary")}";
                _monitorCombo.Items.Add(label);
            }
            if (_monitorCombo.Items.Count == 0)
                _monitorCombo.Items.Add("Монитор 1: Не определён");
            int savedIdx = AppSettings.SelectedMonitor;
            _monitorCombo.SelectedIndex = savedIdx < _monitorCombo.Items.Count ? savedIdx : 0;

            stack.Children.Add(MakeField(LocalizationService.Get("CaptureMonitor"), _monitorCombo));

            // — Max FPS
            _fpsCombo = new ComboBox
            {
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
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

            stack.Children.Add(MakeField(LocalizationService.Get("MaxFps"), _fpsCombo));

            // — Max Resolution
            _resolutionCombo = new ComboBox
            {
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _resolutionCombo.Items.Add(LocalizationService.Get("ResNative"));
            _resolutionCombo.Items.Add("3840x2160 (4K)");
            _resolutionCombo.Items.Add("2560x1440 (2K)");
            _resolutionCombo.Items.Add("1920x1200 (WUXGA)");
            _resolutionCombo.Items.Add("1920x1080 (Full HD)");
            _resolutionCombo.Items.Add("1600x900 (HD+)");
            _resolutionCombo.Items.Add("1366x768 (WXGA)");
            _resolutionCombo.Items.Add("1280x720 (HD)");
            _resolutionCombo.Items.Add("960x540 (qHD)");
            _resolutionCombo.SelectedItem = AppSettings.MaxResolution;

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

            stack.Children.Add(MakeField(LocalizationService.Get("MaxResolution"), _resolutionCombo));

            return WrapInCard("\uE714", LocalizationService.Get("SectionVideo"), stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 4 — БЕЗОПАСНОСТЬ (Security)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildSecuritySection()
        {
            var stack = new StackPanel { Spacing = ItemSpacing };

            // — Encryption (always on, read-only)
            var encryptionToggle = new ToggleSwitch
            {
                IsOn = true,
                IsEnabled = false,
                OnContent = MakeToggleLabel("AES-256-GCM"),
                OffContent = MakeToggleLabel(LocalizationService.Get("ToggleOff"))
            };
            stack.Children.Add(MakeToggleRow(LocalizationService.Get("Encryption"), encryptionToggle));

            // — Confirm before accepting
            _confirmToggle = new ToggleSwitch
            {
                IsOn = AppSettings.RequireConfirmation,
                OnContent = MakeToggleLabel(LocalizationService.Get("ToggleOn")),
                OffContent = MakeToggleLabel(LocalizationService.Get("ToggleOff"))
            };
            stack.Children.Add(MakeToggleRow(LocalizationService.Get("RequireConfirmation"), _confirmToggle));

            return WrapInCard("\uE72E", LocalizationService.Get("SectionSecurity"), stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Section 5 — О ПРОГРАММЕ (About)
        // ══════════════════════════════════════════════════════════════════

        private Border BuildAboutSection()
        {
            var stack = new StackPanel { Spacing = 8 };

            stack.Children.Add(new TextBlock
            {
                Text = "STORM REMOTE CONTROL",
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"Версия 1.0.0",
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            string buildDate = LocalizationService.FormatDate(DateTime.Now);
            string arch = RuntimeInformation.OSArchitecture.ToString();
            stack.Children.Add(new TextBlock
            {
                Text = $"Сборка от {buildDate} • {arch} • .NET 10 / WinUI 3",
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            AppSettings.InitializeDeviceId();
            stack.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = "ID:",
                        FontFamily = AppFont,
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = AppSettings.DeviceId,
                        FontFamily = AppFont,
                        FontWeight = FontWeights.Bold,
                        FontSize = 13,
                        Foreground = (SolidColorBrush)Application.Current.Resources["PulsingGreenBrush"],
                        VerticalAlignment = VerticalAlignment.Center,
                        IsTextSelectionEnabled = true
                    }
                }
            });

            return WrapInCard("\uE946", LocalizationService.Get("SectionAbout"), stack);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Save handler
        // ══════════════════════════════════════════════════════════════════

        private void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_profileCombo.SelectedItem is string profile)
            {
                AppSettings.ConnectionProfile = profile;
            }

            if (int.TryParse(_portTextBox.Text, out int port) && port > 0 && port <= 65535)
            {
                AppSettings.Port = port;
            }

            AppSettings.ConnectionPassword = _passwordBox.Password ?? "";
            AppSettings.SignalingUrl = _signalingUrlTextBox.Text;

            if (_fpsCombo.SelectedItem is string fpsStr && int.TryParse(fpsStr, out int fps))
            {
                AppSettings.MaxFps = fps;
            }

            if (_resolutionCombo.SelectedItem is string res)
            {
                AppSettings.MaxResolution = res;
            }

            AppSettings.SelectedMonitor = _monitorCombo.SelectedIndex;
            AppSettings.RequireConfirmation = _confirmToggle.IsOn;

            // Save Theme and Language
            if (_themeCombo.SelectedIndex >= 0)
            {
                int i = 0;
                foreach (var key in ThemeManager.Themes.Keys)
                {
                    if (i == _themeCombo.SelectedIndex)
                    {
                        ThemeManager.CurrentTheme = key;
                        break;
                    }
                    i++;
                }
            }

            if (_languageCombo.SelectedIndex >= 0)
            {
                int i = 0;
                foreach (var key in LocalizationService.SupportedLanguages.Keys)
                {
                    if (i == _languageCombo.SelectedIndex)
                    {
                        LocalizationService.CurrentLanguage = key;
                        break;
                    }
                    i++;
                }
            }

            AppSettings.Save();
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI helper methods
        // ══════════════════════════════════════════════════════════════════

        private static Border WrapInCard(string iconGlyph, string title, StackPanel body)
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 0, 12)
            };

            header.Children.Add(new FontIcon
            {
                Glyph = iconGlyph,
                FontSize = 14,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
            });

            header.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"],
                VerticalAlignment = VerticalAlignment.Center
            });

            var wrapper = new StackPanel();
            wrapper.Children.Add(header);
            wrapper.Children.Add(body);

            return new Border
            {
                Background = (SolidColorBrush)Application.Current.Resources["CardBgBrush"],
                BorderBrush = (SolidColorBrush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(CardCornerRadius),
                Padding = new Thickness(CardPadding),
                Child = wrapper
            };
        }

        private static StackPanel MakeField(string label, UIElement control)
        {
            return new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        FontFamily = AppFont,
                        FontWeight = FontWeights.Bold,
                        FontSize = 11,
                        Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    },
                    control
                }
            };
        }

        private static Grid MakeToggleRow(string label, ToggleSwitch toggle)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(toggle, 1);

            grid.Children.Add(labelBlock);
            grid.Children.Add(toggle);

            return grid;
        }

        private static TextBlock MakeToggleLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = AppFont,
                FontWeight = FontWeights.Bold,
                FontSize = 11
            };
        }

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
                            bool primary = (mi.dwFlags & 1) != 0;
                            monitors.Add(new MonitorInfo(w, h, primary, mi.szDevice ?? ""));
                        }
                        return true;
                    }, IntPtr.Zero);
            }
            catch { }

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
