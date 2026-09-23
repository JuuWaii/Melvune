using FnMusic.Infrastructure;
using Windows.Storage;

namespace FnMusic.App.Services;

public static class LocalMusicMetadataReader
{
    public static async Task<LocalMusicMetadata> ReadAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(timeout.Token);
        var metadata = await file.Properties.GetMusicPropertiesAsync().AsTask(timeout.Token);
        return new(metadata.Title, metadata.Artist, metadata.Duration.TotalSeconds);
    }
}
