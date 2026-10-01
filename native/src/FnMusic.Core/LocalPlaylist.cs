namespace FnMusic.Core;

/// <summary>Local references survive disconnection. Display data is a snapshot, not credentials.</summary>
public sealed record LocalPlaylistEntry(Guid Id, TrackReference Track, string Title, string Artist, string Album,
    double DurationSeconds, bool IsCue, string? LibraryId = null, string? CoverId = null, IReadOnlyList<string>? LibraryIds = null)
{
    public static LocalPlaylistEntry FromTrack(MusicTrack track, string? libraryId = null)
    {
        var libraries = libraryId is null ? track.LibraryIds.ToArray() : new[] { libraryId };
        return new(Guid.NewGuid(), track.Reference, track.Title, track.Artist, track.Album, track.DurationSeconds, track.IsCue,
            libraries.FirstOrDefault(), track.CoverId, libraries);
    }
    public IReadOnlyList<string> GetLibraryIds() => LibraryIds ?? (string.IsNullOrEmpty(LibraryId) ? [] : new[] { LibraryId });
    public MusicTrack ToTrack() => new(Track.TrackId, Title, Artist, DurationSeconds, IsCue)
        { SourceInstanceId = Track.SourceInstanceId, Album = Album, CoverId = CoverId, LibraryIds = GetLibraryIds().ToArray() };
    public bool Equals(LocalPlaylistEntry? other) => other is not null && Id == other.Id && Track == other.Track
        && Title == other.Title && Artist == other.Artist && Album == other.Album && DurationSeconds == other.DurationSeconds
        && IsCue == other.IsCue && LibraryId == other.LibraryId && CoverId == other.CoverId && GetLibraryIds().SequenceEqual(other.GetLibraryIds());
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id); hash.Add(Track); hash.Add(Title); hash.Add(Artist); hash.Add(Album); hash.Add(DurationSeconds);
        hash.Add(IsCue); hash.Add(LibraryId); hash.Add(CoverId);
        foreach (var library in GetLibraryIds()) hash.Add(library);
        return hash.ToHashCode();
    }
}

public sealed record LocalPlaylist(Guid Id, string Name, IReadOnlyList<LocalPlaylistEntry> Entries);
public sealed record LocalPlaylistCatalog(long Revision, IReadOnlyList<LocalPlaylist> Playlists)
{
    public static LocalPlaylistCatalog Empty => new(0, []);
}

public sealed class PlaylistConflictException() : IOException("Playlists changed. Reload before saving.");
