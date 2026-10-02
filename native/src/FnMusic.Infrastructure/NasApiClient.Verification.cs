#if DEBUG
using System.Text.Json;
using FnMusic.Core;

namespace FnMusic.Infrastructure;
public sealed partial class NasApiClient
{
    // Opt-in read-only diagnostic data for checking actual indexed files.
    // Private NAS paths are never added to MusicTrack, settings or normal logs.
    public async Task<IReadOnlyList<(MusicTrack Track, string Path)>> ListTrackFilesForVerificationAsync(int page, int size, CancellationToken ct)
    {
        ValidatePage(page, size);
        using var data = await RequestAsync(HttpMethod.Get, $"track/list?page={page}&size={size}", null, ct);
        var tracks = ParseTrackPage(data.RootElement);
        if (tracks.Tracks.Count > size || tracks.Tracks.Count > tracks.Total) throw new MusicApiException(MusicFailure.InvalidResponse);
        var rows = data.RootElement.GetProperty("list").EnumerateArray().ToArray();
        var files = new List<(MusicTrack, string)>();
        for (int i = 0; i < rows.Length; i++)
        {
            string? path = TrackFilePath(rows[i]);
            if (path is null)
            {
                var reference = tracks.Tracks[i].Reference;
                ValidateReference(reference);
                using var metadata = await RequestAsync(HttpMethod.Get, "track/metadata?guid=" + EncodeCollectionId(reference.TrackId), null, ct);
                var root = metadata.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("track", out var track)
                    || RequiredSettingsString(track, "guid", 256) != reference.TrackId) throw new MusicApiException(MusicFailure.InvalidResponse);
                path = TrackFilePath(root) ?? throw new MusicApiException(MusicFailure.InvalidResponse);
            }
            files.Add((tracks.Tracks[i], path));
        }
        return files;
    }

    private static string? TrackFilePath(JsonElement item)
    {
        if (!item.TryGetProperty("audioSpec", out var spec) || spec.ValueKind == JsonValueKind.Null) return null;
        if (spec.ValueKind != JsonValueKind.Object) throw new MusicApiException(MusicFailure.InvalidResponse);
        if (!spec.TryGetProperty("path", out var path) || path.ValueKind == JsonValueKind.Null) return null;
        if (path.ValueKind != JsonValueKind.String || path.GetString()!.Length > 4096 || string.IsNullOrWhiteSpace(path.GetString()))
            throw new MusicApiException(MusicFailure.InvalidResponse);
        return path.GetString();
    }
}
#endif
