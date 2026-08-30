using System;
using Microsoft.UI.Xaml;
using StormRemoteControl.Views;

namespace StormRemoteControl
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// Acts as the core launch vector for the Remote Control executable shell.
    /// </summary>
    public partial class App : Application
    {
        private Window _mainWindow;

        /// <summary>
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // Set up and display our custom Mica Window shell
            _mainWindow = new MainWindow();
            _mainWindow.Activate();
        }
    }
}
