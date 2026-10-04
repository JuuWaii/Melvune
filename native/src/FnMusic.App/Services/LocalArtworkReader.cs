using Windows.Storage;
using Windows.Storage.FileProperties;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FnMusic.App.Services;

internal static class LocalArtworkReader
{
    internal static Task<byte[]?> ReadThumbnailAsync(string path, CancellationToken ct) => ReadThumbnailAsync(path, ArtworkDecoder.ThumbnailEdge, ct);

    internal static async Task<byte[]?> ReadThumbnailAsync(string path, int requestedSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (requestedSize is <= 0 or > ArtworkDecoder.FullCoverEdge) throw new ArgumentOutOfRangeException(nameof(requestedSize));
        // Full-page covers should use the embedded original rather than an already reduced Shell thumbnail.
        if (requestedSize > ArtworkDecoder.ThumbnailEdge)
        {
            var embedded = await ReadEmbeddedAsync(path, ct);
            if (embedded is not null) return embedded;
        }
        try
        {
            var thumbnail = await ReadWindowsThumbnailAsync(path, requestedSize, ct);
            if (thumbnail is not null) return thumbnail;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* Shell handlers are optional; try the stream decoder. */ }
        return requestedSize > ArtworkDecoder.ThumbnailEdge ? null : await ReadEmbeddedAsync(path, ct);
    }

    private static async Task<byte[]?> ReadEmbeddedAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var probe = new SeekablePlayback(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous));
        try
        {
            using var cancel = timeout.Token.Register(() => _ = probe.Stop());
            var source = await probe.Opening.WaitAsync(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (!source.HasThumbnail) return null;
            var thumbnail = source.ExtractThumbnail();
            var buffer = thumbnail.Buffer;
            if (buffer is null || buffer.Length == 0 || buffer.Length > 4 * 1024 * 1024) return null;
            var bytes = buffer.ToArray();
            timeout.Token.ThrowIfCancellationRequested();
            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        finally { await probe.Stop(); }
    }

    private static async Task<byte[]?> ReadWindowsThumbnailAsync(string path, int requestedSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, (uint)requestedSize,
            ThumbnailOptions.None).AsTask(ct);
        const int limit = 4 * 1024 * 1024;
        if (thumbnail is null || thumbnail.Type != ThumbnailType.Image || thumbnail.Size == 0 || thumbnail.Size > limit) return null;
        using var stream = thumbnail.AsStreamForRead();
        using var memory = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > limit) return null;
            memory.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        return memory.Length == 0 ? null : memory.ToArray();
    }
}
