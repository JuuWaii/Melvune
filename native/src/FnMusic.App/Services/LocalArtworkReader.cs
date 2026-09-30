using Windows.Storage;
using Windows.Storage.FileProperties;
using System.Runtime.InteropServices.WindowsRuntime;

namespace FnMusic.App.Services;

internal static class LocalArtworkReader
{
    internal static async Task<byte[]?> ReadThumbnailAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var thumbnail = await ReadWindowsThumbnailAsync(path, ct);
            if (thumbnail is not null) return thumbnail;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* Shell handlers are optional; try the stream decoder. */ }
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

    private static async Task<byte[]?> ReadWindowsThumbnailAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 256,
            ThumbnailOptions.UseCurrentScale).AsTask(ct);
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
