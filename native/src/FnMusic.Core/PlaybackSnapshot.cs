namespace FnMusic.Core;

public enum PlaybackStatus { Stopped, Loading, Playing, Paused, Buffering }
public enum PlaybackLyricsKind { Loading, Timed, Plain, Empty, Unavailable }
public enum PlaybackCommandKind { Play, Pause, Toggle, Stop, Previous, Next, Seek }
public sealed record PlaybackCommand(PlaybackCommandKind Kind, Guid? TrackGeneration = null, double? PositionSeconds = null);
public sealed record PlaybackSnapshot(
    int ProtocolVersion, Guid InstanceId, long Sequence, Guid? TrackGeneration,
    DateTimeOffset Timestamp, PlaybackStatus State, double PositionSeconds, double DurationSeconds,
    string Title, string Artist, string Album, PlaybackLyricsKind LyricsKind,
    int CurrentLineIndex, string CurrentLine, bool CanPlay, bool CanPause, bool CanPrevious,
    bool CanNext, bool CanSeek);
public sealed record PlaybackLyricsSnapshot(Guid? TrackGeneration, PlaybackLyricsKind Kind,
    IReadOnlyList<LyricLine> Lines, bool IsLimited);

/// <summary>Immutable public data only. Never carries source IDs, paths, URLs or credentials.</summary>
public sealed class PlaybackSnapshotHub
{
    public const int ProtocolVersion = 1;
    public const int MaximumLyricsLines = 2000;
    public const int MaximumTextLength = 2048;
    public Guid InstanceId { get; } = Guid.NewGuid();
    private PlaybackSnapshot current;
    private PlaybackLyricsSnapshot lyrics = new(null, PlaybackLyricsKind.Empty, Array.Empty<LyricLine>(), false);
    public PlaybackSnapshot Current => Volatile.Read(ref current);
    public PlaybackLyricsSnapshot Lyrics => Volatile.Read(ref lyrics);
    public event Action<PlaybackSnapshot>? Changed;
    public PlaybackSnapshotHub() => current = new(ProtocolVersion, InstanceId, 0, null, DateTimeOffset.UtcNow,
        PlaybackStatus.Stopped, 0, 0, "", "", "", PlaybackLyricsKind.Empty, -1, "", false, false, false, false, false);
    public static string BoundText(string? value) => string.IsNullOrEmpty(value) ? "" : value[..Math.Min(value.Length, MaximumTextLength)];
    public void SetLyrics(Guid? generation, PlaybackLyricsKind kind, Lyrics value)
    {
        var lines = value.Lines.Take(MaximumLyricsLines).Select(line => new LyricLine(
            double.IsFinite(line.Seconds) ? Math.Max(0, line.Seconds) : 0, BoundText(line.Text))).ToArray();
        Volatile.Write(ref lyrics, new(generation, kind, Array.AsReadOnly(lines), value.Lines.Count > lines.Length
            || value.Lines.Any(line => line.Text.Length > MaximumTextLength)));
    }
    public void Publish(PlaybackSnapshot value)
    {
        var next = value with { ProtocolVersion = ProtocolVersion, InstanceId = InstanceId,
            Sequence = Current.Sequence + 1, Timestamp = DateTimeOffset.UtcNow,
            Title = BoundText(value.Title), Artist = BoundText(value.Artist), Album = BoundText(value.Album),
            CurrentLine = BoundText(value.CurrentLine),
            PositionSeconds = double.IsFinite(value.PositionSeconds) ? Math.Max(0, value.PositionSeconds) : 0,
            DurationSeconds = double.IsFinite(value.DurationSeconds) ? Math.Max(0, value.DurationSeconds) : 0 };
        Volatile.Write(ref current, next);
        Changed?.Invoke(next);
    }
}
