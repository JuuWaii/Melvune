using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private Task<WriteableBitmap>? playlistWatermark;
    private async void PlaylistWatermark_Loaded(object sender, RoutedEventArgs args)
    {
        if (closed || sender is not Image image) return;
        try
        {
            var source = await (playlistWatermark ??= LoadPlaylistWatermarkAsync());
            if (!closed && image.IsLoaded) image.Source = source;
        }
        catch (Exception) { /* Leave a clean text row if the optional source watermark is unavailable. */ }
    }
    private async Task<WriteableBitmap> LoadPlaylistWatermarkAsync()
    {
        // Decode and tone conversion run off the UI thread once per window; the source stays unmodified.
        var decoded = await Task.Run(async () =>
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "FnOsMusic.png"));
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            if (stream.Size > 1024 * 1024) throw new InvalidDataException();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth is 0 or > 1024 || decoder.PixelHeight is 0 or > 1024) throw new InvalidDataException();
            var provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = provider.DetachPixelData();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte gray = (byte)((29 * pixels[i] + 150 * pixels[i + 1] + 77 * pixels[i + 2] + 128) >> 8);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
            }
            return (Width: (int)decoder.PixelWidth, Height: (int)decoder.PixelHeight, Pixels: pixels);
        });
        // Creating the XAML image source stays on the caller's UI synchronization context.
        var bitmap = new WriteableBitmap(decoded.Width, decoded.Height);
        using (var output = bitmap.PixelBuffer.AsStream()) await output.WriteAsync(decoded.Pixels);
        bitmap.Invalidate();
        return bitmap;
    }
}
