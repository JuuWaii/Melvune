#if DEBUG
using System.Text.Json;
using FnMusic.App.ViewModels;
using FnMusic.Core;

namespace FnMusic.App.Services;
// Explicit write probe: only a uniquely named playlist created by this invocation is mutated or removed.
internal static class PlaylistWriteVerification
{
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>();
        string stage = "session", error = "";
        string fixtureName = "FN验证-" + Guid.NewGuid().ToString("N")[..16];
        string renamedName = fixtureName + "-改";
        PlaylistReference? owned = null;
        var before = new HashSet<PlaylistReference>(); var allowedTracks = new HashSet<TrackReference>();
        bool cleaned = false;
        using var connection = new ConnectionViewModel();
        using var observer = new ConnectionViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await connection.InitializeAsync(); await observer.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            var other = observer.AuthenticatedClient ?? throw new InvalidOperationException();
            before = (await api.ListPlaylistsAsync(timeout.Token)).Select(item => item.Reference).ToHashSet();
            var samples = (await api.ListTracksAsync(1, 2, timeout.Token)).Tracks;
            allowedTracks = samples.Select(track => track.Reference).ToHashSet();
            checks[stage] = true;
            stage = "create_and_readback";
            var created = await api.CreatePlaylistAsync(fixtureName, timeout.Token);
            if (before.Contains(created.Reference)) throw new InvalidOperationException();
            owned = created.Reference;
            checks[stage] = created.Name == fixtureName;
            var snapshot = await api.ReadPlaylistSnapshotAsync(owned, timeout.Token);
            if (samples.Count > 0)
            {
                stage = "add_tracks_readback";
                snapshot = await api.ChangePlaylistTracksAsync(snapshot, samples.Select(track => track.Reference).ToArray(), true, timeout.Token);
                checks[stage] = snapshot.Tracks.Count == samples.Count;
            }
            stage = "second_client_observes_created_playlist";
            checks[stage] = (await other.ListPlaylistsAsync(timeout.Token)).Any(item => item.Reference == owned && item.Name == fixtureName);
            stage = "second_client_rename";
            var changed = await other.RenamePlaylistAsync(snapshot.Playlist, renamedName, timeout.Token);
            checks[stage] = changed.Name == renamedName;
            stage = "stale_client_rejected_before_write";
            try { await api.RenamePlaylistAsync(snapshot.Playlist, fixtureName, timeout.Token); throw new InvalidOperationException(); }
            catch (RemotePlaylistConflictException) { checks[stage] = true; }
            snapshot = await api.ReadPlaylistSnapshotAsync(owned, timeout.Token);
            stage = "refresh_observes_remote_rename"; checks[stage] = snapshot.Playlist.Name == renamedName;
            if (snapshot.Tracks.Count > 0)
            {
                stage = "remove_track_readback";
                int previousCount = snapshot.Tracks.Count;
                snapshot = await api.ChangePlaylistTracksAsync(snapshot, [snapshot.Tracks[0].Reference], false, timeout.Token);
                checks[stage] = snapshot.Tracks.Count == previousCount - 1;
            }
            stage = checks.Values.All(value => value) ? "complete" : "contract_mismatch";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            if (owned is not null && !before.Contains(owned) && connection.AuthenticatedClient is { } api)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var current = await api.ReadPlaylistSnapshotAsync(owned, cleanup.Token);
                    if ((current.Playlist.Name == fixtureName || current.Playlist.Name == renamedName) && current.Tracks.All(track => allowedTracks.Contains(track.Reference)))
                    { await api.DeletePlaylistAsync(current, cleanup.Token); cleaned = true; }
                }
                catch (Exception) { }
            }
            checks["owned_fixture_removed"] = cleaned;
            if (!cleaned && owned is not null) stage = "cleanup_incomplete";
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "playlist-write-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks }));
    }
}
#endif
