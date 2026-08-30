using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StormRemoteControl.Models;
using StormRemoteControl.Services;
using Windows.ApplicationModel.DataTransfer;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Primary landing page — ID-based host/client dashboard.
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
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var runningProcesses = System.Diagnostics.Process.GetProcessesByName(currentProcess.ProcessName);
            var oldProcess = System.Linq.Enumerable.FirstOrDefault(runningProcesses, p => p.Id != currentProcess.Id);

            if (oldProcess != null)
            {
                ContentDialog dialog = new ContentDialog
                {
                    Title = "УЖЕ ЗАПУЩЕНО",
                    Content = "STORM REMOTE CONTROL уже работает в фоновом режиме (возможно, свернут в трей возле часов).\n\nВы хотите закрыть старую копию и запустить новую?",
                    PrimaryButtonText = "Закрыть старую и продолжить",
                    CloseButtonText = "Отмена",
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = ElementTheme.Dark,
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    try
                    {
                        oldProcess.Kill();
                        oldProcess.WaitForExit(3000);
                    }
                    catch { }
                }
                else
                {
                    Application.Current.Exit();
                    return;
                }
            }

            PulsingStoryboard.Begin();

            // Initialize device ID
            AppSettings.InitializeDeviceId();
            DeviceIdText.Text = AppSettings.DeviceId;

            // Show password indicator
            UpdatePasswordIndicator();

            // Register titlebar drag region
            SetupTitleBar();

            // Check real network status
            await CheckNetworkStatusAsync();

            // Wire up STUN discovery
            _sessionService.PublicEndpointDiscovered += OnPublicEndpointDiscovered;
            _sessionService.StateChanged += OnHostSessionStateChanged;

            // Auto-start host mode
            StartHostWithRetryAsync();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            PulsingStoryboard.Stop();
            _sessionService.PublicEndpointDiscovered -= OnPublicEndpointDiscovered;
            _sessionService.StateChanged -= OnHostSessionStateChanged;
        }

        /// <summary>Set up the custom titlebar drag region so settings button remains clickable.</summary>
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

        /// <summary>Check real network connectivity and update status indicator.</summary>
        private async Task CheckNetworkStatusAsync()
        {
            try
            {
                bool isConnected = NetworkInterface.GetIsNetworkAvailable();

                if (isConnected)
                {
                    // Try to resolve DNS to confirm internet access
                    try
                    {
                        var addresses = await Dns.GetHostAddressesAsync("stun.l.google.com");
                        if (addresses.Length > 0)
                        {
                            NetworkStatusText.Text = "СЕТЬ: ОНЛАЙН";
                            SetNetworkIndicator(true);
                        }
                        else
                        {
                            NetworkStatusText.Text = "СЕТЬ: НЕТ ИНТЕРНЕТА";
                            SetNetworkIndicator(false);
                        }
                    }
                    catch
                    {
                        NetworkStatusText.Text = "СЕТЬ: ЛОКАЛЬНАЯ";
                        SetNetworkIndicator(true);
                    }
                }
                else
                {
                    NetworkStatusText.Text = "СЕТЬ: ОФЛАЙН";
                    SetNetworkIndicator(false);
                }
            }
            catch
            {
                NetworkStatusText.Text = "СЕТЬ: НЕ ОПРЕДЕЛЕНО";
                SetNetworkIndicator(false);
            }
        }

        private void SetNetworkIndicator(bool online)
        {
            var color = online
                ? Windows.UI.Color.FromArgb(255, 16, 185, 129)  // Green
                : Windows.UI.Color.FromArgb(255, 211, 47, 47);   // Red
            var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            NetworkPulseDot.Fill = brush;
            NetworkStatusText.Foreground = brush;
        }

        /// <summary>Update password visibility indicator.</summary>
        private void UpdatePasswordIndicator()
        {
            PasswordInfoPanel.Visibility = !string.IsNullOrEmpty(AppSettings.ConnectionPassword)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>Handle STUN public endpoint discovery.</summary>
        private void OnPublicEndpointDiscovered(IPEndPoint publicEp)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                NetworkStatusText.Text = "СЕТЬ: ОНЛАЙН";
                SetNetworkIndicator(true);
            });
        }

        /// <summary>Toggle host mode on/off.</summary>
        private async void StartHostWithRetryAsync()
        {
            if (_hostActive) return;

            HostStatusText.Text = "ЗАПУСК...";
            HostStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(40, 255, 255, 255));

            bool success = await _sessionService.InitializeHostAsync(AppSettings.Port);

            if (success)
            {
                _hostActive = true;
                HostStatusText.Text = "АКТИВЕН";
                HostStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(40, 16, 185, 129));
            }
            else
            {
                HostStatusText.Text = "ОШИБКА, ПОВТОР...";
                HostStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(40, 211, 47, 47));

                // Auto-restart on failure
                await Task.Delay(5000);
                StartHostWithRetryAsync();
            }
        }

        private async void OnHostSessionStateChanged(SessionState state)
        {
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                switch (state)
                {
                    case SessionState.WaitingForPeer:
                        HostStatusText.Text = "ОЖИДАНИЕ...";
                        HostStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(40, 16, 185, 129));
                        break;
                    case SessionState.Connected:
                        HostStatusText.Text = "ПОДКЛЮЧЁН";
                        HostStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(40, 16, 185, 129));
                        
                        if (_hostActive)
                        {
                            var dialog = new ContentDialog
                            {
                                Title = "ВХОДЯЩЕЕ ПОДКЛЮЧЕНИЕ",
                                Content = "К вашему компьютеру успешно подключился партнер. Теперь он видит ваш экран и может управлять им.",
                                CloseButtonText = "ОК",
                                XamlRoot = this.XamlRoot,
                                RequestedTheme = ElementTheme.Dark
                            };
                            _ = dialog.ShowAsync();
                        }
                        break;
                    case SessionState.Failed:
                    case SessionState.Disconnected:
                        HostStatusText.Text = "ОТКЛЮЧЁН";
                        HostStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Windows.UI.Color.FromArgb(40, 211, 47, 47));
                        
                        // If we are still supposed to be hosting but got disconnected/failed, try to restart
                        _hostActive = false;
                        _sessionService.Disconnect();
                        await Task.Delay(2000);
                        StartHostWithRetryAsync();
                        break;
                }
            });
        }

        /// <summary>Copy device ID to clipboard.</summary>
        private void OnCopyIdClicked(object sender, RoutedEventArgs e)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(AppSettings.DeviceId);
            Clipboard.SetContent(dataPackage);
        }

        /// <summary>Connect to remote host by ID.</summary>
        private async void OnConnectButtonClicked(object sender, RoutedEventArgs e)
        {
            string partnerId = PartnerIdTextBox.Text?.Trim() ?? string.Empty;
            // Remove spaces from ID format "847 291 063" -> "847291063"
            partnerId = partnerId.Replace(" ", "");

            if (string.IsNullOrEmpty(partnerId))
            {
                ShowVisualError("Некорректный ID", "Введите ID удалённого компьютера.");
                return;
            }

            SetInputControlsState(false);

            try
            {
                var clientService = new RemoteSessionService();
                // For now, treat ID as a direct connection identifier
                // In a full implementation, this would go through a signaling server
                // to resolve the ID to an IP:port pair
                bool success = await clientService.ConnectAsync(partnerId);

                if (success)
                {
                    this.Frame.Navigate(typeof(SessionPage), clientService);
                }
                else
                {
                    ShowVisualError("Ошибка подключения",
                        "Не удалось установить P2P-соединение. Убедитесь, что:\n" +
                        "• ID партнера введён корректно\n" +
                        "• На удалённом ПК запущен режим хоста\n" +
                        "• Интернет-соединение активно");
                    clientService.Dispose();
                    SetInputControlsState(true);
                }
            }
            catch (Exception ex)
            {
                ShowVisualError("Сетевая ошибка", $"P2P-подключение прервано: {ex.Message}");
                SetInputControlsState(true);
            }
        }

        /// <summary>Open file manager page.</summary>
        private void OnFileManagerClicked(object sender, RoutedEventArgs e)
        {
            this.Frame.Navigate(typeof(FileManagerPage));
        }

        /// <summary>Open settings dialog.</summary>
        private async void OnSettingsClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog
            {
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                // Settings saved — update UI
                DeviceIdText.Text = AppSettings.DeviceId;
                UpdatePasswordIndicator();
            }
        }

        /// <summary>Show info dialog with all technology details.</summary>
        private async void OnInfoClicked(object sender, RoutedEventArgs e)
        {
            var content = new StackPanel { Spacing = 20, MinWidth = 500 };

            // SECTION: ENCRYPTION
            var encSection = new StackPanel { Spacing = 6 };
            encSection.Children.Add(CreateInfoHeader("\uE72E", "ШИФРОВАНИЕ"));
            encSection.Children.Add(CreateInfoRow("Алгоритм", "AES-256-GCM"));
            encSection.Children.Add(CreateInfoRow("Обмен ключами", "ECDH P-256 (Elliptic Curve Diffie-Hellman)"));
            encSection.Children.Add(CreateInfoRow("Nonce", "12 байт, счётчик + случайный префикс"));
            encSection.Children.Add(CreateInfoRow("Тег аутентификации", "16 байт (GCM Tag)"));
            encSection.Children.Add(CreateInfoRow("Аутентификация", "HMAC-SHA256 challenge-response"));
            content.Children.Add(WrapSection(encSection));

            // SECTION: NAT TRAVERSAL
            var natSection = new StackPanel { Spacing = 6 };
            natSection.Children.Add(CreateInfoHeader("\uE774", "ОБХОД NAT (NAT TRAVERSAL)"));
            natSection.Children.Add(CreateInfoRow("Протокол", "STUN (RFC 5389) + UDP Hole Punching"));
            natSection.Children.Add(CreateInfoRow("STUN-сервер", "stun.l.google.com:19302"));
            natSection.Children.Add(CreateInfoRow("Совместимость", "~80% типов NAT"));
            natSection.Children.Add(CreateInfoRow("Fallback", "TURN-релей (при Symmetric NAT)"));
            natSection.Children.Add(CreateInfoRow("Сигнализация", "WebSocket (JSON-протокол)"));
            content.Children.Add(WrapSection(natSection));

            // SECTION: VIDEO STREAM
            var videoSection = new StackPanel { Spacing = 6 };
            videoSection.Children.Add(CreateInfoHeader("\uE7F4", "ВИДЕОПОТОК"));
            videoSection.Children.Add(CreateInfoRow("Захват экрана", "Windows.Graphics.Capture API"));
            videoSection.Children.Add(CreateInfoRow("Кодирование", "H.264/H.265 аппаратное (GPU)"));
            videoSection.Children.Add(CreateInfoRow("Encoder", "Media Foundation (NVIDIA NVENC / AMD AMF / Intel QSV)"));
            videoSection.Children.Add(CreateInfoRow("Fallback", "JPEG тайловая дельта-компрессия (CPU)"));
            videoSection.Children.Add(CreateInfoRow("Макс. FPS", "До 144 (адаптивный)"));
            videoSection.Children.Add(CreateInfoRow("Битрейт", "Адаптивный, 2–20 Мбит/с по нагрузке"));
            videoSection.Children.Add(CreateInfoRow("Экономия трафика", "60–80% (дельта-кодирование кадров)"));
            videoSection.Children.Add(CreateInfoRow("Адаптивное качество", "Авто по RTT и потерям пакетов"));
            content.Children.Add(WrapSection(videoSection));

            // SECTION: NETWORK
            var netSection = new StackPanel { Spacing = 6 };
            netSection.Children.Add(CreateInfoHeader("\uE968", "СЕТЬ И ПРОТОКОЛ"));
            netSection.Children.Add(CreateInfoRow("Транспорт", "UDP (собственный протокол STORM)"));
            netSection.Children.Add(CreateInfoRow("Порт", $"{AppSettings.Port}"));
            netSection.Children.Add(CreateInfoRow("MTU", "1400 байт"));
            netSection.Children.Add(CreateInfoRow("Keepalive", "Каждые 5 секунд"));
            netSection.Children.Add(CreateInfoRow("Таймаут", "15 секунд"));
            netSection.Children.Add(CreateInfoRow("Фрагментация", "Автоматическая для больших кадров"));
            content.Children.Add(WrapSection(netSection));

            // SECTION: FEATURES
            var featSection = new StackPanel { Spacing = 6 };
            featSection.Children.Add(CreateInfoHeader("\uE74C", "ВОЗМОЖНОСТИ"));
            featSection.Children.Add(CreateInfoRow("Буфер обмена", "Двусторонняя синхронизация текста"));
            featSection.Children.Add(CreateInfoRow("Файловый менеджер", "Двухоконный, drag & drop, горячие клавиши"));
            featSection.Children.Add(CreateInfoRow("Чат", "Текстовый чат во время сессии"));
            featSection.Children.Add(CreateInfoRow("Wake-on-LAN", "Удалённое включение ПК"));
            featSection.Children.Add(CreateInfoRow("Горячие клавиши", "Ctrl+Alt+Del, Alt+Tab, Win"));
            featSection.Children.Add(CreateInfoRow("Автозапуск", "Запуск при старте Windows"));
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
                Title = "ИНФОРМАЦИЯ О ТЕХНОЛОГИЯХ",
                Content = scrollViewer,
                CloseButtonText = "ЗАКРЫТЬ",
                XamlRoot = this.XamlRoot,
                RequestedTheme = ElementTheme.Dark
            };
            await dialog.ShowAsync();
        }

        private static Border WrapSection(StackPanel section)
        {
            return new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(20, 255, 255, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
                Child = section
            };
        }

        private static StackPanel CreateInfoHeader(string glyph, string title)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                Glyph = glyph, FontSize = 16,
                Foreground = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
            });
            sp.Children.Add(new TextBlock
            {
                Text = title, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 13,
                Foreground = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
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
                Text = label, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 11,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(140, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lbl, 0);

            var val = new TextBlock
            {
                Text = value, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Century Gothic"),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 11,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(230, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(val, 1);

            grid.Children.Add(lbl);
            grid.Children.Add(val);
            return grid;
        }

        /// <summary>Generate a new random device ID.</summary>
        private async void OnChangeIdClicked(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "СМЕНИТЬ ID",
                Content = "Вы уверены? Текущий ID будет заменён на новый случайный. Все, кто знает ваш текущий ID, не смогут подключиться.",
                PrimaryButtonText = "СМЕНИТЬ",
                CloseButtonText = "ОТМЕНА",
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
                Title = title.ToUpper(),
                Content = content,
                CloseButtonText = "ОК",
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
