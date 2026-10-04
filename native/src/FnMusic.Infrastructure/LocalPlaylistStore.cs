using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

/// <summary>Atomic catalog writes with revision checks across windows/processes.</summary>
public sealed class LocalPlaylistStore(string directory)
{
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly string file = Path.Combine(directory, "local-playlists.json");
    private sealed record Document(int Version, long Revision, LocalPlaylist[] Playlists);

    public async Task<LocalPlaylistCatalog> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(file)) return LocalPlaylistCatalog.Empty;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Playlist catalog is too large.");
        var document = await JsonSerializer.DeserializeAsync<Document>(stream, cancellationToken: ct).ConfigureAwait(false);
        if (document is null || document.Version is not (1 or 2)) throw new InvalidDataException("Unsupported playlist catalog.");
        var catalog = new LocalPlaylistCatalog(document.Revision, document.Playlists);
        Validate(catalog);
        return catalog;
    }

    public async Task<LocalPlaylistCatalog> SaveAsync(LocalPlaylistCatalog catalog, CancellationToken ct = default)
    {
        Validate(catalog);
        var next = catalog with { Revision = checked(catalog.Revision + 1) };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(2, next.Revision, next.Playlists.ToArray()));
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Playlist catalog is too large.");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        // FileShare.None is the cross-process writer lock; cancellation bounds contention.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var writerLock = await AcquireWriterAsync(timeout.Token).ConfigureAwait(false);
        var current = await LoadAsync(ct).ConfigureAwait(false);
        if (current.Revision != catalog.Revision) throw new PlaylistConflictException();
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(pending, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(pending, file, true);
            return next;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private async Task<FileStream> AcquireWriterAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
    }

    private static void Validate(LocalPlaylistCatalog catalog)
    {
        if (catalog.Revision < 0 || catalog.Playlists is null || catalog.Playlists.Count > 500)
            throw new InvalidDataException("Invalid playlist catalog.");
        var playlists = new HashSet<Guid>();
        foreach (var playlist in catalog.Playlists)
        {
            if (playlist is null || playlist.Id == Guid.Empty || !playlists.Add(playlist.Id) ||
                string.IsNullOrWhiteSpace(playlist.Name) || playlist.Name.Length > 120 || playlist.Entries is null || playlist.Entries.Count > 10000)
                throw new InvalidDataException("Invalid playlist.");
            var entries = new HashSet<Guid>();
            var tracks = new HashSet<TrackReference>();
            foreach (var entry in playlist.Entries)
            {
                if (entry is null || entry.Id == Guid.Empty || !entries.Add(entry.Id) || entry.Track is null ||
                    entry.Track.SourceInstanceId == Guid.Empty || string.IsNullOrWhiteSpace(entry.Track.TrackId) || entry.Track.TrackId.Length > 4096 ||
                    !tracks.Add(entry.Track) || !TextValid(entry.Title) || !TextValid(entry.Artist) || !TextValid(entry.Album) ||
                    (entry.LibraryId is not null && entry.LibraryId.Length > 4096) ||
                    !LibrariesValid(entry) || !RelatedCollectionsValid(entry) ||
                    (entry.CoverId is not null && entry.CoverId.Length > 4096) ||
                    !double.IsFinite(entry.DurationSeconds) || entry.DurationSeconds is < 0 or > 864000)
                    throw new InvalidDataException("Invalid playlist entry.");
            }
        }
    }
    private static bool TextValid(string text) => text is not null && text.Length <= 4096;
    private static bool RelatedCollectionsValid(LocalPlaylistEntry entry) =>
        (entry.AlbumId is null || entry.AlbumId.Length is > 0 and <= 256)
        && (entry.Artists is null || entry.Artists.Count <= 100 && entry.Artists.All(a => a is not null
            && !string.IsNullOrWhiteSpace(a.Id) && a.Id.Length <= 256 && TextValid(a.Name))
            && entry.Artists.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() == entry.Artists.Count);
    private static bool LibrariesValid(LocalPlaylistEntry entry)
    {
        var ids = entry.GetLibraryIds();
        return ids.Count <= 100 && ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 4096)
            && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count
            && (entry.LibraryIds is null || entry.LibraryId == ids.FirstOrDefault());
    }
}
