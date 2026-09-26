namespace FnMusic.Core;

/// <summary>Local references survive disconnection. Display data is a snapshot, not credentials.</summary>
public sealed record LocalPlaylistEntry(Guid Id, TrackReference Track, string Title, string Artist, string Album,
    double DurationSeconds, bool IsCue, string? LibraryId = null, string? CoverId = null)
{
    public static LocalPlaylistEntry FromTrack(MusicTrack track, string? libraryId = null) =>
        new(Guid.NewGuid(), track.Reference, track.Title, track.Artist, track.Album, track.DurationSeconds, track.IsCue, libraryId, track.CoverId);
    public MusicTrack ToTrack() => new(Track.TrackId, Title, Artist, DurationSeconds, IsCue)
        { SourceInstanceId = Track.SourceInstanceId, Album = Album, CoverId = CoverId };
}

public sealed record LocalPlaylist(Guid Id, string Name, IReadOnlyList<LocalPlaylistEntry> Entries);
public sealed record LocalPlaylistCatalog(long Revision, IReadOnlyList<LocalPlaylist> Playlists)
{
    public static LocalPlaylistCatalog Empty => new(0, []);
}

public sealed class PlaylistConflictException() : IOException("Playlists changed. Reload before saving.");
