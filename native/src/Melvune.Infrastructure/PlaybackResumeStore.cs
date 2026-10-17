using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Melvune.Core;

namespace Melvune.Infrastructure;

public sealed record PlaybackResumeState(MusicTrack Track, double PositionSeconds);
public sealed record PlaybackSessionState(PlaybackQueueSnapshot Queue, double PositionSeconds, string Origin);

/// <summary>Stores source-qualified song/session snapshots, never stream URLs or local file paths.</summary>
public sealed class PlaybackResumeStore(string directory)
{
    private const int MaximumBytes = 256 * 1024;
    private const int MaximumSessionBytes = 16 * 1024 * 1024;
    private const int MaximumTracks = 10000;
    private static readonly TimeSpan WriterWait = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions SessionOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private readonly string file = Path.Combine(directory, "playback-resume.json");
    // An explicit DTO omits computed MusicTrack properties and leaves no place for connection secrets.
    private sealed record Song(Guid SourceInstanceId, string Id, string Title, string Artist, string Album,
        double DurationSeconds, bool IsCue, bool IsFavorite, string SourceLabel, string? CoverId,
        string? AlbumId, string[] LibraryIds, TrackArtist[] Artists);
    private sealed record Document(int Version, Song Track, double PositionSeconds);
    private sealed record Reference(Guid SourceInstanceId, string TrackId);
    private sealed record QueueDocument(Song[] Tracks, Reference? Current, QueueMode Mode,
        Reference[] History, Reference[] RequestedNext);
    private sealed record SessionDocument(int Version, Song? Track, double PositionSeconds, QueueDocument Queue, string Origin);
#if DEBUG
    // Diagnostic seam tests cancellation immediately before and after the atomic replacement.
    private Action<bool>? commitBoundary = null;
#endif

    public async Task<PlaybackResumeState?> LoadAsync(CancellationToken ct = default)
    {
        var loaded = await ReadAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return loaded.Legacy ?? (loaded.Session?.Queue.Current is { } current
            ? new PlaybackResumeState(loaded.Session.Queue.Tracks.Single(track => track.Reference == current), loaded.Session.PositionSeconds)
            : null);
    }

    public async Task<PlaybackSessionState?> LoadSessionAsync(CancellationToken ct = default)
    {
        var loaded = await ReadAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (loaded.Session is not null) return loaded.Session;
        if (loaded.Legacy is not { } legacy) return null;
        var state = new PlaybackSessionState(new([legacy.Track], legacy.Track.Reference, QueueMode.Sequential, [], []),
            legacy.PositionSeconds, "上次播放歌曲");
        try { ValidateSession(state); return state; }
        catch (InvalidDataException) { return null; }
    }

    private async Task<(PlaybackResumeState? Legacy, PlaybackSessionState? Session)> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Missing loads remain read-only, without creating the profile or lock.
            if (!File.Exists(file)) return default;
            using var body = new MemoryStream();
            // Windows can reject replacing a file that still has a reader's handle.
            // Serialize only the bounded file read with writers, dispose the input
            // before releasing the lock, and do all JSON work after releasing it.
            using (var reader = await LockAsync(ct).ConfigureAwait(false))
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumSessionBytes) { ct.ThrowIfCancellationRequested(); return default; }
                byte[] buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (body.Length + read > MaximumSessionBytes) { ct.ThrowIfCancellationRequested(); return default; }
                    body.Write(buffer, 0, read);
                }
            }
            ct.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number)) return default;
            if (number == 1)
            {
                if (body.Length > MaximumBytes) return default;
                var document = json.RootElement.Deserialize<Document>();
                if (document?.Track is null) return default;
                var state = new PlaybackResumeState(ToTrack(document.Track), document.PositionSeconds);
                Validate(state);
                ct.ThrowIfCancellationRequested();
                return (state, null);
            }
            if (number != 2) return default;
            ValidateSessionShape(json.RootElement);
            var session = json.RootElement.Deserialize<SessionDocument>(SessionOptions);
            if (session?.Queue is null || session.Queue.Tracks is null || session.Queue.History is null || session.Queue.RequestedNext is null)
                return default;
            var queue = session.Queue;
            var result = new PlaybackSessionState(new(queue.Tracks.Select(ToTrack).ToArray(), ToReference(queue.Current), queue.Mode,
                queue.History.Select(reference => ToReference(reference)!).ToArray(),
                queue.RequestedNext.Select(reference => ToReference(reference)!).ToArray()), session.PositionSeconds, session.Origin);
            ValidateSession(result);
            var current = queue.Current is null ? null : queue.Tracks.Single(song => song.SourceInstanceId == queue.Current.SourceInstanceId && song.Id == queue.Current.TrackId);
            if ((current is null) != (session.Track is null) || (current is not null &&
                !JsonSerializer.SerializeToUtf8Bytes(current).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(session.Track))))
                return default;
            ct.ThrowIfCancellationRequested();
            return (null, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ct.ThrowIfCancellationRequested();
            // Corrupt/unreadable input is retained for diagnosis; loading never replaces it.
            return default;
        }
    }

    public async Task SaveSessionAsync(PlaybackSessionState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var writer = await LockAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await WriteAsync(EncodeSession(state), ct).ConfigureAwait(false);
    }

    public void SaveSession(PlaybackSessionState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var writer = Lock(ct);
        ct.ThrowIfCancellationRequested();
        Write(EncodeSession(state), ct);
    }

    public async Task SaveAsync(PlaybackResumeState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var writer = await LockAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await WriteAsync(Encode(state), ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
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
#if DEBUG
            commitBoundary?.Invoke(false);
#endif
            ct.ThrowIfCancellationRequested();
            File.Move(pending, file, true);
#if DEBUG
            commitBoundary?.Invoke(true);
#endif
        }
        finally { DeletePending(pending); }
    }

    public void Save(PlaybackResumeState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var writer = Lock(ct);
        ct.ThrowIfCancellationRequested();
        Write(Encode(state), ct);
    }

    private void Write(byte[] bytes, CancellationToken ct)
    {
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
#if DEBUG
            commitBoundary?.Invoke(false);
#endif
            ct.ThrowIfCancellationRequested();
            File.Move(pending, file, true);
#if DEBUG
            commitBoundary?.Invoke(true);
#endif
        }
        finally { DeletePending(pending); }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var writer = await LockAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Delete(file);
    }

    public void Clear(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var writer = Lock(ct);
        ct.ThrowIfCancellationRequested();
        File.Delete(file);
    }

    // The OS lock serializes separate store instances and processes, including Clear
    // and the short handle-owning portion of Load.
    // Acquire before encoding/creating the pending file and release only after the
    // atomic commit. Clear waits for an already-writing save and removes its result.
    private FileStream OpenWriterLock() => new(file + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None);

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(WriterWait);
        try
        {
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                try { return OpenWriterLock(); }
                catch (IOException) { await Task.Delay(20, wait.Token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new IOException("Playback resume record is busy.", ex); }
    }

    private FileStream Lock(CancellationToken ct)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (wait.Elapsed >= WriterWait) throw new IOException("Playback resume record is busy.");
            try { return OpenWriterLock(); }
            catch (IOException)
            {
                // WaitHandle also wakes immediately when the caller cancels.
                if (ct.WaitHandle.WaitOne(20)) ct.ThrowIfCancellationRequested();
            }
        }
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
        var track = NormalizeTrack(original);
        Validate(state with { Track = track });
        var song = ToSong(track);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, song, state.PositionSeconds));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Playback resume record is too large.");
        return bytes;
    }

    private static byte[] EncodeSession(PlaybackSessionState state)
    {
        if (state?.Queue?.Tracks is null || state.Queue.Tracks.Count > MaximumTracks ||
            state.Queue.History is null || state.Queue.History.Count > MaximumTracks ||
            state.Queue.RequestedNext is null || state.Queue.RequestedNext.Count > MaximumTracks) throw Invalid();
        // Enforce the document budget while normalizing, before retaining a whole
        // queue of clipped tag strings. A single song has bounded fields; its temporary
        // encoding is released at each iteration. The current song is emitted twice
        // (root compatibility projection and queue), so count both copies here.
        var tracks = new List<MusicTrack>(state.Queue.Tracks.Count);
        var songDocuments = new List<Song>(state.Queue.Tracks.Count);
        long songBytes = 0;
        foreach (var original in state.Queue.Tracks)
        {
            if (tracks.Count >= MaximumTracks) throw Invalid();
            var track = NormalizeTrack(original);
            if (track.IsCue) throw Invalid();
            Validate(new(track, 0));
            var song = ToSong(track);
            int bytes = JsonSerializer.SerializeToUtf8Bytes(song, SessionOptions).Length;
            songBytes += state.Queue.Current == track.Reference ? 2L * bytes : bytes;
            if (songBytes > MaximumSessionBytes)
                throw new InvalidDataException("Playback session record is too large.");
            tracks.Add(track);
            songDocuments.Add(song);
        }
        var normalized = state with
        {
            Queue = state.Queue with { Tracks = tracks.ToArray() },
            Origin = Clip(state.Origin, 512)
        };
        ValidateSession(normalized);
        var queue = normalized.Queue;
        var songs = songDocuments.ToArray();
        var current = queue.Current is null ? null : songs.Single(song => song.SourceInstanceId == queue.Current.SourceInstanceId && song.Id == queue.Current.TrackId);
        var document = new SessionDocument(2, current, normalized.PositionSeconds,
            new(songs, FromReference(queue.Current), queue.Mode,
                queue.History.Select(reference => FromReference(reference)!).ToArray(),
                queue.RequestedNext.Select(reference => FromReference(reference)!).ToArray()), normalized.Origin);
        using var output = new LimitedBuffer(MaximumSessionBytes);
        JsonSerializer.Serialize(output, document, SessionOptions);
        return output.ToArray();
    }

    private static void ValidateSession(PlaybackSessionState state)
    {
        if (state is null || state.Queue is null || state.Queue.Tracks is null || state.Queue.Tracks.Count > MaximumTracks ||
            state.Queue.History is null || state.Queue.History.Count > MaximumTracks || state.Queue.RequestedNext is null ||
            state.Queue.RequestedNext.Count > MaximumTracks || state.Origin is null || state.Origin.Length > 512 ||
            !double.IsFinite(state.PositionSeconds) || state.PositionSeconds < 0) throw Invalid();
        foreach (var track in state.Queue.Tracks)
        {
            if (track is null || track.IsCue) throw Invalid();
            Validate(new(track, 0));
        }
        var queue = new PlaybackQueue();
        if (!queue.Restore(state.Queue)) throw Invalid();
        if (queue.Current is { } current) Validate(new(current, state.PositionSeconds));
        else if (state.PositionSeconds != 0) throw Invalid();
    }

    private static MusicTrack NormalizeTrack(MusicTrack original)
    {
        if (original is null) throw Invalid();
        return original with
        {
            Title = Clip(original.Title), Artist = Clip(original.Artist), Album = Clip(original.Album),
            SourceLabel = Clip(original.SourceLabel),
            CoverId = string.IsNullOrWhiteSpace(original.CoverId) ? null : original.CoverId,
            LibraryIds = original.LibraryIds?.Take(101).ToArray()!,
            Artists = original.Artists?.Take(101).Select(artist => artist is null ? null!
                : artist with { Name = Clip(artist.Name) }).ToArray()!
        };
    }
    private static Song ToSong(MusicTrack track) => new(track.SourceInstanceId, track.Id, track.Title, track.Artist, track.Album,
        track.DurationSeconds, track.IsCue, track.IsFavorite, track.SourceLabel, track.CoverId,
        track.AlbumId, track.LibraryIds.ToArray(), track.Artists.ToArray());
    private static MusicTrack ToTrack(Song song)
    {
        if (song is null) throw Invalid();
        return new(song.Id, song.Title, song.Artist, song.DurationSeconds, song.IsCue)
        {
            SourceInstanceId = song.SourceInstanceId, Album = song.Album, AlbumId = song.AlbumId,
            IsFavorite = song.IsFavorite, SourceLabel = song.SourceLabel, CoverId = song.CoverId,
            LibraryIds = song.LibraryIds, Artists = song.Artists
        };
    }
    private static TrackReference? ToReference(Reference? reference) => reference is null ? null : new(reference.SourceInstanceId, reference.TrackId);
    private static Reference? FromReference(TrackReference? reference) => reference is null ? null : new(reference.SourceInstanceId, reference.TrackId);

    private static void ValidateSessionShape(JsonElement root)
    {
        Fields(root, "Version", "Track", "PositionSeconds", "Queue", "Origin");
        var queue = root.GetProperty("Queue");
        Fields(queue, "Tracks", "Current", "Mode", "History", "RequestedNext");
        SongShape(root.GetProperty("Track"), optional: true);
        foreach (var track in Array(queue.GetProperty("Tracks"))) SongShape(track, optional: false);
        ReferenceShape(queue.GetProperty("Current"), optional: true);
        foreach (var reference in Array(queue.GetProperty("History"))) ReferenceShape(reference, optional: false);
        foreach (var reference in Array(queue.GetProperty("RequestedNext"))) ReferenceShape(reference, optional: false);
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumTracks) throw Invalid();
        return value.EnumerateArray();
    }
    private static void ReferenceShape(JsonElement value, bool optional)
    {
        if (optional && value.ValueKind == JsonValueKind.Null) return;
        Fields(value, "SourceInstanceId", "TrackId");
    }
    private static void SongShape(JsonElement value, bool optional)
    {
        if (optional && value.ValueKind == JsonValueKind.Null) return;
        Fields(value, "SourceInstanceId", "Id", "Title", "Artist", "Album", "DurationSeconds", "IsCue", "IsFavorite",
            "SourceLabel", "CoverId", "AlbumId", "LibraryIds", "Artists");
        var artists = value.GetProperty("Artists");
        if (artists.ValueKind != JsonValueKind.Array || artists.GetArrayLength() > 100) throw Invalid();
        foreach (var artist in artists.EnumerateArray()) Fields(artist, "Id", "Name");
    }
    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !expected.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        if (names.Count != expected.Length) throw Invalid();
    }
    private sealed class LimitedBuffer(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { Check(1); base.WriteByte(value); }
        private void Check(int count)
        { if (Length + count > maximum) throw new InvalidDataException("Playback session record is too large."); }
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
    private static string Clip(string? text, int maximum = 4096)
    {
        if (text is null || text.Length <= maximum) return text!;
        int length = char.IsHighSurrogate(text[maximum - 1]) && char.IsLowSurrogate(text[maximum]) ? maximum - 1 : maximum;
        return text[..length];
    }
    private static bool Identifier(string? id, int maximum) => !string.IsNullOrWhiteSpace(id) && id.Length <= maximum && !id.Any(char.IsControl);
    private static InvalidDataException Invalid() => new("Invalid playback resume record.");
}
