// Copyright (c) STORM REMOTE CONTROL Contributors. All rights reserved.
// Licensed under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using StormRemoteControl.Models;
using StormRemoteControl.Services;
using StormRemoteControl.Views;

namespace StormRemoteControl
{
    /// <summary>
    /// Represents the main entry point of STORM REMOTE CONTROL.
    /// Implements strict Single Instance Mode (Mutex & Window Activation),
    /// initializes Localization and Theme managers.
    /// </summary>
    public sealed partial class App : Application
    {
        private Window? _mainWindow;
        private static Mutex? _mutex;
        private const string MutexName = @"Global\STORM_REMOTE_CONTROL_SingleInstanceMutex";

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        /// <summary>Gets the main application window, accessible from any page.</summary>
        public static Window? MainWindow { get; private set; }

        public App()
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                // App is already running: Activate existing window and exit duplicate instance
                try
                {
                    IntPtr hwnd = FindWindow(null, "STORM REMOTE CONTROL");
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }
                }
                catch { }

                Environment.Exit(0);
                return;
            }

            AppSettings.Load();
            LocalizationService.CurrentLanguage = AppSettings.ActiveLanguage;

            this.InitializeComponent();
            this.UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash_log.txt"),
                    $"\n=== [{LocalizationService.FormatDateTime(DateTime.Now)}] APP UNHANDLED ===\n{e.Exception}\n");
            }
            catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            ThemeManager.Initialize();
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _mainWindow.Activate();
        }
    }
}
