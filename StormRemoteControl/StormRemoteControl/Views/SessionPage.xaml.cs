using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using StormRemoteControl.Models;
using StormRemoteControl.Services;
using StormRemoteControl.Core;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using QualityProfile = StormRemoteControl.Core.QualityProfile;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Remote session viewport — displays real decoded video frames from the P2P stream
    /// and captures local input for transmission to the host.
    /// </summary>
    public sealed partial class SessionPage : Page
    {
        private RemoteSessionService? _sessionService;
        private bool _isAudioMuted = false;
        private bool _isChatOpen = false;
        private Guid _activeFileTransferId = Guid.Empty;

        public SessionPage()
        {
            this.InitializeComponent();

            RemoteDisplaySurface.PointerMoved += OnSurfacePointerMoved;
            RemoteDisplaySurface.PointerPressed += OnSurfacePointerPressed;
            RemoteDisplaySurface.PointerReleased += OnSurfacePointerReleased;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is RemoteSessionService service)
            {
                _sessionService = service;

                // Wire up real events
                _sessionService.MetricsUpdated += OnSessionMetricsUpdated;
                _sessionService.StateChanged += OnSessionStateChanged;
                _sessionService.ChatMessageReceived += OnChatMessageReceived;
                _sessionService.VideoFrameReceived += OnVideoFrameReceived;

                // File transfer events
                _sessionService.FileOfferReceived += OnFileOfferReceived;
                _sessionService.FileTransferProgress += OnFileTransferProgress;
                _sessionService.FileTransferCompleted += OnFileTransferCompleted;
            }
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            if (_sessionService != null)
            {
                _sessionService.MetricsUpdated -= OnSessionMetricsUpdated;
                _sessionService.StateChanged -= OnSessionStateChanged;
                _sessionService.ChatMessageReceived -= OnChatMessageReceived;
                _sessionService.VideoFrameReceived -= OnVideoFrameReceived;
                _sessionService.FileOfferReceived -= OnFileOfferReceived;
                _sessionService.FileTransferProgress -= OnFileTransferProgress;
                _sessionService.FileTransferCompleted -= OnFileTransferCompleted;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  VIDEO FRAME DISPLAY — Decode JPEG and render
        // ═══════════════════════════════════════════════════════════

        private void OnVideoFrameReceived(byte[] jpegData, int width, int height)
        {
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    // Decode JPEG bytes into a BitmapImage for display
                    using var stream = new InMemoryRandomAccessStream();
                    using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                    writer.WriteBytes(jpegData);
                    await writer.StoreAsync();

                    var bitmapImage = new BitmapImage();
                    stream.Seek(0);
                    await bitmapImage.SetSourceAsync(stream);

                    RemoteDisplaySurface.Source = bitmapImage;
                }
                catch (Exception)
                {
                    // Skip corrupt frames silently
                }
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  METRICS DISPLAY
        // ═══════════════════════════════════════════════════════════

        private void OnSessionMetricsUpdated(double latency, double bitrate, int fps)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                FpsTextBlock.Text = $"{fps} FPS";
                LatencyTextBlock.Text = $"{latency:F1} MS";
                BitrateTextBlock.Text = $"{bitrate:F1} MBPS";
            });
        }

        private void OnSessionStateChanged(SessionState state)
        {
            if (state == SessionState.Disconnected)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    this.Frame.Navigate(typeof(MainPage));
                });
            }
        }

        private void OnChatMessageReceived(string message)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                AppendChatBubble("ПАРТНЕР", message, false);
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  INPUT CAPTURE — Mouse & Keyboard
        // ═══════════════════════════════════════════════════════════

        private async void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_sessionService == null) return;
            var pointerPoint = e.GetCurrentPoint(RemoteDisplaySurface);
            var width = RemoteDisplaySurface.ActualWidth;
            var height = RemoteDisplaySurface.ActualHeight;

            if (width > 0 && height > 0)
            {
                int normX = (int)((pointerPoint.Position.X / width) * 65535);
                int normY = (int)((pointerPoint.Position.Y / height) * 65535);
                await _sessionService.SendInputAsync(0x01, normX, normY, 0u);
            }
        }

        private async void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_sessionService == null) return;
            var pointerPoint = e.GetCurrentPoint(RemoteDisplaySurface);
            uint clickData = (uint)(pointerPoint.Properties.IsLeftButtonPressed ? 0x01 : 0x02);
            await _sessionService.SendInputAsync(0x02, 0, 0, clickData);
        }

        private async void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_sessionService == null) return;
            await _sessionService.SendInputAsync(0x03, 0, 0, 0u);
        }

        // ═══════════════════════════════════════════════════════════
        //  TOOLBAR ACTIONS
        // ═══════════════════════════════════════════════════════════

        private void OnSpeedModeSelected(object sender, RoutedEventArgs e)
        {
            if (_sessionService != null)
            {
                _sessionService.ActiveProfile = QualityProfile.Speed;
                QualityButton.Content = "КАЧЕСТВО: СКОРОСТЬ";
            }
        }

        private void OnQualityModeSelected(object sender, RoutedEventArgs e)
        {
            if (_sessionService != null)
            {
                _sessionService.ActiveProfile = QualityProfile.Quality;
                QualityButton.Content = "КАЧЕСТВО: ВЫСОКОЕ";
            }
        }

        private void OnAutoModeSelected(object sender, RoutedEventArgs e)
        {
            if (_sessionService != null)
            {
                _sessionService.ActiveProfile = QualityProfile.Auto;
                QualityButton.Content = "КАЧЕСТВО: АВТО";
            }
        }

        private async void OnCtrlAltDelClick(object sender, RoutedEventArgs e)
        {
            if (_sessionService != null)
                await _sessionService.SendInputAsync(0x04, 0, 0, 0x0015u);
        }

        private async void OnAltTabClick(object sender, RoutedEventArgs e)
        {
            if (_sessionService != null)
                await _sessionService.SendInputAsync(0x04, 0, 0, 0x0016u);
        }

        private async void OnWinKeyClick(object sender, RoutedEventArgs e)
        {
            if (_sessionService != null)
                await _sessionService.SendInputAsync(0x04, 0, 0, 0x0017u);
        }

        private void OnAudioToggleClick(object sender, RoutedEventArgs e)
        {
            _isAudioMuted = !_isAudioMuted;
            AudioButton.Content = _isAudioMuted ? "ЗВУК: ВЫКЛ" : "ЗВУК: ВКЛ";
        }

        private void OnDisconnectClick(object sender, RoutedEventArgs e)
        {
            _sessionService?.Disconnect();
            this.Frame.Navigate(typeof(MainPage));
        }

        // ═══════════════════════════════════════════════════════════
        //  FILE TRANSFER
        // ═══════════════════════════════════════════════════════════

        private void OnFileTransferClick(object sender, RoutedEventArgs e) { }

        private async void OnSendFileClick(object sender, RoutedEventArgs e)
        {
            if (_sessionService == null) return;

            try
            {
                var filePicker = new FileOpenPicker();
                filePicker.FileTypeFilter.Add("*");
                filePicker.SuggestedStartLocation = PickerLocationId.Desktop;

                // WinUI 3 unpackaged: initialize picker with app window handle
                var appWindow = App.MainWindow;
                if (appWindow != null)
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(appWindow);
                    WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hWnd);
                }

                var file = await filePicker.PickSingleFileAsync();
                if (file != null)
                {
                    _activeFileTransferId = await _sessionService.SendFileAsync(file.Path);
                    FileTransferName.Text = file.Name;
                    FileTransferPercent.Text = "0%";
                    FileTransferProgress.Value = 0;
                    FileTransferOverlay.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[STORM] File picker error: {ex.Message}");
            }
        }

        private async void OnFileOfferReceived(Guid transferId, string fileName, long fileSize)
        {
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                string sizeStr = fileSize < 1024 * 1024
                    ? $"{fileSize / 1024.0:F1} KB"
                    : $"{fileSize / (1024.0 * 1024.0):F1} MB";

                var dialog = new ContentDialog
                {
                    Title = "ВХОДЯЩИЙ ФАЙЛ",
                    Content = $"Партнер отправляет файл:\n\n{fileName}\nРазмер: {sizeStr}\n\nПринять?",
                    PrimaryButtonText = "ПРИНЯТЬ",
                    CloseButtonText = "ОТКЛОНИТЬ",
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    string savePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);
                    _sessionService?.AcceptFileOffer(transferId, savePath);
                    _activeFileTransferId = transferId;
                    FileTransferName.Text = fileName;
                    FileTransferOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    _sessionService?.RejectFileOffer(transferId);
                }
            });
        }

        private void OnFileTransferProgress(Guid transferId, double progress)
        {
            if (transferId != _activeFileTransferId) return;
            this.DispatcherQueue.TryEnqueue(() =>
            {
                int pct = (int)(progress * 100);
                FileTransferPercent.Text = $"{pct}%";
                FileTransferProgress.Value = pct;
            });
        }

        private void OnFileTransferCompleted(Guid transferId, bool success)
        {
            if (transferId != _activeFileTransferId) return;
            this.DispatcherQueue.TryEnqueue(() =>
            {
                FileTransferPercent.Text = success ? "ГОТОВО" : "ОШИБКА";
                FileTransferProgress.Value = success ? 100 : 0;

                // Hide overlay after 3 seconds
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(3);
                timer.Tick += (s, e) =>
                {
                    FileTransferOverlay.Visibility = Visibility.Collapsed;
                    timer.Stop();
                };
                timer.Start();
            });
        }

        // ═══════════════════════════════════════════════════════════
        //  CHAT
        // ═══════════════════════════════════════════════════════════

        private void OnChatToggleClick(object sender, RoutedEventArgs e)
        {
            _isChatOpen = !_isChatOpen;
            ChatColumn.Width = _isChatOpen ? new GridLength(320) : new GridLength(0);
        }

        private void OnSendMessageClick(object sender, RoutedEventArgs e) => TransmitChatMessage();

        private void OnMessageInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                TransmitChatMessage();
        }

        private async void TransmitChatMessage()
        {
            string rawMsg = MessageInputBox.Text;
            if (string.IsNullOrEmpty(rawMsg) || _sessionService == null) return;

            await _sessionService.SendChatMessageAsync(rawMsg);
            AppendChatBubble("ВЫ", rawMsg, true);
            MessageInputBox.Text = "";
        }

        private void AppendChatBubble(string author, string text, bool isSelf)
        {
            var card = new Border
            {
                Background = isSelf
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(26, 211, 47, 47))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(26, 25, 25, 32)),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = isSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                MaxWidth = 240
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = author.ToUpper(),
                Style = (Style)Application.Current.Resources["StormTextSubtleBoldStyle"],
                Foreground = isSelf
                    ? (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["AccentColorBrush"]
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 185, 129))
            });
            stack.Children.Add(new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["StormTextBoldStyle"],
                TextWrapping = TextWrapping.Wrap
            });

            card.Child = stack;
            ChatMessagesStack.Children.Add(card);
        }
    }
}
