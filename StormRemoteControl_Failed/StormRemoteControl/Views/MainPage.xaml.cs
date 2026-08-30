using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StormRemoteControl.Core;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Serves as the primary landing page and dashboard controller for the STORM REMOTE CONTROL app.
    /// Manages user credentials, network signaling requests, and recent history cards.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private RemoteSessionService _sessionService;

        public MainPage()
        {
            this.InitializeComponent();

            // Set up page life cycle handlers
            this.Loaded += OnPageLoaded;
            this.Unloaded += OnPageUnloaded;

            // Instantiate P2P session manager
            _sessionService = new RemoteSessionService();
        }

        /// <summary>
        /// Fires when the layout finishes loading. Immediately begins the pulsing network animation.
        /// </summary>
        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            PulsingStoryboard.Begin();
        }

        /// <summary>
        /// Releases background thread listeners when leaving the dashboard.
        /// </summary>
        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            PulsingStoryboard.Stop();
            _sessionService?.Dispose();
        }

        /// <summary>
        /// Asynchronously initializes connection to remote host after validating the ID text string.
        /// </summary>
        private async void OnConnectButtonClicked(object sender, RoutedEventArgs e)
        {
            string partnerId = PartnerIdTextBox.Text?.Replace(" ", "").Trim();
            string accessPassword = AccessTokenBox.Password;

            if (string.IsNullOrEmpty(partnerId) || partnerId.Length < 6)
            {
                ShowVisualError("Некорректный ID", "Пожалуйста, введите корректный числовой ID партнера (минимум 6 цифр).");
                return;
            }

            // Lock controls to prevent double clicks during connection phase
            SetInputControlsState(false);
            ConnectButton.Content = "ПОДКЛЮЧЕНИЕ...";

            try
            {
                // Dispatch connection task to P2P worker thread pools
                bool success = await _sessionService.ConnectAsync(partnerId, accessPassword);

                if (success)
                {
                    // Clean up fields and show success dialog
                    ShowVisualError("Соединение установлено", "P2P Сессия успешно создана с удаленным хостом! (Демонстрационный режим)");
                    SetInputControlsState(true);
                    ConnectButton.Content = "ПОДКЛЮЧИТЬСЯ";
                }
                else
                {
                    ShowVisualError("Ошибка авторизации", "Не удалось установить соединение. Проверьте правильность ID или пароля доступа.");
                    SetInputControlsState(true);
                    ConnectButton.Content = "ПОДКЛЮЧИТЬСЯ";
                }
            }
            catch (Exception ex)
            {
                ShowVisualError("Сетевая ошибка", $"Произошел сбой при P2P-подключении: {ex.Message}");
                SetInputControlsState(true);
                ConnectButton.Content = "ПОДКЛЮЧИТЬСЯ";
            }
        }

        /// <summary>
        /// Routes tile selection clicks from recent history cards, populating input fields automatically.
        /// </summary>
        private void OnRecentItemClicked(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is GridViewItem item)
            {
                // Locate border inside grid item
                if (item.Content is Border border && border.Child is Grid grid)
                {
                    // Find StackPanel
                    foreach (var child in grid.Children)
                    {
                        if (child is StackPanel stack)
                        {
                            // Extract details from children
                            foreach (var element in stack.Children)
                            {
                                if (element is TextBlock textBlock && textBlock.Text.StartsWith("ID:"))
                                {
                                    string fullId = textBlock.Text.Replace("ID:", "").Trim();
                                    PartnerIdTextBox.Text = fullId;
                                    AccessTokenBox.Password = "1234"; // Pre-filled credentials simulation
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Displays error dialogs matching our Century Gothic Typography rules.
        /// </summary>
        private async void ShowVisualError(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title.ToUpper(),
                Content = content,
                CloseButtonText = "ОК",
                XamlRoot = this.XamlRoot
            };

            // Force Century Gothic Bold on dialog texts programmatically
            dialog.Resources["ContentDialogTitleFontFamily"] = new Microsoft.UI.Xaml.Media.FontFamily("Century Gothic");
            dialog.Resources["ContentDialogTitleFontWeight"] = Microsoft.UI.Text.FontWeights.Bold;

            await dialog.ShowAsync();
        }

        /// <summary>
        /// Toggles interaction state of dashboard inputs.
        /// </summary>
        private void SetInputControlsState(bool isEnabled)
        {
            PartnerIdTextBox.IsEnabled = isEnabled;
            AccessTokenBox.IsEnabled = isEnabled;
            ConnectButton.IsEnabled = isEnabled;
        }
    }
}
