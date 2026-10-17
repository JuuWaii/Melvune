using System.Text.Json;

namespace Melvune.Infrastructure;

/// <summary>Bounded per-library metadata snapshot; no music directory access during restoration.</summary>
public sealed class LocalMusicIndexStore(string directory)
{
    private const int Limit = 32 * 1024 * 1024;
    private sealed record Document(int Version, LocalMusicIndex Index);
    private string FilePath(Guid id) => id != Guid.Empty
        ? Path.Combine(directory, "local-index", id.ToString("N") + ".json") : throw new ArgumentException();

    public async Task<LocalMusicSource?> LoadAsync(LocalMusicSettings settings, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var input = new FileStream(FilePath(settings.SourceId), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
            if (input.Length is <= 0 or > Limit) return null;
            byte[] bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, ct);
            var document = JsonSerializer.Deserialize<Document>(bytes);
            if (document?.Version != 1 || document.Index?.Settings.SourceId != settings.SourceId
                || !string.Equals(LocalLibraryStore.NormalizePath(document.Index.Settings.Directory), LocalLibraryStore.NormalizePath(settings.Directory), StringComparison.OrdinalIgnoreCase)) return null;
            var source = LocalMusicSource.FromIndex(document.Index); ct.ThrowIfCancellationRequested(); return source;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException) { return null; }
    }

    public async Task SaveAsync(LocalMusicSource source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var index = source.CreateIndex(); LocalMusicSource.FromIndex(index);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, index));
        if (bytes.Length > Limit) throw new InvalidDataException();
        string file = FilePath(source.Source.Id); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(pending, bytes, ct); ct.ThrowIfCancellationRequested(); File.Move(pending, file, true); }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
