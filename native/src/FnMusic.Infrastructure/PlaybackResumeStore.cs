using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;

public sealed record PlaybackResumeState(MusicTrack Track, double PositionSeconds);

/// <summary>Stores a source-qualified song snapshot, never its stream URL or local file path.</summary>
public sealed class PlaybackResumeStore(string directory)
{
    private const int MaximumBytes = 256 * 1024;
    private readonly string file = Path.Combine(directory, "playback-resume.json");
    // An explicit DTO omits computed MusicTrack properties and leaves no place for connection secrets.
    private sealed record Song(Guid SourceInstanceId, string Id, string Title, string Artist, string Album,
        double DurationSeconds, bool IsCue, bool IsFavorite, string SourceLabel, string? CoverId,
        string? AlbumId, string[] LibraryIds, TrackArtist[] Artists);
    private sealed record Document(int Version, Song Track, double PositionSeconds);

    public async Task<PlaybackResumeState?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumBytes) { ct.ThrowIfCancellationRequested(); return null; }
            // Bound the actual bytes read as well as the initial length, even if another writer grows the file.
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaximumBytes) { ct.ThrowIfCancellationRequested(); return null; }
                body.Write(buffer, 0, read);
            }
            ct.ThrowIfCancellationRequested();
            var document = JsonSerializer.Deserialize<Document>(body.GetBuffer().AsSpan(0, (int)body.Length));
            ct.ThrowIfCancellationRequested();
            if (document is null || document.Version != 1 || document.Track is null) return null;
            var song = document.Track;
            var track = new MusicTrack(song.Id, song.Title, song.Artist, song.DurationSeconds, song.IsCue)
            {
                SourceInstanceId = song.SourceInstanceId, Album = song.Album, AlbumId = song.AlbumId,
                IsFavorite = song.IsFavorite, SourceLabel = song.SourceLabel, CoverId = song.CoverId,
                LibraryIds = song.LibraryIds, Artists = song.Artists
            };
            var state = new PlaybackResumeState(track, document.PositionSeconds);
            Validate(state);
            ct.ThrowIfCancellationRequested();
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ct.ThrowIfCancellationRequested();
            // A corrupt/unreadable file is retained for diagnosis; loading never replaces it.
            return null;
        }
    }

    public async Task SaveAsync(PlaybackResumeState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        byte[] bytes = Encode(state);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string pending = PendingPath();
        try
        {
            await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(pending, file, true);
        }
        finally { DeletePending(pending); }
    }

    public void Save(PlaybackResumeState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        byte[] bytes = Encode(state);
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string pending = PendingPath();
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(pending, file, true);
        }
        finally { DeletePending(pending); }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        Clear(ct);
        return Task.CompletedTask;
    }

    public void Clear(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(file);
    }

    private string PendingPath() => file + "." + Guid.NewGuid().ToString("N") + ".tmp";
    private static void DeletePending(string pending)
    {
        try { File.Delete(pending); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] Encode(PlaybackResumeState state)
    {
        if (state?.Track is not { } original) throw Invalid();
        // Display tags can legitimately be longer than the resume snapshot (notably FN tags).
        // Keep source/track/related identities exact, and bound only presentation text.
        var track = original with
        {
            Title = Clip(original.Title), Artist = Clip(original.Artist), Album = Clip(original.Album),
            SourceLabel = Clip(original.SourceLabel),
            CoverId = string.IsNullOrWhiteSpace(original.CoverId) ? null : original.CoverId,
            LibraryIds = original.LibraryIds?.Take(101).ToArray()!,
            Artists = original.Artists?.Take(101).Select(artist => artist is null ? null!
                : artist with { Name = Clip(artist.Name) }).ToArray()!
        };
        Validate(state with { Track = track });
        var song = new Song(track.SourceInstanceId, track.Id, track.Title, track.Artist, track.Album,
            track.DurationSeconds, track.IsCue, track.IsFavorite, track.SourceLabel, track.CoverId,
            track.AlbumId, track.LibraryIds.ToArray(), track.Artists.ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, song, state.PositionSeconds));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Playback resume record is too large.");
        return bytes;
    }

    private static void Validate(PlaybackResumeState state)
    {
        if (state is null || state.Track is null) throw Invalid();
        var track = state.Track;
        if (track.SourceInstanceId == Guid.Empty || !Identifier(track.Id, 4096) ||
            !Text(track.Title) || !Text(track.Artist) || !Text(track.Album) || !Text(track.SourceLabel) ||
            !double.IsFinite(track.DurationSeconds) || track.DurationSeconds is < 0 or > 864000 ||
            !double.IsFinite(state.PositionSeconds) || state.PositionSeconds < 0 || state.PositionSeconds > track.DurationSeconds ||
            (track.CoverId is not null && !Identifier(track.CoverId, 4096)) ||
            (track.AlbumId is not null && !Identifier(track.AlbumId, 256)) ||
            track.LibraryIds is null || track.LibraryIds.Count > 100 ||
            track.LibraryIds.Any(id => !Identifier(id, 4096)) ||
            track.LibraryIds.Distinct(StringComparer.Ordinal).Count() != track.LibraryIds.Count ||
            track.Artists is null || track.Artists.Count > 100 ||
            track.Artists.Any(artist => artist is null || !Identifier(artist.Id, 256) || !Text(artist.Name)) ||
            track.Artists.Select(artist => artist.Id).Distinct(StringComparer.Ordinal).Count() != track.Artists.Count)
            throw Invalid();
    }

    private static bool Text(string? text) => text is not null && text.Length <= 4096;
    private static string Clip(string? text)
    {
        if (text is null || text.Length <= 4096) return text!;
        int length = char.IsHighSurrogate(text[4095]) && char.IsLowSurrogate(text[4096]) ? 4095 : 4096;
        return text[..length];
    }
    private static bool Identifier(string? id, int maximum) => !string.IsNullOrWhiteSpace(id) && id.Length <= maximum && !id.Any(char.IsControl);
    private static InvalidDataException Invalid() => new("Invalid playback resume record.");
}
