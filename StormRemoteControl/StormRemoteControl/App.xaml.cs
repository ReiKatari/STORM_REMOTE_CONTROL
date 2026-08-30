using System;
using Microsoft.UI.Xaml;
using StormRemoteControl.Views;

namespace StormRemoteControl
{
    /// <summary>
    /// Represents the main Entry Point class of the STORM REMOTE CONTROL application.
    /// Triggers shell window launch on startup.
    /// </summary>
    public sealed partial class App : Application
    {
        private Window? _mainWindow;
        private static System.Threading.Mutex? _mutex;

        /// <summary>Gets the main application window, accessible from any page.</summary>
        public static Window? MainWindow { get; private set; }

        public App()
        {
            _mutex = new System.Threading.Mutex(true, "STORM_REMOTE_CONTROL_MUTEX", out bool createdNew);
            if (!createdNew)
            {
                // App is already running. Exit this instance.
                Environment.Exit(0);
                return;
            }

            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt"), "\n=== APP UNHANDLED ===\n" + e.Exception.ToString());
            }
            catch {}
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _mainWindow.Activate();
        }
    }
}
