#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FnMusic.App.Services;

internal static class ArtworkFixture
{
    internal static async Task<byte[]> EncodeAsync(int width, int height, bool rotated = false)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(rotated ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, stream);
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = 64; pixels[i + 1] = 96; pixels[i + 2] = 128; pixels[i + 3] = 255; }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        if (rotated) await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
        { ["System.Photo.Orientation"] = new BitmapTypedValue((ushort)6, global::Windows.Foundation.PropertyType.UInt16) });
        await encoder.FlushAsync();
        stream.Seek(0);
        using var input = stream.AsStreamForRead();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }
}
#endif
