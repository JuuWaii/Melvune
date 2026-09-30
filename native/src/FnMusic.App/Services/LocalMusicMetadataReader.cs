using FnMusic.Infrastructure;
using Windows.Storage;

namespace FnMusic.App.Services;

public static class LocalMusicMetadataReader
{
    public static async Task<LocalMusicMetadata> ReadAsync(string path, CancellationToken ct)
    {
        var result = new LocalMusicMetadata("", "", 0);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(timeout.Token);
                var metadata = await file.Properties.GetMusicPropertiesAsync().AsTask(timeout.Token);
                result = new(metadata.Title, metadata.Artist, metadata.Duration.TotalSeconds) { Album = metadata.Album };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* Unsupported shell metadata still permits decoder probing. */ }
        }
        ct.ThrowIfCancellationRequested();
        if (result.DurationSeconds > 0) return result;

        // Some supported audio formats have no Windows property handler (e.g. AIFF).
        // Reuse stream-only decoder configuration; never let embedded URLs open files/network.
        SeekablePlayback? probe = null;
        using var fallbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fallbackTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            probe = new SeekablePlayback(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous));
            using var cancel = fallbackTimeout.Token.Register(() => _ = probe.Stop());
            var source = await probe.Opening.WaitAsync(fallbackTimeout.Token);
            fallbackTimeout.Token.ThrowIfCancellationRequested();
            result = result with { DurationSeconds = source.Duration.TotalSeconds };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* Preserve any shell tags when probing is unsupported or times out. */ }
        finally
        {
            if (probe is not null) await probe.Stop();
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }
}
