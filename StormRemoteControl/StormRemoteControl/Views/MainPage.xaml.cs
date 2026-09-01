// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StormRemoteControl.Models;
using StormRemoteControl.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Primary landing page — ID-based host/client dashboard.
    /// Fully localized (6 languages), themed (8 themes), styled with Sentence case.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private RemoteSessionService _sessionService;
        private bool _hostActive = false;

        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += OnPageLoaded;
            this.Unloaded += OnPageUnloaded;
            _sessionService = new RemoteSessionService();

            LocalizationService.LanguageChanged += OnLanguageChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            UpdateLocalizedTexts();
            PulsingStoryboard.Begin();

            AppSettings.InitializeDeviceId();
            DeviceIdText.Text = AppSettings.DeviceId;

            UpdatePasswordIndicator();
            SetupTitleBar();

            await CheckNetworkStatusAsync();

            _sessionService.PublicEndpointDiscovered += OnPublicEndpointDiscovered;
            _sessionService.StateChanged += OnHostSessionStateChanged;

            StartHostWithRetryAsync();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            PulsingStoryboard.Stop();
            _sessionService.PublicEndpointDiscovered -= OnPublicEndpointDiscovered;
            _sessionService.StateChanged -= OnHostSessionStateChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        private void OnLanguageChanged()
        {
            DispatcherQueue.TryEnqueue(UpdateLocalizedTexts);
        }

        private void OnThemeChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ThemeManager.ApplyTheme(ThemeManager.CurrentTheme);
            });
        }

        private void UpdateLocalizedTexts()
        {
            FileManagerButtonText.Text = LocalizationService.Get("Files");
            SettingsButtonText.Text = LocalizationService.Get("Settings");
            InfoButtonText.Text = LocalizationService.Get("Info");

            YourComputerTitle.Text = LocalizationService.Get("YourComputer");
            HostDescText.Text = LocalizationService.Get("HostDescription");
            YourIdSubText.Text = LocalizationService.Get("YourId");
            PasswordProtectedText.Text = LocalizationService.Get("ProtectedByPassword");
            CopyIdButtonText.Text = LocalizationService.Get("CopyId");
            ChangeIdButtonText.Text = LocalizationService.Get("ChangeId");

            RemoteControlTitle.Text = LocalizationService.Get("RemoteControl");
            ClientDescText.Text = LocalizationService.Get("ClientDescription");
            PartnerIdTextBox.PlaceholderText = LocalizationService.Get("PartnerIdPlaceholder");
            PartnerPasswordBox.PlaceholderText = LocalizationService.Get("PartnerPasswordPlaceholder");
            ConnectButtonText.Text = LocalizationService.Get("Connect");

            RecentConnectionsTitle.Text = LocalizationService.Get("RecentConnections");
            NoRecentConnectionsText.Text = LocalizationService.Get("NoRecentConnections");
            RecentConnectionsDescText.Text = LocalizationService.Get("RecentConnectionsDesc");

            if (!_hostActive)
            {
                HostStatusText.Text = LocalizationService.Get("StatusReady");
            }
        }

        private void SetupTitleBar()
        {
            try
            {
                var window = App.MainWindow;
                if (window != null)
                {
                    window.SetTitleBar(CustomTitleBar);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] TitleBar setup: {ex.Message}");
            }
        }

        private async Task CheckNetworkStatusAsync()
        {
            try
            {
                bool isConnected = NetworkInterface.GetIsNetworkAvailable();

                if (isConnected)
                {
                    try
                    {
                        var addresses = await Dns.GetHostAddressesAsync("stun.l.google.com");
                        if (addresses.Length > 0)
                        {
                            NetworkStatusText.Text = LocalizationService.Get("StatusOnline");
                            SetNetworkIndicator(true);
                        }
                        else
                        {
                            NetworkStatusText.Text = LocalizationService.Get("StatusNoInternet");
                            SetNetworkIndicator(false);
                        }
                    }
                    catch
                    {
                        NetworkStatusText.Text = LocalizationService.Get("StatusLocal");
                        SetNetworkIndicator(true);
                    }
                }
                else
                {
                    NetworkStatusText.Text = LocalizationService.Get("StatusOffline");
                    SetNetworkIndicator(false);
                }
            }
            catch
            {
                NetworkStatusText.Text = LocalizationService.Get("StatusUndetermined");
                SetNetworkIndicator(false);
            }
        }

        private void SetNetworkIndicator(bool online)
        {
            var color = online
                ? Color.FromArgb(255, 16, 185, 129)
                : Color.FromArgb(255, 211, 47, 47);
            var brush = new SolidColorBrush(color);
            NetworkPulseDot.Fill = brush;
            NetworkStatusText.Foreground = brush;
        }

        private void UpdatePasswordIndicator()
        {
            PasswordInfoPanel.Visibility = !string.IsNullOrEmpty(AppSettings.ConnectionPassword)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnPublicEndpointDiscovered(IPEndPoint publicEp)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                NetworkStatusText.Text = LocalizationService.Get("StatusOnline");
                SetNetworkIndicator(true);
            });
        }

        private async void StartHostWithRetryAsync()
        {
            if (_hostActive) return;

            HostStatusText.Text = LocalizationService.Get("StatusStarting");
            HostStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));

            bool success = await _sessionService.InitializeHostAsync(AppSettings.Port);

            if (success)
            {
                _hostActive = true;
                HostStatusText.Text = LocalizationService.Get("StatusActive");
                HostStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
            }
            else
            {
                HostStatusText.Text = LocalizationService.Get("StatusErrorRetry");
                HostStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 211, 47, 47));

                await Task.Delay(5000);
                StartHostWithRetryAsync();
            }
        }

        private void OnHostSessionStateChanged(SessionState state)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                switch (state)
                {
                    case SessionState.WaitingForPeer:
                        HostStatusText.Text = LocalizationService.Get("StatusWaiting");
                        HostStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
                        break;
                    case SessionState.Connected:
                        HostStatusText.Text = LocalizationService.Get("StatusConnected");
                        HostStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 16, 185, 129));
                        
                        if (_hostActive)
                        {
                            var dialog = new ContentDialog
                            {
                                Title = LocalizationService.Get("DialogIncomingConnTitle"),
                                Content = LocalizationService.Get("DialogIncomingConnContent"),
                                CloseButtonText = LocalizationService.Get("Close"),
                                XamlRoot = this.XamlRoot,
                                RequestedTheme = ElementTheme.Dark
                            };
                            _ = dialog.ShowAsync();
                        }
                        break;
                    case SessionState.Failed:
                    case SessionState.Disconnected:
                        HostStatusText.Text = LocalizationService.Get("StatusDisconnected");
                        HostStatusBadge.Background = new SolidColorBrush(Color.FromArgb(40, 211, 47, 47));
                        
                        _hostActive = false;
                        _sessionService.Disconnect();
                        Task.Delay(2000).ContinueWith(_ => DispatcherQueue.TryEnqueue(StartHostWithRetryAsync));
                        break;
                }
            });
        }

        private void OnCopyIdClicked(object sender, RoutedEventArgs e)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(AppSettings.DeviceId);
            Clipboard.SetContent(dataPackage);
        }

        private async void OnConnectButtonClicked(object sender, RoutedEventArgs e)
        {
            string partnerId = PartnerIdTextBox.Text?.Trim() ?? string.Empty;
            partnerId = partnerId.Replace(" ", "");

            if (string.IsNullOrEmpty(partnerId))
            {
                ShowVisualError(LocalizationService.Get("ErrorInvalidId"), LocalizationService.Get("ErrorInvalidIdContent"));
                return;
            }

            SetInputControlsState(false);

            try
            {
                var clientService = new RemoteSessionService();
                bool success = await clientService.ConnectAsync(partnerId);

                if (success)
                {
                    this.Frame.Navigate(typeof(SessionPage), clientService);
                }
                else
                {
                    ShowVisualError(LocalizationService.Get("ErrorConnection"), LocalizationService.Get("ErrorConnectionContent"));
                    clientService.Dispose();
                    SetInputControlsState(true);
                }
            }
            catch (Exception ex)
            {
                ShowVisualError(LocalizationService.Get("ErrorNetwork"), $"{LocalizationService.Get("ErrorConnectionContent")}\n({ex.Message})");
                SetInputControlsState(true);
            }
        }

        private void OnFileManagerClicked(object sender, RoutedEventArgs e)
        {
            this.Frame.Navigate(typeof(FileManagerPage));
        }

        private async void OnSettingsClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog
            {
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                DeviceIdText.Text = AppSettings.DeviceId;
                UpdatePasswordIndicator();
                UpdateLocalizedTexts();
            }
        }

        private async void OnInfoClicked(object sender, RoutedEventArgs e)
        {
            var content = new StackPanel { Spacing = 18, MinWidth = 520 };

            // SECTION: ENCRYPTION
            var encSection = new StackPanel { Spacing = 6 };
            encSection.Children.Add(CreateInfoHeader("\uE72E", LocalizationService.Get("SecEncryption")));
            encSection.Children.Add(CreateInfoRow("Алгоритм", "AES-256-GCM"));
            encSection.Children.Add(CreateInfoRow("Обмен ключами", "ECDH P-256 (Elliptic Curve Diffie-Hellman)"));
            encSection.Children.Add(CreateInfoRow("Nonce", "12 байт, счётчик и случайный префикс"));
            encSection.Children.Add(CreateInfoRow("Тег аутентификации", "16 байт (GCM Tag)"));
            encSection.Children.Add(CreateInfoRow("Аутентификация", "HMAC-SHA256 challenge-response"));
            content.Children.Add(WrapSection(encSection));

            // SECTION: NAT TRAVERSAL
            var natSection = new StackPanel { Spacing = 6 };
            natSection.Children.Add(CreateInfoHeader("\uE774", LocalizationService.Get("SecNat")));
            natSection.Children.Add(CreateInfoRow("Протокол", "STUN (RFC 5389) и UDP Hole Punching"));
            natSection.Children.Add(CreateInfoRow("STUN-сервер", "stun.l.google.com:19302"));
            natSection.Children.Add(CreateInfoRow("Совместимость", "~80% типов NAT"));
            natSection.Children.Add(CreateInfoRow("Fallback", "TURN-релей (при Symmetric NAT)"));
            natSection.Children.Add(CreateInfoRow("Сигнализация", "WebSocket (JSON-протокол)"));
            content.Children.Add(WrapSection(natSection));

            // SECTION: VIDEO STREAM
            var videoSection = new StackPanel { Spacing = 6 };
            videoSection.Children.Add(CreateInfoHeader("\uE7F4", LocalizationService.Get("SecVideoStream")));
            videoSection.Children.Add(CreateInfoRow("Захват экрана", "Windows.Graphics.Capture API"));
            videoSection.Children.Add(CreateInfoRow("Кодирование", "H.264/H.265 аппаратное (GPU)"));
            videoSection.Children.Add(CreateInfoRow("Encoder", "Media Foundation (NVIDIA NVENC / AMD AMF / Intel QSV)"));
            videoSection.Children.Add(CreateInfoRow("Fallback", "JPEG тайловая дельта-компрессия (CPU)"));
            videoSection.Children.Add(CreateInfoRow("Макс. FPS", "До 144 (адаптивный)"));
            videoSection.Children.Add(CreateInfoRow("Битрейт", "Адаптивный, 2–20 Мбит/с по нагрузке"));
            content.Children.Add(WrapSection(videoSection));

            // SECTION: NETWORK
            var netSection = new StackPanel { Spacing = 6 };
            netSection.Children.Add(CreateInfoHeader("\uE968", LocalizationService.Get("SecNetwork")));
            netSection.Children.Add(CreateInfoRow("Транспорт", "UDP (собственный протокол STORM)"));
            netSection.Children.Add(CreateInfoRow("Порт", $"{AppSettings.Port}"));
            netSection.Children.Add(CreateInfoRow("MTU", "1400 байт"));
            netSection.Children.Add(CreateInfoRow("Keepalive", "Каждые 5 секунд"));
            netSection.Children.Add(CreateInfoRow("Таймаут", "15 секунд"));
            content.Children.Add(WrapSection(netSection));

            // SECTION: FEATURES
            var featSection = new StackPanel { Spacing = 6 };
            featSection.Children.Add(CreateInfoHeader("\uE74C", LocalizationService.Get("SecFeatures")));
            featSection.Children.Add(CreateInfoRow("Буфер обмена", "Двусторонняя синхронизация текста"));
            featSection.Children.Add(CreateInfoRow("Файловый менеджер", "Двухоконный, drag & drop, горячие клавиши"));
            featSection.Children.Add(CreateInfoRow("Чат", "Текстовый чат во время сессии"));
            featSection.Children.Add(CreateInfoRow("Wake-on-LAN", "Удалённое включение ПК"));
            featSection.Children.Add(CreateInfoRow("Горячие клавиши", "Ctrl+Alt+Del, Alt+Tab, Win"));
            featSection.Children.Add(CreateInfoRow("Темы оформления", "8 фирменных тем STORM SOFT"));
            featSection.Children.Add(CreateInfoRow("Локализация", "100% перевод на 6 языков"));
            content.Children.Add(WrapSection(featSection));

            var scrollViewer = new ScrollViewer
            {
                Content = content,
                MaxHeight = 500,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 12, 0)
            };

            var dialog = new ContentDialog
            {
                Title = LocalizationService.Get("TechInfoTitle"),
                Content = scrollViewer,
                CloseButtonText = LocalizationService.Get("Close"),
                XamlRoot = this.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };
            await dialog.ShowAsync();
        }

        private static Border WrapSection(StackPanel section)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Child = section
            };
        }

        private static StackPanel CreateInfoHeader(string glyph, string title)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = glyph, FontSize = 16,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
            });
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 13,
                Foreground = (SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
            });
            return sp;
        }

        private static Grid CreateInfoRow(string label, string value)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lbl = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);

            var val = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(val, 1);

            grid.Children.Add(lbl);
            grid.Children.Add(val);
            return grid;
        }

        private async void OnChangeIdClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = LocalizationService.Get("DialogChangeIdTitle"),
                Content = LocalizationService.Get("DialogChangeIdContent"),
                PrimaryButtonText = LocalizationService.Get("ChangeId"),
                CloseButtonText = LocalizationService.Get("Cancel"),
                XamlRoot = this.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                AppSettings.RegenerateDeviceId();
                DeviceIdText.Text = AppSettings.DeviceId;
            }
        }

        private async void ShowVisualError(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = LocalizationService.Get("Close"),
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private void SetInputControlsState(bool isEnabled)
        {
            PartnerIdTextBox.IsEnabled = isEnabled;
            PartnerPasswordBox.IsEnabled = isEnabled;
            ConnectButton.IsEnabled = isEnabled;
        }
    }
}
