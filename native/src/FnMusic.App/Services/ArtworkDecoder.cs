using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FnMusic.App.Services;

internal static class ArtworkDecoder
{
    internal const int ThumbnailEdge = 256;
    internal const int FullCoverEdge = 1024;

    internal static async Task<WriteableBitmap> DecodeAsync(byte[] bytes, int longestEdge, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length is 0 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (longestEdge is <= 0 or > FullCoverEdge) throw new ArgumentOutOfRangeException(nameof(longestEdge));
        using var input = new MemoryStream(bytes);
        using var stream = input.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        uint width = decoder.PixelWidth, height = decoder.PixelHeight;
        if (width == 0 || height == 0) throw new InvalidDataException();
        double scale = Math.Min(1, (double)longestEdge / Math.Max(width, height));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Floor(width * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Floor(height * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        // Decode one static frame at the target size and normalize EXIF orientation before handing pixels to XAML.
        using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        if (pixels.PixelWidth <= 0 || pixels.PixelHeight <= 0 || Math.Max(pixels.PixelWidth, pixels.PixelHeight) > longestEdge)
            throw new InvalidDataException();
        var bitmap = new WriteableBitmap(pixels.PixelWidth, pixels.PixelHeight);
        pixels.CopyToBuffer(bitmap.PixelBuffer);
        bitmap.Invalidate();
        ct.ThrowIfCancellationRequested();
        return bitmap;
    }
}
