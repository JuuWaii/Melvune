using System.Text.Json;
using System.Text.Json.Serialization;

namespace FnMusic.Infrastructure;

public sealed record LocalLibrary(Guid SourceId, string Directory, string Name, string Group)
{
    [JsonIgnore] public LocalMusicSettings Settings => new(SourceId, Directory);
}
public sealed record LocalLibraryCatalog(long Revision, IReadOnlyList<LocalLibrary> Libraries)
{
    public static LocalLibraryCatalog Empty => new(0, []);
}
public sealed class LocalLibraryConflictException() : IOException("Libraries changed. Reload before saving.");

/// <summary>Multiple directory identities, with non-destructive legacy migration.</summary>
public sealed class LocalLibraryStore(string directory)
{
    private readonly string file = Path.Combine(directory, "local-libraries.json");
    private sealed record Document(int Version, long Revision, LocalLibrary[] Libraries);
    public static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public async Task<LocalLibraryCatalog> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(file))
        {
            var legacy = await new LocalMusicSettingsStore(directory).LoadAsync(ct);
            if (legacy is null) return LocalLibraryCatalog.Empty;
            var migrated = new LocalLibraryCatalog(0, [new(legacy.SourceId, NormalizePath(legacy.Directory), "本地音乐", "")]);
            Validate(migrated);
            return migrated;
        }
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 1024 * 1024) throw new InvalidDataException();
        var document = await JsonSerializer.DeserializeAsync<Document>(input, cancellationToken: ct);
        if (document is null || document.Version != 1) throw new InvalidDataException();
        var catalog = new LocalLibraryCatalog(document.Revision, document.Libraries);
        Validate(catalog); return catalog;
    }

    public async Task<LocalLibraryCatalog> SaveAsync(LocalLibraryCatalog catalog, CancellationToken ct = default)
    {
        Validate(catalog);
        ct.ThrowIfCancellationRequested();
        System.IO.Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var writer = await LockAsync(timeout.Token);
        if ((await LoadAsync(ct)).Revision != catalog.Revision) throw new LocalLibraryConflictException();
        var next = catalog with { Revision = checked(catalog.Revision + 1) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, next.Revision, next.Libraries.ToArray()));
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(pending, bytes, ct);
            ct.ThrowIfCancellationRequested(); File.Move(pending, file, true);
            return next;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50, ct); }
        }
    }

    private static void Validate(LocalLibraryCatalog catalog)
    {
        if (catalog.Revision < 0 || catalog.Libraries is null || catalog.Libraries.Count > 100) throw new InvalidDataException();
        var ids = new HashSet<Guid>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in catalog.Libraries)
        {
            if (library is null || library.SourceId == Guid.Empty || !ids.Add(library.SourceId) ||
                string.IsNullOrWhiteSpace(library.Directory) || library.Directory.Length > 4096 || !Path.IsPathFullyQualified(library.Directory) ||
                !paths.Add(NormalizePath(library.Directory)) || string.IsNullOrWhiteSpace(library.Name) || library.Name.Length > 120 ||
                library.Group is null || library.Group.Length > 120) throw new InvalidDataException();
        }
    }
}
