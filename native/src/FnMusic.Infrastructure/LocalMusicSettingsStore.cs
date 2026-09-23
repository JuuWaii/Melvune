using System.Text.Json;

namespace FnMusic.Infrastructure;

public sealed class LocalMusicSettingsStore(string directory)
{
    private readonly string file = Path.Combine(directory, "local-music.json");
    public async Task<LocalMusicSettings?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 16384) throw new InvalidDataException();
        var settings = JsonSerializer.Deserialize<LocalMusicSettings>(await File.ReadAllTextAsync(file, ct));
        if (settings is null || settings.SourceId == Guid.Empty || string.IsNullOrWhiteSpace(settings.Directory) || !Path.IsPathFullyQualified(settings.Directory))
            throw new InvalidDataException();
        return settings;
    }
    public async Task SaveAsync(LocalMusicSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(settings), ct);
            ct.ThrowIfCancellationRequested(); File.Move(pending, file, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
