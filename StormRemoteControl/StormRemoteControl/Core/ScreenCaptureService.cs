// ==========================================================================
// ScreenCaptureService.cs — Windows.Graphics.Capture screen capture engine
// ==========================================================================
// Uses Direct3D11 device creation via P/Invoke and the Windows Graphics
// Capture API to capture the primary monitor (host mode) or a user-selected
// capture item (picker mode). Falls back to a synthetic gradient bitmap
// generator when D3D interop cannot be initialised.
// ==========================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Foundation;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Captures screen frames from the primary monitor or a user-selected capture
    /// item using the Windows.Graphics.Capture pipeline. Emits raw BGRA8 pixel
    /// data through the <see cref="FrameCaptured"/> event at the configured FPS.
    /// </summary>
    public sealed class ScreenCaptureService : IDisposable
    {
        // -----------------------------------------------------------------
        // COM interface for extracting raw bytes from IMemoryBuffer
        // -----------------------------------------------------------------

        /// <summary>
        /// COM interface used to obtain a raw byte pointer from an
        /// <see cref="Windows.Foundation.IMemoryBuffer"/>.
        /// </summary>
        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            /// <summary>Retrieves a pointer to the underlying byte buffer.</summary>
            void GetBuffer(out byte* buffer, out uint capacity);
        }

        // -----------------------------------------------------------------
        // COM interop — IGraphicsCaptureItemInterop
        // -----------------------------------------------------------------

        /// <summary>
        /// Interop interface that can create a <see cref="GraphicsCaptureItem"/>
        /// from an HWND or HMONITOR without the system picker UI.
        /// </summary>
        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow(
                [In] IntPtr window,
                [In] ref Guid iid);

            IntPtr CreateForMonitor(
                [In] IntPtr monitor,
                [In] ref Guid iid);
        }

        /// <summary>
        /// IInitializeWithWindow — used to set the parent HWND on the
        /// <see cref="GraphicsCapturePicker"/> for unpackaged WinUI 3 apps.
        /// </summary>
        [ComImport]
        [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInitializeWithWindow
        {
            void Initialize(IntPtr hwnd);
        }

        // -----------------------------------------------------------------
        // P/Invoke — Direct3D 11
        // -----------------------------------------------------------------

        /// <summary>Creates a Direct3D 11 device and immediate context.</summary>
        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            int driverType,
            IntPtr software,
            uint flags,
            IntPtr pFeatureLevels,
            uint featureLevels,
            uint sdkVersion,
            out IntPtr ppDevice,
            out int pFeatureLevel,
            out IntPtr ppImmediateContext);

        /// <summary>
        /// Creates a WinRT <see cref="IDirect3DDevice"/> from a native DXGI device.
        /// </summary>
        [DllImport("d3d11.dll",
            EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
            SetLastError = true,
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            CallingConvention = CallingConvention.StdCall)]
        private static extern uint CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice,
            out IntPtr graphicsDevice);

        // -----------------------------------------------------------------
        // P/Invoke — Win32 monitor enumeration and COM activation
        // -----------------------------------------------------------------

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetDesktopWindow();

        private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        /// <summary>
        /// Activates a WinRT class factory for the given activation ID.
        /// Used to obtain <see cref="IGraphicsCaptureItemInterop"/>.
        /// </summary>
        [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory",
            CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId,
            ref Guid iid,
            out IntPtr factory);

        /// <summary>Creates an HSTRING from a managed string.</summary>
        [DllImport("combase.dll", EntryPoint = "WindowsCreateString",
            CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        /// <summary>Deletes an HSTRING.</summary>
        [DllImport("combase.dll", EntryPoint = "WindowsDeleteString",
            ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        // -----------------------------------------------------------------
        // D3D / COM constants
        // -----------------------------------------------------------------

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        private const uint D3D11_SDK_VERSION = 7;

        /// <summary>IDXGIDevice interface GUID.</summary>
        private static readonly Guid IidDxgiDevice =
            new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        /// <summary>IGraphicsCaptureItemInterop interface GUID.</summary>
        private static readonly Guid IidGraphicsCaptureItemInterop =
            new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

        /// <summary>IGraphicsCaptureItem interface GUID.</summary>
        private static readonly Guid IidGraphicsCaptureItem =
            new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        /// <summary>WinRT activation class ID for GraphicsCaptureItem.</summary>
        private const string GraphicsCaptureItemClassName =
            "Windows.Graphics.Capture.GraphicsCaptureItem";

        // -----------------------------------------------------------------
        // Instance state
        // -----------------------------------------------------------------

        private IDirect3DDevice? _device;
        private GraphicsCaptureItem? _captureItem;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private readonly Stopwatch _frameStopwatch = new();
        private bool _disposed;

        /// <summary>
        /// Fires when a new frame has been captured.
        /// Parameters: BGRA8 pixel bytes, width, height.
        /// </summary>
        public event Action<byte[], int, int>? FrameCaptured;

        /// <summary>Gets a value indicating whether capture is currently active.</summary>
        public bool IsCapturing { get; private set; }

        /// <summary>Gets the width of the most recently captured frame.</summary>
        public int CapturedWidth { get; private set; }

        /// <summary>Gets the height of the most recently captured frame.</summary>
        public int CapturedHeight { get; private set; }

        private int _targetFps = 30;

        /// <summary>
        /// Gets or sets the target frame rate. Clamped to <c>[1, 60]</c>.
        /// </summary>
        public int TargetFps
        {
            get => _targetFps;
            set => _targetFps = Math.Clamp(value, 1, 60);
        }

        // =================================================================
        // D3D device creation
        // =================================================================

        /// <summary>
        /// Attempts to create a WinRT <see cref="IDirect3DDevice"/> by
        /// P/Invoking into <c>d3d11.dll</c>. Returns <see langword="true"/>
        /// on success.
        /// </summary>
        private bool TryCreateDirect3DDevice()
        {
            if (_device is not null) return true;

            IntPtr nativeDevice = IntPtr.Zero;
            IntPtr immediateContext = IntPtr.Zero;
            IntPtr dxgiDevicePtr = IntPtr.Zero;
            IntPtr winrtDevicePtr = IntPtr.Zero;

            try
            {
                int hr = D3D11CreateDevice(
                    IntPtr.Zero,
                    D3D_DRIVER_TYPE_HARDWARE,
                    IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero,
                    0,
                    D3D11_SDK_VERSION,
                    out nativeDevice,
                    out _,
                    out immediateContext);

                if (hr < 0 || nativeDevice == IntPtr.Zero)
                {
                    Debug.WriteLine($"[STORM] D3D11CreateDevice failed: 0x{hr:X8}");
                    return false;
                }

                // QueryInterface for IDXGIDevice
                Guid dxgiGuid = IidDxgiDevice;
                hr = Marshal.QueryInterface(nativeDevice, in dxgiGuid, out dxgiDevicePtr);
                if (hr < 0 || dxgiDevicePtr == IntPtr.Zero)
                {
                    Debug.WriteLine($"[STORM] QueryInterface IDXGIDevice failed: 0x{hr:X8}");
                    return false;
                }

                // Wrap the DXGI device in a WinRT IDirect3DDevice
                uint result = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out winrtDevicePtr);
                if (result != 0 || winrtDevicePtr == IntPtr.Zero)
                {
                    Debug.WriteLine($"[STORM] CreateDirect3D11DeviceFromDXGIDevice failed: 0x{result:X8}");
                    return false;
                }

                _device = Marshal.GetObjectForIUnknown(winrtDevicePtr) as IDirect3DDevice;
                return _device is not null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] D3D device creation exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (winrtDevicePtr != IntPtr.Zero) Marshal.Release(winrtDevicePtr);
                if (dxgiDevicePtr != IntPtr.Zero) Marshal.Release(dxgiDevicePtr);
                if (immediateContext != IntPtr.Zero) Marshal.Release(immediateContext);
                if (nativeDevice != IntPtr.Zero) Marshal.Release(nativeDevice);
            }
        }

        // =================================================================
        // StartCapture — host mode (primary monitor, no picker)
        // =================================================================

        /// <summary>
        /// Begins capturing the primary monitor without showing the system picker.
        /// Uses <c>IGraphicsCaptureItemInterop::CreateForMonitor</c> via COM to
        /// create a <see cref="GraphicsCaptureItem"/> directly from the monitor handle.
        /// Falls back to a synthetic gradient frame generator if the capture
        /// pipeline cannot be initialised.
        /// </summary>
        public void StartCapture()
        {
            if (IsCapturing) return;

            if (!TryCreateDirect3DDevice())
            {
                Debug.WriteLine("[STORM] D3D device unavailable — starting fallback gradient capture.");
                StartFallbackCapture();
                return;
            }

            try
            {
                // Obtain the primary monitor handle
                IntPtr hMonitor = MonitorFromWindow(GetDesktopWindow(), MONITOR_DEFAULTTOPRIMARY);
                if (hMonitor == IntPtr.Zero)
                {
                    Debug.WriteLine("[STORM] MonitorFromWindow returned null — fallback.");
                    StartFallbackCapture();
                    return;
                }

                _captureItem = TryCreateCaptureItemForMonitor(hMonitor);
                if (_captureItem is null)
                {
                    Debug.WriteLine("[STORM] Failed to create GraphicsCaptureItem for monitor — fallback.");
                    StartFallbackCapture();
                    return;
                }

                BeginCaptureSession();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] StartCapture exception: {ex.Message}");
                StartFallbackCapture();
            }
        }

        /// <summary>
        /// Attempts to create a <see cref="GraphicsCaptureItem"/> from a monitor
        /// handle using the <c>IGraphicsCaptureItemInterop</c> COM interface,
        /// obtained via <c>RoGetActivationFactory</c>.
        /// </summary>
        private static GraphicsCaptureItem? TryCreateCaptureItemForMonitor(IntPtr hMonitor)
        {
            IntPtr hstring = IntPtr.Zero;

            try
            {
                // Create HSTRING for the WinRT class name
                int hr = WindowsCreateString(
                    GraphicsCaptureItemClassName,
                    GraphicsCaptureItemClassName.Length,
                    out hstring);

                if (hr < 0 || hstring == IntPtr.Zero)
                {
                    Debug.WriteLine($"[STORM] WindowsCreateString failed: 0x{hr:X8}");
                    return null;
                }

                // Get the IGraphicsCaptureItemInterop factory
                Guid interopIid = IidGraphicsCaptureItemInterop;
                hr = RoGetActivationFactory(hstring, ref interopIid, out IntPtr factoryPtr);
                if (hr < 0 || factoryPtr == IntPtr.Zero)
                {
                    Debug.WriteLine($"[STORM] RoGetActivationFactory failed: 0x{hr:X8}");
                    return null;
                }

                try
                {
                    var factory = (IGraphicsCaptureItemInterop)
                        Marshal.GetObjectForIUnknown(factoryPtr);

                    Guid itemIid = IidGraphicsCaptureItem;
                    IntPtr itemPtr = factory.CreateForMonitor(hMonitor, ref itemIid);
                    if (itemPtr == IntPtr.Zero) return null;

                    try
                    {
                        return Marshal.GetObjectForIUnknown(itemPtr) as GraphicsCaptureItem;
                    }
                    finally
                    {
                        Marshal.Release(itemPtr);
                    }
                }
                finally
                {
                    Marshal.Release(factoryPtr);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] Interop CreateForMonitor failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (hstring != IntPtr.Zero)
                {
                    WindowsDeleteString(hstring);
                }
            }
        }

        // =================================================================
        // StartCaptureWithPickerAsync — picker mode
        // =================================================================

        /// <summary>
        /// Shows the system <see cref="GraphicsCapturePicker"/> and begins
        /// capturing the item chosen by the user.
        /// </summary>
        /// <param name="hwnd">
        /// Window handle required to initialise the picker for un-packaged
        /// WinUI 3 applications.
        /// </param>
        public async Task StartCaptureWithPickerAsync(IntPtr hwnd)
        {
            if (IsCapturing) return;

            if (!TryCreateDirect3DDevice())
            {
                Debug.WriteLine("[STORM] D3D device unavailable — starting fallback gradient capture.");
                StartFallbackCapture();
                return;
            }

            try
            {
                var picker = new GraphicsCapturePicker();

                // Initialise the picker with the parent HWND (required for WinUI 3 unpackaged)
                var initWithWindow = (IInitializeWithWindow)(object)picker;
                initWithWindow.Initialize(hwnd);

                _captureItem = await picker.PickSingleItemAsync();
                if (_captureItem is null)
                {
                    Debug.WriteLine("[STORM] User cancelled the capture picker.");
                    return;
                }

                BeginCaptureSession();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] StartCaptureWithPickerAsync exception: {ex.Message}");
                StartFallbackCapture();
            }
        }

        // =================================================================
        // Session management
        // =================================================================

        /// <summary>
        /// Creates the frame pool, attaches the <c>FrameArrived</c> handler
        /// and starts the capture session.
        /// </summary>
        private void BeginCaptureSession()
        {
            if (_device is null || _captureItem is null) return;

            SizeInt32 size = _captureItem.Size;
            CapturedWidth = size.Width;
            CapturedHeight = size.Height;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);

            _framePool.FrameArrived += OnFrameArrived;

            _captureItem.Closed += (_, _) => StopCapture();

            _session = _framePool.CreateCaptureSession(_captureItem);

            // Suppress the yellow capture border on Windows 11.
            // IsBorderRequired was added in 10.0.20348.0; the property
            // setter may throw on older builds, so we swallow the exception.
            try
            {
                _session.IsBorderRequired = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] IsBorderRequired not supported: {ex.Message}");
            }

            _session.StartCapture();

            _frameStopwatch.Restart();
            IsCapturing = true;

            Debug.WriteLine($"[STORM] Capture started — {CapturedWidth}×{CapturedHeight}");
        }

        /// <summary>Stops the active capture session and releases capture resources.</summary>
        public void StopCapture()
        {
            if (!IsCapturing) return;
            IsCapturing = false;

            _fallbackCts?.Cancel();
            _fallbackCts = null;

            _session?.Dispose();
            _session = null;

            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }

            _captureItem = null;
            _frameStopwatch.Stop();

            Debug.WriteLine("[STORM] Capture stopped.");
        }

        // =================================================================
        // Frame handling
        // =================================================================

        /// <summary>Minimum interval between frames based on <see cref="TargetFps"/>.</summary>
        private double MinFrameIntervalMs => 1000.0 / _targetFps;

        private int _isProcessingFrame = 0;

        /// <summary>
        /// Handler for <see cref="Direct3D11CaptureFramePool.FrameArrived"/>.
        /// Throttles to <see cref="TargetFps"/>, converts the surface to a
        /// <see cref="SoftwareBitmap"/>, extracts raw BGRA8 pixel bytes, and
        /// fires <see cref="FrameCaptured"/>.
        /// </summary>
        private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (!IsCapturing) return;

            // FPS throttle — skip this frame if we're ahead of schedule
            if (_frameStopwatch.Elapsed.TotalMilliseconds < MinFrameIntervalMs)
            {
                // Must still consume the frame to prevent the pool from stalling
                sender.TryGetNextFrame()?.Dispose();
                return;
            }

            // Prevent re-entrancy if encoding takes too long
            if (Interlocked.CompareExchange(ref _isProcessingFrame, 1, 0) != 0)
            {
                sender.TryGetNextFrame()?.Dispose();
                return;
            }

            try
            {
                _frameStopwatch.Restart();

                using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null) return;

                SizeInt32 frameSize = frame.ContentSize;
                int width = frameSize.Width;
                int height = frameSize.Height;

                if (width <= 0 || height <= 0) return;

                // Resize pool if the captured surface dimensions changed
                if (width != CapturedWidth || height != CapturedHeight)
                {
                    CapturedWidth = width;
                    CapturedHeight = height;
                    _framePool?.Recreate(
                        _device!,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        new SizeInt32 { Width = width, Height = height });
                }

                // Convert the Direct3D surface to a SoftwareBitmap
                using SoftwareBitmap softwareBitmap =
                    await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                        frame.Surface,
                        BitmapAlphaMode.Premultiplied);

                byte[] pixels = ExtractBgraPixels(softwareBitmap);
                FrameCaptured?.Invoke(pixels, width, height);
            }
            catch (ObjectDisposedException)
            {
                // Session was torn down while processing — safe to ignore
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM] FrameArrived error: {ex.Message}");
            }
            finally
            {
                _isProcessingFrame = 0;
            }
        }

        private byte[]? _pixelBuffer;

        /// <summary>
        /// Extracts raw BGRA8 pixel data from a <see cref="SoftwareBitmap"/>
        /// using <see cref="IMemoryBufferByteAccess"/> COM interop.
        /// Reuses a single byte array to prevent LOH destruction and memory leaks.
        /// </summary>
        private unsafe byte[] ExtractBgraPixels(SoftwareBitmap bitmap)
        {
            using BitmapBuffer buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
            using IMemoryBufferReference reference = buffer.CreateReference();

            var byteAccess = (IMemoryBufferByteAccess)reference;
            byteAccess.GetBuffer(out byte* dataPtr, out uint capacity);

            if (_pixelBuffer == null || _pixelBuffer.Length != capacity)
            {
                _pixelBuffer = new byte[capacity];
            }

            new ReadOnlySpan<byte>(dataPtr, (int)capacity).CopyTo(_pixelBuffer);

            return _pixelBuffer;
        }

        // =================================================================
        // Fallback — synthetic gradient capture
        // =================================================================

        private CancellationTokenSource? _fallbackCts;

        /// <summary>
        /// Starts a background loop that generates synthetic gradient frames
        /// when the real capture pipeline is unavailable.
        /// </summary>
        private void StartFallbackCapture()
        {
            if (IsCapturing) return;

            const int width = 1920;
            const int height = 1080;
            CapturedWidth = width;
            CapturedHeight = height;
            IsCapturing = true;

            _fallbackCts = new CancellationTokenSource();
            CancellationToken token = _fallbackCts.Token;

            _ = Task.Run(async () =>
            {
                int frameIndex = 0;
                var sw = Stopwatch.StartNew();

                while (!token.IsCancellationRequested)
                {
                    sw.Restart();

                    byte[] pixels = GenerateGradientFrame(width, height, frameIndex);
                    FrameCaptured?.Invoke(pixels, width, height);
                    frameIndex++;

                    int targetMs = (int)(1000.0 / _targetFps);
                    int elapsed = (int)sw.ElapsedMilliseconds;
                    int delay = Math.Max(1, targetMs - elapsed);

                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }

                IsCapturing = false;
            }, token);

            Debug.WriteLine("[STORM] Fallback gradient capture started.");
        }

        /// <summary>
        /// Generates a BGRA8 gradient bitmap that shifts colour based on
        /// <paramref name="frameIndex"/> to provide visible animation.
        /// </summary>
        private static byte[] GenerateGradientFrame(int width, int height, int frameIndex)
        {
            byte[] pixels = new byte[width * height * 4];
            byte shift = (byte)(frameIndex % 256);

            for (int y = 0; y < height; y++)
            {
                byte rowValue = (byte)((y * 255 / height + shift) & 0xFF);
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    byte colValue = (byte)((x * 255 / width + shift) & 0xFF);

                    pixels[offset] = colValue;               // B
                    pixels[offset + 1] = rowValue;            // G
                    pixels[offset + 2] = (byte)(255 - shift); // R
                    pixels[offset + 3] = 255;                 // A
                }
            }

            return pixels;
        }

        // =================================================================
        // IDisposable
        // =================================================================

        /// <summary>Releases all resources held by the service.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopCapture();

            _device?.Dispose();
            _device = null;
        }
    }
}
