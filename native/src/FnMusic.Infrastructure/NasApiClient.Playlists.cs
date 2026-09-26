using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;
public sealed partial class NasApiClient
{
    private string ValidatePlaylistReference(PlaylistReference reference)
    {
        if (SourceInstanceId == Guid.Empty || reference.SourceInstanceId != SourceInstanceId) throw new MusicApiException(MusicFailure.Rejected);
        return EncodeCollectionId(reference.PlaylistId);
    }
    private static string PlaylistName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 32) throw new ArgumentException("Playlist names must contain 1 to 32 characters.");
        return name;
    }
    private NasPlaylist ParsePlaylist(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("guid", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 256 ||
            !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString()!.Length > 4096)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        string? cover = null; long? updated = null;
        if (item.TryGetProperty("coverId", out var coverElement) && coverElement.ValueKind != JsonValueKind.Null)
        {
            if (coverElement.ValueKind != JsonValueKind.String || coverElement.GetString()!.Length > 4096) throw new MusicApiException(MusicFailure.InvalidResponse);
            cover = coverElement.GetString();
        }
        if (item.TryGetProperty("updatedAt", out var time) && time.ValueKind != JsonValueKind.Null)
        {
            if (time.ValueKind != JsonValueKind.Number || !time.TryGetInt64(out long value) || value < 0) throw new MusicApiException(MusicFailure.InvalidResponse);
            updated = value;
        }
        return new(new(SourceInstanceId, id.GetString()!), name.GetString()!, cover, updated);
    }
    public async Task<IReadOnlyList<NasPlaylist>> ListPlaylistsAsync(CancellationToken ct)
    {
        using var data = await RequestAsync(HttpMethod.Get, "playlist/list", null, ct);
        var root = data.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new MusicApiException(MusicFailure.InvalidResponse);
        var playlists = list.EnumerateArray().Select(ParsePlaylist).ToArray();
        if (playlists.Select(item => item.Reference).Distinct().Count() != playlists.Length) throw new MusicApiException(MusicFailure.InvalidResponse);
        return playlists;
    }
    public async Task<NasPlaylist> GetPlaylistAsync(PlaylistReference reference, CancellationToken ct)
    {
        string id = ValidatePlaylistReference(reference);
        using var data = await RequestAsync(HttpMethod.Get, "playlist/detail?guid=" + id, null, ct);
        var result = ParsePlaylist(data.RootElement);
        if (result.Reference != reference) throw new MusicApiException(MusicFailure.InvalidResponse);
        return result;
    }
    public async Task<TrackPage> ListPlaylistTracksAsync(PlaylistReference reference, int page, int size, CancellationToken ct)
    {
        string id = ValidatePlaylistReference(reference); ValidatePage(page, size);
        using var data = await RequestAsync(HttpMethod.Get, $"track/playlist-detail/list?playlistGUID={id}&page={page}&size={size}&sort=trackAddedAt%2Casc", null, ct);
        var result = ParseTrackPage(data.RootElement);
        if (result.Tracks.Count > size || result.Tracks.Count > result.Total) throw new MusicApiException(MusicFailure.InvalidResponse);
        return result;
    }
    public async Task<NasPlaylistSnapshot> ReadPlaylistSnapshotAsync(PlaylistReference reference, CancellationToken ct)
    {
        var before = await GetPlaylistAsync(reference, ct);
        var tracks = new List<MusicTrack>(); int expected = -1;
        for (int page = 1; ; page++)
        {
            var result = await ListPlaylistTracksAsync(reference, page, 100, ct);
            if (result.Total > 10000) throw new MusicApiException(MusicFailure.Rejected);
            if (expected >= 0 && result.Total != expected) throw new RemotePlaylistConflictException();
            expected = result.Total; tracks.AddRange(result.Tracks);
            if (tracks.Count > expected || tracks.Select(track => track.Reference).Distinct().Count() != tracks.Count)
                throw new MusicApiException(MusicFailure.InvalidResponse);
            if (tracks.Count == expected) break;
            if (result.Tracks.Count != 100) throw new MusicApiException(MusicFailure.InvalidResponse);
        }
        if (await GetPlaylistAsync(reference, ct) != before) throw new RemotePlaylistConflictException();
        return new(before, tracks);
    }
    public async Task<NasPlaylist> CreatePlaylistAsync(string name, CancellationToken ct)
    {
        if (SourceInstanceId == Guid.Empty) throw new MusicApiException(MusicFailure.Rejected);
        name = PlaylistName(name);
        using var data = await RequestAsync(HttpMethod.Post, "playlist/create", new { name, coverId = (string?)null }, ct);
        var created = ParsePlaylist(data.RootElement);
        var confirmed = await GetPlaylistAsync(created.Reference, ct);
        if (confirmed.Name != name) throw new RemotePlaylistConflictException();
        return confirmed;
    }
    public async Task<NasPlaylist> RenamePlaylistAsync(NasPlaylist expected, string name, CancellationToken ct)
    {
        ValidatePlaylistReference(expected.Reference); name = PlaylistName(name);
        if (await GetPlaylistAsync(expected.Reference, ct) != expected) throw new RemotePlaylistConflictException();
        using var data = await RequestAsync(HttpMethod.Post, "playlist/edit", new { guid = expected.Reference.PlaylistId, name, coverId = expected.CoverId }, ct);
        var confirmed = await GetPlaylistAsync(expected.Reference, ct);
        if (confirmed.Name != name || confirmed.CoverId != expected.CoverId) throw new RemotePlaylistConflictException();
        return confirmed;
    }
    private static bool SamePlaylistSnapshot(NasPlaylistSnapshot expected, NasPlaylistSnapshot current) =>
        expected.Playlist == current.Playlist && expected.Tracks.Select(track => track.Reference).SequenceEqual(current.Tracks.Select(track => track.Reference));
    public async Task<NasPlaylistSnapshot> ChangePlaylistTracksAsync(NasPlaylistSnapshot expected, IReadOnlyList<TrackReference> tracks, bool add, CancellationToken ct)
    {
        ValidatePlaylistReference(expected.Playlist.Reference);
        if (tracks.Count is < 1 or > 100 || tracks.Distinct().Count() != tracks.Count) throw new ArgumentException("Invalid track selection.");
        foreach (var track in tracks) { ValidateReference(track); _ = EncodeCollectionId(track.TrackId); }
        var current = await ReadPlaylistSnapshotAsync(expected.Playlist.Reference, ct);
        if (!SamePlaylistSnapshot(expected, current)) throw new RemotePlaylistConflictException();
        var expectedRefs = current.Tracks.Select(track => track.Reference).ToHashSet();
        var changed = tracks.Where(track => add ? !expectedRefs.Contains(track) : expectedRefs.Contains(track)).ToArray();
        if (changed.Length == 0) return current;
        if (add && expectedRefs.Count + changed.Length > 10000) throw new MusicApiException(MusicFailure.Rejected);
        using var data = await RequestAsync(HttpMethod.Post, add ? "playlist/add-track" : "playlist/remove-track",
            new { guid = expected.Playlist.Reference.PlaylistId, trackGUIDs = changed.Select(track => track.TrackId).ToArray() }, ct);
        if (add) expectedRefs.UnionWith(changed); else expectedRefs.ExceptWith(changed);
        var confirmed = await ReadPlaylistSnapshotAsync(expected.Playlist.Reference, ct);
        if (!expectedRefs.SetEquals(confirmed.Tracks.Select(track => track.Reference))) throw new RemotePlaylistConflictException();
        return confirmed;
    }
    public async Task DeletePlaylistAsync(NasPlaylistSnapshot expected, CancellationToken ct)
    {
        ValidatePlaylistReference(expected.Playlist.Reference);
        if (!SamePlaylistSnapshot(expected, await ReadPlaylistSnapshotAsync(expected.Playlist.Reference, ct))) throw new RemotePlaylistConflictException();
        using var data = await RequestAsync(HttpMethod.Post, "playlist/delete", new { guid = expected.Playlist.Reference.PlaylistId }, ct);
        if ((await ListPlaylistsAsync(ct)).Any(item => item.Reference == expected.Playlist.Reference)) throw new RemotePlaylistConflictException();
    }
}
