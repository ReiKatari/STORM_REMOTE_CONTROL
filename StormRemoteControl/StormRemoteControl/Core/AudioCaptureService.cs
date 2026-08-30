// ==========================================================================
// AudioCaptureService.cs — WASAPI loopback capture via WinRT AudioGraph
// ==========================================================================
// Captures system audio output from the default render device using the
// Windows.Media.Audio.AudioGraph API, converts the float-32 stereo
// quantum data to 16-bit PCM 16 kHz mono for bandwidth-efficient
// transport, and fires the AudioDataCaptured event.
// ==========================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Captures system audio via WASAPI loopback using the WinRT
    /// <see cref="AudioGraph"/> pipeline. Emits 16-bit PCM data at
    /// 16 kHz mono through the <see cref="AudioDataCaptured"/> event.
    /// </summary>
    public sealed class AudioCaptureService : IDisposable
    {
        // -----------------------------------------------------------------
        // COM interface for raw byte access into AudioBuffer
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
        // Constants
        // -----------------------------------------------------------------

        /// <summary>Target output sample rate after down-sampling (Hz).</summary>
        private const uint TargetSampleRate = 16_000;

        /// <summary>Number of output channels (mono).</summary>
        private const uint TargetChannels = 1;

        // -----------------------------------------------------------------
        // Instance state
        // -----------------------------------------------------------------

        private AudioGraph? _audioGraph;
        private AudioDeviceInputNode? _deviceInputNode;
        private AudioFrameOutputNode? _frameOutputNode;
        private bool _disposed;

        /// <summary>
        /// Graph-level encoding properties — sample rate and channel count
        /// of the device input before we down-sample.
        /// </summary>
        private uint _graphSampleRate;
        private uint _graphChannels;

        /// <summary>
        /// Fires when a chunk of PCM audio has been captured.
        /// The byte array contains 16-bit signed PCM at 16 kHz, mono.
        /// </summary>
        public event Action<byte[]>? AudioDataCaptured;

        /// <summary>Gets a value indicating whether audio capture is active.</summary>
        public bool IsCapturing { get; private set; }

        // =================================================================
        // Start / Stop
        // =================================================================

        /// <summary>
        /// Initialises the AudioGraph pipeline and begins capturing the
        /// default system audio render device (loopback).
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the graph was created and capture started
        /// successfully; <see langword="false"/> otherwise.
        /// </returns>
        public async Task<bool> StartCaptureAsync()
        {
            if (IsCapturing) return true;

            try
            {
                // --- Build the AudioGraph -----------------------------------
                var settings = new AudioGraphSettings(AudioRenderCategory.Media)
                {
                    QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency
                };

                CreateAudioGraphResult graphResult =
                    await AudioGraph.CreateAsync(settings);

                if (graphResult.Status != AudioGraphCreationStatus.Success)
                {
                    Debug.WriteLine(
                        $"[STORM AUDIO] AudioGraph creation failed: {graphResult.Status}");
                    return false;
                }

                _audioGraph = graphResult.Graph;
                _graphSampleRate = _audioGraph.EncodingProperties.SampleRate;
                _graphChannels = _audioGraph.EncodingProperties.ChannelCount;

                // --- Create device input (loopback) -------------------------
                CreateAudioDeviceInputNodeResult inputResult =
                    await _audioGraph.CreateDeviceInputNodeAsync(
                        Windows.Media.Capture.MediaCategory.Other);

                if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    Debug.WriteLine(
                        $"[STORM AUDIO] DeviceInputNode failed: {inputResult.Status}");
                    CleanupGraph();
                    return false;
                }

                _deviceInputNode = inputResult.DeviceInputNode;

                // --- Create frame output node -------------------------------
                _frameOutputNode = _audioGraph.CreateFrameOutputNode();
                _deviceInputNode.AddOutgoingConnection(_frameOutputNode);

                _audioGraph.QuantumStarted += OnQuantumStarted;

                // --- Go! ----------------------------------------------------
                _audioGraph.Start();
                IsCapturing = true;

                Debug.WriteLine(
                    $"[STORM AUDIO] Capture started — " +
                    $"{_graphSampleRate} Hz, {_graphChannels} ch, " +
                    $"quantum {_audioGraph.SamplesPerQuantum} samples");

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STORM AUDIO] StartCaptureAsync exception: {ex.Message}");
                CleanupGraph();
                return false;
            }
        }

        /// <summary>Stops the audio capture and releases graph resources.</summary>
        public void StopCapture()
        {
            if (!IsCapturing) return;
            IsCapturing = false;

            if (_audioGraph is not null)
            {
                _audioGraph.QuantumStarted -= OnQuantumStarted;
                _audioGraph.Stop();
            }

            CleanupGraph();
            Debug.WriteLine("[STORM AUDIO] Capture stopped.");
        }

        // =================================================================
        // Quantum handler
        // =================================================================

        /// <summary>
        /// Called by the AudioGraph each time a new quantum of audio data
        /// is available. Reads raw float-32 PCM from the frame output node,
        /// downsamples to 16 kHz mono 16-bit PCM, and fires
        /// <see cref="AudioDataCaptured"/>.
        /// </summary>
        private void OnQuantumStarted(AudioGraph sender, object args)
        {
            if (!IsCapturing || _frameOutputNode is null) return;

            AudioFrame frame = _frameOutputNode.GetFrame();
            ProcessAudioFrame(frame);
        }

        /// <summary>
        /// Extracts raw bytes from an <see cref="AudioFrame"/>, converts
        /// float-32 samples to 16-bit PCM, and downsamples to the target
        /// rate/channel configuration.
        /// </summary>
        private unsafe void ProcessAudioFrame(AudioFrame frame)
        {
            using AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using IMemoryBufferReference reference = buffer.CreateReference();

            var byteAccess = (IMemoryBufferByteAccess)reference;
            byteAccess.GetBuffer(out byte* dataPtr, out uint capacityBytes);

            if (capacityBytes == 0) return;

            // The graph delivers IEEE float-32 samples
            int totalFloatSamples = (int)(capacityBytes / sizeof(float));
            if (totalFloatSamples == 0) return;

            var floatSpan = new ReadOnlySpan<float>(dataPtr, totalFloatSamples);

            // Downsample and convert to 16-bit PCM mono
            byte[] pcm16 = DownsampleToMono16(floatSpan, _graphSampleRate, _graphChannels);

            if (pcm16.Length > 0)
            {
                AudioDataCaptured?.Invoke(pcm16);
            }
        }

        // =================================================================
        // DSP helpers
        // =================================================================

        /// <summary>
        /// Converts interleaved float-32 PCM to 16-bit signed PCM,
        /// mixing down to mono and resampling from
        /// <paramref name="sourceSampleRate"/> to <see cref="TargetSampleRate"/>
        /// using simple linear decimation.
        /// </summary>
        /// <param name="source">Interleaved float-32 samples.</param>
        /// <param name="sourceSampleRate">Sample rate of the source data.</param>
        /// <param name="sourceChannels">Channel count of the source data.</param>
        /// <returns>16-bit signed PCM byte array at 16 kHz, mono.</returns>
        private static byte[] DownsampleToMono16(
            ReadOnlySpan<float> source,
            uint sourceSampleRate,
            uint sourceChannels)
        {
            if (source.IsEmpty || sourceChannels == 0) return [];

            int sourceFrameCount = source.Length / (int)sourceChannels;
            if (sourceFrameCount == 0) return [];

            // Compute decimation ratio
            double ratio = (double)sourceSampleRate / TargetSampleRate;
            int outputFrameCount = (int)(sourceFrameCount / ratio);
            if (outputFrameCount <= 0) return [];

            byte[] output = new byte[outputFrameCount * 2]; // 16-bit = 2 bytes per sample

            for (int i = 0; i < outputFrameCount; i++)
            {
                // Map output sample index back to source frame
                int srcFrame = (int)(i * ratio);
                if (srcFrame >= sourceFrameCount) srcFrame = sourceFrameCount - 1;

                // Mix channels to mono by averaging
                float monoSample = 0f;
                int baseIndex = srcFrame * (int)sourceChannels;
                for (int ch = 0; ch < (int)sourceChannels; ch++)
                {
                    int idx = baseIndex + ch;
                    if (idx < source.Length)
                    {
                        monoSample += source[idx];
                    }
                }
                monoSample /= sourceChannels;

                // Clamp and convert to Int16
                monoSample = Math.Clamp(monoSample, -1.0f, 1.0f);
                short pcm16Value = (short)(monoSample * short.MaxValue);

                // Little-endian write
                output[i * 2] = (byte)(pcm16Value & 0xFF);
                output[i * 2 + 1] = (byte)((pcm16Value >> 8) & 0xFF);
            }

            return output;
        }

        // =================================================================
        // Cleanup / Dispose
        // =================================================================

        /// <summary>
        /// Releases the AudioGraph, input node, and output node without
        /// changing the <see cref="IsCapturing"/> flag (caller handles that).
        /// </summary>
        private void CleanupGraph()
        {
            _frameOutputNode?.Dispose();
            _frameOutputNode = null;

            _deviceInputNode?.Dispose();
            _deviceInputNode = null;

            _audioGraph?.Dispose();
            _audioGraph = null;
        }

        /// <summary>Releases all resources held by the service.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopCapture();
        }
    }
}
