namespace FnMusic.Core;

public sealed record PlaylistReference(Guid SourceInstanceId, string PlaylistId);
public sealed record NasPlaylist(PlaylistReference Reference, string Name, string? CoverId, long? UpdatedAt);
public sealed record NasPlaylistSnapshot(NasPlaylist Playlist, IReadOnlyList<MusicTrack> Tracks);
public sealed class RemotePlaylistConflictException() : IOException("Remote playlist changed. Refresh before retrying.");
