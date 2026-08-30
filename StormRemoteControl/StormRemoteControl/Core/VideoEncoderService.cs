// ==========================================================================
// VideoEncoderService.cs — JPEG frame encoder via Windows.Graphics.Imaging
// ==========================================================================
// Encodes raw BGRA8 pixel buffers to JPEG byte arrays using the WinRT
// BitmapEncoder pipeline.  Provides synchronous and asynchronous paths,
// optional down-scaling for bandwidth-limited scenarios, and preset
// quality profiles.
// ==========================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using StormRemoteControl.Models;

namespace StormRemoteControl.Core
{
    /// <summary>
    /// Quality profile presets that control JPEG quality and maximum
    /// encoded resolution.
    /// </summary>
    public enum QualityProfile
    {
        /// <summary>Low quality / small size — max 1280×720, quality 40.</summary>
        Speed,

        /// <summary>High quality / large size — native resolution, quality 85.</summary>
        Quality,

        /// <summary>Balanced — native resolution, quality 65.</summary>
        Auto
    }

    /// <summary>
    /// Encodes raw BGRA8 pixel data into JPEG byte arrays, optionally
    /// resizing the output for bandwidth-constrained connections.
    /// </summary>
    public sealed class VideoEncoderService
    {
        // =================================================================
        // Public API
        // =================================================================

        /// <summary>
        /// Asynchronously encodes a BGRA8 pixel buffer to a JPEG byte array.
        /// </summary>
        /// <param name="bgraPixels">Raw BGRA8 pixel data.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="quality">
        /// JPEG quality in the range <c>[1, 100]</c>. Default is <c>75</c>.
        /// </param>
        /// <returns>The JPEG-encoded image bytes.</returns>
        public async Task<byte[]> EncodeFrameAsync(
            byte[] bgraPixels, int width, int height, int quality = 75)
        {
            ValidateInput(bgraPixels, width, height);
            quality = Math.Clamp(quality, 1, 100);

            using var stream = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await CreateJpegEncoderAsync(stream, quality);

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)width,
                (uint)height,
                dpiX: 96.0,
                dpiY: 96.0,
                bgraPixels);

            await encoder.FlushAsync();
            return await StreamToBytesAsync(stream);
        }

        /// <summary>
        /// Synchronously encodes a BGRA8 pixel buffer to a JPEG byte array.
        /// Blocks the calling thread; prefer <see cref="EncodeFrameAsync"/>
        /// from asynchronous contexts.
        /// </summary>
        /// <param name="bgraPixels">Raw BGRA8 pixel data.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="quality">
        /// JPEG quality in the range <c>[1, 100]</c>. Default is <c>75</c>.
        /// </param>
        /// <returns>The JPEG-encoded image bytes.</returns>
        public byte[] EncodeFrame(
            byte[] bgraPixels, int width, int height, int quality = 75)
        {
            // Run the WinRT async pipeline on a thread-pool thread to avoid
            // potential dead-locks when called from an STA context.
            return Task.Run(() => EncodeFrameAsync(bgraPixels, width, height, quality))
                       .GetAwaiter()
                       .GetResult();
        }

        /// <summary>
        /// Asynchronously encodes a BGRA8 pixel buffer to a JPEG byte array
        /// while resizing the output to
        /// <paramref name="targetWidth"/>×<paramref name="targetHeight"/>.
        /// </summary>
        /// <param name="bgraPixels">Raw BGRA8 pixel data.</param>
        /// <param name="width">Source frame width in pixels.</param>
        /// <param name="height">Source frame height in pixels.</param>
        /// <param name="targetWidth">Desired output width.</param>
        /// <param name="targetHeight">Desired output height.</param>
        /// <param name="quality">
        /// JPEG quality in the range <c>[1, 100]</c>. Default is <c>60</c>.
        /// </param>
        /// <returns>The JPEG-encoded and resized image bytes.</returns>
        public async Task<byte[]> EncodeFrameResizedAsync(
            byte[] bgraPixels, int width, int height,
            int targetWidth, int targetHeight, int quality = 60)
        {
            ValidateInput(bgraPixels, width, height);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetWidth, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetHeight, 0);
            quality = Math.Clamp(quality, 1, 100);

            using var outputStream = new InMemoryRandomAccessStream();
            BitmapEncoder jpegEncoder = await CreateJpegEncoderAsync(outputStream, quality);

            jpegEncoder.BitmapTransform.ScaledWidth = (uint)targetWidth;
            jpegEncoder.BitmapTransform.ScaledHeight = (uint)targetHeight;
            jpegEncoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;

            jpegEncoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)width,
                (uint)height,
                dpiX: 96.0,
                dpiY: 96.0,
                bgraPixels);

            await jpegEncoder.FlushAsync();
            return await StreamToBytesAsync(outputStream);
        }

        /// <summary>
        /// Returns the encoding settings for the specified
        /// <paramref name="profile"/>.
        /// </summary>
        /// <param name="profile">The quality profile.</param>
        /// <returns>
        /// A tuple of (<c>quality</c>, <c>maxWidth</c>, <c>maxHeight</c>).
        /// A <c>maxWidth</c>/<c>maxHeight</c> of <c>0</c> means native
        /// resolution (no cap).
        /// </returns>
        public (int quality, int maxWidth, int maxHeight) GetProfileSettings(QualityProfile profile)
        {
            return profile switch
            {
                QualityProfile.Speed   => (quality: 40, maxWidth: 1280, maxHeight: 720),
                QualityProfile.Quality => (quality: 85, maxWidth: 0,    maxHeight: 0),
                QualityProfile.Auto    => (quality: 65, maxWidth: 0,    maxHeight: 0),
                _ => (quality: 65, maxWidth: 0, maxHeight: 0)
            };
        }

        // =================================================================
        // Helpers
        // =================================================================

        /// <summary>
        /// Creates a JPEG <see cref="BitmapEncoder"/> with the given quality.
        /// </summary>
        private static async Task<BitmapEncoder> CreateJpegEncoderAsync(
            IRandomAccessStream stream, int quality)
        {
            var propertySet = new BitmapPropertySet
            {
                {
                    "ImageQuality",
                    new BitmapTypedValue(quality / 100.0, Windows.Foundation.PropertyType.Single)
                }
            };

            return await BitmapEncoder.CreateAsync(
                BitmapEncoder.JpegEncoderId, stream, propertySet);
        }

        /// <summary>
        /// Reads the full content of a <see cref="IRandomAccessStream"/>
        /// into a byte array.
        /// </summary>
        private static async Task<byte[]> StreamToBytesAsync(IRandomAccessStream stream)
        {
            stream.Seek(0);
            using var inputStream = stream.GetInputStreamAt(0);
            using var reader = new DataReader(inputStream);
            uint length = (uint)stream.Size;
            await reader.LoadAsync(length);
            byte[] result = new byte[length];
            reader.ReadBytes(result);
            return result;
        }

        /// <summary>
        /// Validates that the pixel buffer size matches the expected
        /// dimensions (4 bytes per pixel for BGRA8).
        /// </summary>
        private static void ValidateInput(byte[] bgraPixels, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(bgraPixels);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

            int expected = width * height * 4;
            if (bgraPixels.Length < expected)
            {
                throw new ArgumentException(
                    $"Pixel buffer too small. Expected at least {expected} bytes " +
                    $"for {width}×{height} BGRA8, but received {bgraPixels.Length}.",
                    nameof(bgraPixels));
            }
        }
    }
}
