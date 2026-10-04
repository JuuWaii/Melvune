using Microsoft.UI.Xaml.Media.Imaging;

namespace FnMusic.App.Services;

// Owned by the window's UI thread. The budget covers retained decoded pixels, not total process memory.
internal sealed class ArtworkCache(int maxEntries = 96, long maxPixelBytes = 48 * 1024 * 1024)
{
    private sealed record Entry(string Key, BitmapSource Image, long PixelBytes);
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recent = new();

    internal int Count => entries.Count;
    internal long PixelBytes { get; private set; }
    internal IEnumerable<BitmapSource> Values => recent.Select(entry => entry.Image);

    internal bool TryGetValue(string key, out BitmapSource image)
    {
        if (!entries.TryGetValue(key, out var node)) { image = null!; return false; }
        recent.Remove(node); recent.AddLast(node);
        image = node.Value.Image;
        return true;
    }

    internal void Set(string key, BitmapSource image)
    {
        if (entries.Remove(key, out var previous)) Remove(previous);
        long bytes = checked((long)image.PixelWidth * image.PixelHeight * 4);
        if (bytes <= 0 || bytes > maxPixelBytes || maxEntries <= 0) return;
        while (recent.First is { } oldest && (entries.Count >= maxEntries || PixelBytes + bytes > maxPixelBytes))
        {
            entries.Remove(oldest.Value.Key); Remove(oldest);
        }
        var node = recent.AddLast(new Entry(key, image, bytes));
        entries.Add(key, node); PixelBytes += bytes;
    }

    private void Remove(LinkedListNode<Entry> node)
    { recent.Remove(node); PixelBytes -= node.Value.PixelBytes; }

    internal void Clear()
    { entries.Clear(); recent.Clear(); PixelBytes = 0; }
}
