using FnMusic.Core;

namespace FnMusic.Infrastructure;

/// <summary>Builds a complete metadata snapshot; an inconsistent page never becomes a partial catalog.</summary>
public static class TrackCatalogReader
{
    public const int PageSize = 50;
    public const int MaximumTracks = 100000;

    public static async Task<IReadOnlyList<MusicTrack>> ReadAsync(
        Func<int, CancellationToken, Task<TrackPage>> readPage, CancellationToken ct,
        IProgress<int>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        var first = await readPage(1, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (first.Total is < 0 or > MaximumTracks) throw new MusicApiException(MusicFailure.InvalidResponse);
        var rows = new List<MusicTrack>(first.Total);
        var identities = new HashSet<TrackReference>();
        void Append(TrackPage result)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Total != first.Total || result.Tracks.Count != Math.Min(PageSize, first.Total - rows.Count))
                throw new MusicApiException(MusicFailure.InvalidResponse);
            foreach (var track in result.Tracks)
            {
                if (string.IsNullOrEmpty(track.Id) || !identities.Add(track.Reference))
                    throw new MusicApiException(MusicFailure.InvalidResponse);
                rows.Add(track);
            }
            progress?.Report(rows.Count);
        }
        Append(first);
        for (int index = 2; rows.Count < first.Total; index++)
        {
            ct.ThrowIfCancellationRequested();
            Append(await readPage(index, ct).ConfigureAwait(false));
        }
        ct.ThrowIfCancellationRequested();
        return rows.AsReadOnly();
    }
}
