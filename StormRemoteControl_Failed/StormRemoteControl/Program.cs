using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace StormRemoteControl
{
    /// <summary>
    /// Custom entry point for the Storm Remote Control application to initialize 
    /// a DispatcherQueue on the main thread before launching the WinUI 3 environment.
    /// </summary>
    public static class Program
    {
        [DllImport("CoreMessaging.dll", EntryPoint = "CreateDispatcherQueueController")]
        private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, ref IntPtr queueController);

        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            public int dwSize;
            public int threadType;
            public int apartmentType;
        }

        private static IntPtr _queueController = IntPtr.Zero;

        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                System.IO.File.WriteAllText("E:\\STORM REMOTE CONTROL\\test.txt", "Main started\n");
                
                try
                {
                    // Initialize Windows App SDK Bootstrapper (Version 2.1 -> 0x00020001)
                    Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(0x00020001);
                    System.IO.File.AppendAllText("E:\\STORM REMOTE CONTROL\\test.txt", "Windows App SDK Bootstrapper initialized successfully\n");
                }
                catch (Exception bootEx)
                {
                    System.IO.File.AppendAllText("E:\\STORM REMOTE CONTROL\\test.txt", $"Windows App SDK Bootstrapper failed to initialize: {bootEx}\n");
                }

                global::WinRT.ComWrappersSupport.InitializeComWrappers();

                // Initialize a WinRT DispatcherQueue on the current main thread.
                // This is required for unpackaged WinUI 3 applications to function properly
                // without crashing in CoreMessagingXP.dll due to a missing dispatcher context.
                DispatcherQueueOptions options = new DispatcherQueueOptions
                {
                    dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions)),
                    threadType = 2,      // DQTYPE_THREAD_CURRENT (Current Thread)
                    apartmentType = 0     // DQT_COMPAT_APA_NONE (NONE)
                };
                int hr = CreateDispatcherQueueController(options, ref _queueController);
                System.IO.File.AppendAllText("E:\\STORM REMOTE CONTROL\\test.txt", $"CreateDispatcherQueueController returned HRESULT: 0x{hr:X8}\n");

                Application.Start((p) => {
                    try
                    {
                        var winUIQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                        var winRTQueue = Windows.System.DispatcherQueue.GetForCurrentThread();
                        System.IO.File.AppendAllText("E:\\STORM REMOTE CONTROL\\test.txt", 
                            $"Application.Start callback invoked. WinUI Queue Null: {winUIQueue == null}, WinRT Queue Null: {winRTQueue == null}\n");
                        
                        var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(winUIQueue);
                        SynchronizationContext.SetSynchronizationContext(context);
                        new App();
                    }
                    catch (Exception callbackEx)
                    {
                        System.IO.File.WriteAllText("E:\\STORM REMOTE CONTROL\\crash_log.txt", $"Exception in Application.Start callback:\n{callbackEx}\n");
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("E:\\STORM REMOTE CONTROL\\crash_log.txt", $"Unhandled exception in Main:\n{ex}\n");
            }
        }
    }
}
