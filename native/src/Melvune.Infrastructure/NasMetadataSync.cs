using Melvune.Core;

namespace Melvune.Infrastructure;

public sealed record MetadataSyncProgress(string Stage, int Completed, int Total);
public sealed record MetadataSyncResult(NasMetadataSnapshot Snapshot, int FailedAssets);

public static class NasMetadataSync
{
    public static async Task<MetadataSyncResult> RunAsync(NasApiClient api, NasMetadataCache cache,
        CancellationToken ct, IProgress<MetadataSyncProgress>? progress = null, Action<NasMetadataSnapshot>? catalogReady = null)
    {
        progress?.Report(new("歌曲", 0, 0));
        var tracks = await TrackCatalogReader.ReadAsync((page, token) => api.ListTracksAsync(page, 50, token), ct);
        var favorites = await TrackCatalogReader.ReadAsync((page, token) => api.ListFavoritesAsync(page, 50, token), ct);
        var favoriteRefs = favorites.Select(track => track.Reference).ToHashSet();
        tracks = tracks.Select(track => track with { IsFavorite = favoriteRefs.Contains(track.Reference) }).ToArray();
        long references = tracks.Count + favorites.Count;
        void AddReferences(int count) { references += count; if (references > 1000000) throw new MusicApiException(MusicFailure.Rejected); }
        async Task<IReadOnlyList<MusicCollection>> ReadCollectionList(Func<int, Task<CollectionPage>> read)
        {
            var all = new List<MusicCollection>(); int expected = -1;
            for (int page = 1; ; page++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await read(page);
                if (result.Total > 100000 || (expected >= 0 && expected != result.Total)
                    || result.Items.Count != Math.Min(100, Math.Max(0, result.Total - all.Count))) throw new MusicApiException(MusicFailure.InvalidResponse);
                expected = result.Total; all.AddRange(result.Items);
                if (all.Count == expected) break;
            }
            if (all.Select(item => item.Id).Distinct().Count() != all.Count) throw new MusicApiException(MusicFailure.InvalidResponse);
            return all;
        }
        async Task<IReadOnlyList<CachedCollection>> Collections(CollectionKind kind)
        {
            var all = await ReadCollectionList(page => api.ListCollectionsAsync(kind, page, 100, ct));
            var snapshots = new List<CachedCollection>();
            foreach (var item in all)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(kind == CollectionKind.Album ? "专辑" : "歌手", snapshots.Count, all.Count));
                var detail = await api.GetCollectionAsync(kind, item.Id, ct);
                var members = await TrackCatalogReader.ReadAsync((page, token) => api.ListCollectionTracksAsync(kind, item.Id, page, 50, token), ct);
                AddReferences(members.Count);
                var relatedAlbums = kind == CollectionKind.Artist
                    ? await ReadCollectionList(page => api.ListArtistAlbumsAsync(item.Id, page, 100, ct)) : [];
                AddReferences(relatedAlbums.Count);
                snapshots.Add(new(detail, members.Select(track => track with { IsFavorite = favoriteRefs.Contains(track.Reference) }).ToArray()) { Albums = relatedAlbums });
            }
            return snapshots;
        }
        var albums = await Collections(CollectionKind.Album);
        var artists = await Collections(CollectionKind.Artist);
        var playlistList = await api.ListPlaylistsAsync(ct);
        if (playlistList.Count > 10000) throw new MusicApiException(MusicFailure.Rejected);
        var playlists = new List<NasPlaylistSnapshot>();
        foreach (var playlist in playlistList)
        {
            ct.ThrowIfCancellationRequested(); progress?.Report(new("歌单", playlists.Count, playlistList.Count));
            var playlistSnapshot = await api.ReadPlaylistSnapshotAsync(playlist.Reference, ct);
            AddReferences(playlistSnapshot.Tracks.Count);
            playlists.Add(playlistSnapshot with { Tracks = playlistSnapshot.Tracks.Select(track => track with { IsFavorite = favoriteRefs.Contains(track.Reference) }).ToArray() });
        }
        var snapshot = new NasMetadataSnapshot(api.SourceInstanceId, DateTimeOffset.UtcNow, tracks, albums, artists, playlists) { Favorites = favorites };
        await cache.SaveAsync(snapshot, ct);
        ct.ThrowIfCancellationRequested(); catalogReady?.Invoke(snapshot);
        var allTracks = tracks.Concat(favorites).Concat(albums.SelectMany(item => item.Tracks)).Concat(artists.SelectMany(item => item.Tracks))
            .Concat(playlists.SelectMany(item => item.Tracks)).DistinctBy(item => item.Reference).ToArray();
        var covers = allTracks.Select(item => item.CoverId).Concat(albums.Select(item => item.Detail.CoverId))
            .Concat(artists.Select(item => item.Detail.CoverId)).Concat(playlists.Select(item => item.Playlist.CoverId))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        int completed = 0, failed = 0, total = allTracks.Length + covers.Length;
        // Asset failures do not erase a complete catalog or the previous usable asset. Authentication failure stops the cycle.
        async Task Asset(Func<Task> read)
        {
            try { ct.ThrowIfCancellationRequested(); await read(); }
            catch (OperationCanceledException) { throw; }
            catch (MusicApiException error) when (error.Failure == MusicFailure.Unauthorized) { throw; }
            catch (Exception ex) when (ex is MusicApiException or IOException or UnauthorizedAccessException or ArgumentException) { Interlocked.Increment(ref failed); }
            progress?.Report(new("封面与歌词", Interlocked.Increment(ref completed), total));
        }
        await Parallel.ForEachAsync(allTracks, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (track, token) => await Asset(async () => await cache.SaveLyricsAsync(track.Reference, await api.GetLyricsAsync(track.Reference, token), token)));
        await Parallel.ForEachAsync(covers, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (id, token) => await Asset(async () =>
            {
                var bytes = await api.GetCoverAsync(id!, NasCoverSize.Large, token);
                if (bytes is { Length: > 0 }) await cache.SaveCoverAsync(api.SourceInstanceId, id!, NasCoverSize.Large, bytes, token);
                else
                {
                    token.ThrowIfCancellationRequested();
                    cache.RemoveInvalidCover(api.SourceInstanceId, id!, NasCoverSize.Large);
                    cache.RemoveInvalidCover(api.SourceInstanceId, id!, NasCoverSize.Medium);
                }
            }));
        ct.ThrowIfCancellationRequested(); return new(snapshot, failed);
    }
}
