namespace FnMusic.Core;

/// <summary>A complete queue state. History is ordered most recent first; explicit next requests play first.</summary>
public sealed record PlaybackQueueSnapshot(
    IReadOnlyList<MusicTrack> Tracks,
    TrackReference? Current,
    QueueMode Mode,
    IReadOnlyList<TrackReference> History,
    IReadOnlyList<TrackReference> RequestedNext);
