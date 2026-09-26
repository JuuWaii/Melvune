#if DEBUG
using System.Text.Json;
using FnMusic.App.ViewModels;

namespace FnMusic.App.Services;
internal static class PlaylistVerification
{
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>(); var skipped = new List<string>();
        string stage = "session", error = "";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var connection = new ConnectionViewModel();
        try
        {
            await connection.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            checks[stage] = true;
            stage = "playlist_list";
            var playlists = await api.ListPlaylistsAsync(timeout.Token); checks[stage] = true;
            if (playlists.Count > 0)
            {
                stage = "playlist_detail_and_tracks";
                var first = await api.ReadPlaylistSnapshotAsync(playlists[0].Reference, timeout.Token);
                checks[stage] = first.Playlist.Reference == playlists[0].Reference;
                if (first.Tracks.Count > 1)
                {
                    stage = "playlist_track_pagination";
                    var one = await api.ListPlaylistTracksAsync(first.Playlist.Reference, 1, 1, timeout.Token);
                    var two = await api.ListPlaylistTracksAsync(first.Playlist.Reference, 2, 1, timeout.Token);
                    checks[stage] = one.Total == two.Total && one.Tracks.Count == 1 && two.Tracks.Count == 1 && one.Tracks[0].Reference != two.Tracks[0].Reference;
                }
                else skipped.Add("playlist_pagination_insufficient_tracks");
            }
            else skipped.Add("playlist_detail_empty_list");
            stage = checks.Values.All(value => value) ? skipped.Count == 0 ? "complete" : "complete_with_skips" : "contract_mismatch";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "playlist-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, skipped }));
    }
}
#endif
