using Melvune.Infrastructure;
using Windows.Storage;

namespace Melvune.App.Services;

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
        if (result.DurationSeconds > 0 && !string.IsNullOrWhiteSpace(result.Title)
            && !string.IsNullOrWhiteSpace(result.Artist) && !string.IsNullOrWhiteSpace(result.Album)) return result;

        // Windows may provide duration but omit valid tags (e.g. RIFF INFO in WAV).
        // Fill only missing fields; keep shell tags and duration when already available.
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
            string Tag(string key)
            {
                foreach (var tag in source.MetadataTags.Take(128))
                {
                    if (!string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string value in tag.Value.Take(8))
                    {
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        return value[..Math.Min(value.Length, 1024)].Trim();
                    }
                }
                return "";
            }
            result = result with
            {
                Title = string.IsNullOrWhiteSpace(result.Title) ? Tag("title") : result.Title,
                Artist = string.IsNullOrWhiteSpace(result.Artist) ? Tag("artist") : result.Artist,
                Album = string.IsNullOrWhiteSpace(result.Album) ? Tag("album") : result.Album,
                DurationSeconds = result.DurationSeconds > 0 ? result.DurationSeconds : source.Duration.TotalSeconds
            };
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
