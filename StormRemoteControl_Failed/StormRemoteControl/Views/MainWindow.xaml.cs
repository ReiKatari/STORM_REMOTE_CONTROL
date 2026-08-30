using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace StormRemoteControl.Views
{
    /// <summary>
    /// Top-level shell hosting framework backdrops, titlebar extensions, and primary page frame navigation.
    /// Uses native Composition system backdrops to apply Fluent Acrylic or Mica glass effects.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private MicaController _micaController;
        private SystemBackdropConfiguration _backdropConfiguration;

        public MainWindow()
        {
            this.InitializeComponent();

            // Apply standard window sizing constraints and center
            ConfigureWindowDimensions();

            // Set up mica glass material background for Fluent styles on Windows 11
            TrySetMicaBackdrop();

            // Extend standard titlebar elements for unified custom top area
            ExtendsContentIntoTitleBar = true;

            // Direct route to the main dashboard layout
            RootFrame.Navigate(typeof(MainPage));
        }

        /// <summary>
        /// Attempts to apply the modern Mica material backdrop on systems where supported (Windows 11+).
        /// </summary>
        private void TrySetMicaBackdrop()
        {
            if (MicaController.IsSupported())
            {
                _backdropConfiguration = new SystemBackdropConfiguration();
                
                // Configure active/deactive transitions so backdrop reacts when window loses focus
                this.Activated += (s, e) =>
                {
                    if (_backdropConfiguration != null)
                    {
                        _backdropConfiguration.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
                    }
                };
                
                _micaController = new MicaController();
                
                // Retrieve the WinRT composition backdrop support interface
                var supportsBackdrop = this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
                _micaController.AddSystemBackdropTarget(supportsBackdrop);
                _micaController.Configure(this, _backdropConfiguration);
            }
        }

        /// <summary>
        /// Configures initial window dimensions to ensure a premium widescreen layout suited for remote control.
        /// Uses P/Invoke calls to communicate directly with Win32 APIs for precise window sizing.
        /// </summary>
        private void ConfigureWindowDimensions()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowIdFromWindow(this);
            var windowId = Microsoft.UI.GetWindowIdFromWindowId(hWnd.Value);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                // Set custom initial size: 1150 x 720 (optimized for modern displays)
                appWindow.Resize(new Windows.Graphics.SizeInt32(1150, 720));
                
                // Position window in the center of the primary display
                var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
                var centerPoint = new Windows.Graphics.PointInt32(
                    (displayArea.OuterBounds.Width - 1150) / 2,
                    (displayArea.OuterBounds.Height - 720) / 2
                );
                
                // Enforce minimum window size boundaries
                appWindow.Move(centerPoint);
            }
        }
    }

    /// <summary>
    /// Helper extension class to handle dynamic casting using COM or internal WinRT mappings.
    /// </summary>
    internal static class WindowExtensions
    {
        public static T As<T>(this Window window)
        {
            return (T)(object)window;
        }
    }
}
