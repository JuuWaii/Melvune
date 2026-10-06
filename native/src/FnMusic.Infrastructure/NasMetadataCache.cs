using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed record CachedCollection(MusicCollection Detail, IReadOnlyList<MusicTrack> Tracks)
{
    public IReadOnlyList<MusicCollection> Albums { get; init; } = [];
}
public sealed record NasMetadataSnapshot(Guid SourceId, DateTimeOffset UpdatedAt,
    IReadOnlyList<MusicTrack> Tracks, IReadOnlyList<CachedCollection> Albums,
    IReadOnlyList<CachedCollection> Artists, IReadOnlyList<NasPlaylistSnapshot> Playlists)
{
    public IReadOnlyList<MusicTrack> Favorites { get; init; } = [];
}

/// <summary>Account/source scoped, bounded metadata only. A failed write retains the previous complete document.</summary>
public sealed class NasMetadataCache(string directory)
{
    private const int DocumentLimit = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new() { IgnoreReadOnlyProperties = true };
    private sealed record Document(int Version, NasMetadataSnapshot Snapshot);
    private string SourceDirectory(Guid source)
    {
        if (source == Guid.Empty) throw new ArgumentException("A registered source is required.", nameof(source));
        return Path.Combine(directory, "nas-metadata", source.ToString("N"));
    }
    private string AssetPath(Guid source, string kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 4096) throw new ArgumentException("Invalid metadata identifier.");
        return Path.Combine(SourceDirectory(source), kind, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))));
    }
    public async Task<NasMetadataSnapshot?> LoadAsync(Guid source, CancellationToken ct)
    {
        var bytes = await ReadAsync(Path.Combine(SourceDirectory(source), "catalog.json"), DocumentLimit, ct);
        if (bytes is null) return null;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(bytes, Options);
            if (document is null || document.Version != 2 || document.Snapshot.SourceId != source) return null;
            Validate(document.Snapshot); return document.Snapshot;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NullReferenceException) { return null; }
    }
    public Task SaveAsync(NasMetadataSnapshot snapshot, CancellationToken ct)
    {
        Validate(snapshot);
        return WriteAsync(Path.Combine(SourceDirectory(snapshot.SourceId), "catalog.json"),
            JsonSerializer.SerializeToUtf8Bytes(new Document(2, snapshot), Options), DocumentLimit, ct);
    }
    public async Task<Lyrics?> ReadLyricsAsync(TrackReference track, CancellationToken ct)
    {
        var bytes = await ReadAsync(AssetPath(track.SourceInstanceId, "lyrics", track.TrackId), 2 * 1024 * 1024, ct);
        if (bytes is null) return null;
        try
        {
            var lyrics = JsonSerializer.Deserialize<Lyrics>(bytes, Options);
            ValidateLyrics(lyrics); return lyrics;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NullReferenceException) { return null; }
    }
    public Task SaveLyricsAsync(TrackReference track, Lyrics lyrics, CancellationToken ct)
    {
        ValidateLyrics(lyrics);
        return WriteAsync(AssetPath(track.SourceInstanceId, "lyrics", track.TrackId), JsonSerializer.SerializeToUtf8Bytes(lyrics, Options), 2 * 1024 * 1024, ct);
    }
    public Task<byte[]?> ReadCoverAsync(Guid source, string id, NasCoverSize size, CancellationToken ct) =>
        ReadAsync(AssetPath(source, "covers-" + (int)size, id), 4 * 1024 * 1024, ct);
    public Task SaveCoverAsync(Guid source, string id, NasCoverSize size, byte[] bytes, CancellationToken ct) =>
        WriteAsync(AssetPath(source, "covers-" + (int)size, id), bytes, 4 * 1024 * 1024, ct);
    public void RemoveInvalidCover(Guid source, string id, NasCoverSize size)
    {
        try { File.Delete(AssetPath(source, "covers-" + (int)size, id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static async Task<byte[]?> ReadAsync(string path, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
            if (stream.Length is <= 0 || stream.Length > limit) return null;
            var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct); return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private static async Task WriteAsync(string path, byte[] bytes, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 || bytes.Length > limit) throw new InvalidDataException("Metadata exceeds cache limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(pending, bytes, ct); ct.ThrowIfCancellationRequested(); File.Move(pending, path, true); }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private static void ValidateLyrics(Lyrics? lyrics)
    {
        if (lyrics?.Lines is null || lyrics.Lines.Count > 10000 || lyrics.Lines.Any(line => line is null || line.Text is null
            || line.Text.Length > 512000 || !double.IsFinite(line.Seconds) || line.Seconds < 0)) throw new InvalidDataException();
        if (lyrics.IsTimed && !lyrics.Lines.Select(line => line.Seconds).SequenceEqual(lyrics.Lines.Select(line => line.Seconds).Order())) throw new InvalidDataException();
    }
    internal static void Validate(NasMetadataSnapshot snapshot)
    {
        if (snapshot is null || snapshot.SourceId == Guid.Empty || snapshot.UpdatedAt == default
            || snapshot.Tracks is null || snapshot.Albums is null || snapshot.Artists is null || snapshot.Playlists is null
            || snapshot.Albums.Count > 100000 || snapshot.Artists.Count > 100000 || snapshot.Playlists.Count > 10000) throw new InvalidDataException();
        void Tracks(IReadOnlyList<MusicTrack> tracks)
        {
            if (tracks is null || tracks.Count > TrackCatalogReader.MaximumTracks || tracks.Any(track => track is null
                || track.SourceInstanceId != snapshot.SourceId || string.IsNullOrWhiteSpace(track.Id) || track.Id.Length > 256
                || track.Title is null || track.Title.Length > 65536 || track.Artist is null || track.Artist.Length > 65536
                || track.Album is null || track.Album.Length > 65536 || !double.IsFinite(track.DurationSeconds) || track.DurationSeconds < 0
                || track.LibraryIds is null || track.Artists is null)
                || tracks.Select(track => track.Reference).Distinct().Count() != tracks.Count) throw new InvalidDataException();
        }
        Tracks(snapshot.Tracks); Tracks(snapshot.Favorites);
        long count = snapshot.Tracks.Count + snapshot.Favorites.Count;
        foreach (var collections in new[] { snapshot.Albums, snapshot.Artists })
        {
            if (collections.Any(item => item?.Detail is null || string.IsNullOrWhiteSpace(item.Detail.Id) || item.Detail.Id.Length > 256
                || item.Detail.Name is null || item.Detail.Name.Length > 65536) || collections.Select(item => item.Detail.Id).Distinct().Count() != collections.Count) throw new InvalidDataException();
            foreach (var item in collections)
            {
                Tracks(item.Tracks); count += item.Tracks.Count;
                if (item.Albums is null || item.Albums.Count > 100000 || item.Albums.Any(album => album is null || string.IsNullOrWhiteSpace(album.Id)
                    || album.Id.Length > 256 || album.Name is null || album.Name.Length > 65536)
                    || item.Albums.Select(album => album.Id).Distinct().Count() != item.Albums.Count) throw new InvalidDataException();
                count += item.Albums.Count;
            }
        }
        foreach (var item in snapshot.Playlists)
        {
            if (item?.Playlist is null || item.Playlist.Reference.SourceInstanceId != snapshot.SourceId
                || string.IsNullOrWhiteSpace(item.Playlist.Reference.PlaylistId) || item.Playlist.Reference.PlaylistId.Length > 256
                || item.Playlist.Name is null || item.Playlist.Name.Length > 4096) throw new InvalidDataException();
            Tracks(item.Tracks); count += item.Tracks.Count;
        }
        if (count > 1000000 || snapshot.Playlists.Select(item => item.Playlist.Reference).Distinct().Count() != snapshot.Playlists.Count) throw new InvalidDataException();
    }
}
