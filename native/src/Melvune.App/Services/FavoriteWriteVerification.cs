#if DEBUG
using System.Text.Json;
using Melvune.App.ViewModels;
using Melvune.Core;
using Melvune.Infrastructure;

namespace Melvune.App.Services;

// Explicit integration probe; only undo additions made by this invocation.
internal static class FavoriteWriteVerification
{
    public static async Task RunAsync()
    {
        var checks = new Dictionary<string, bool>();
        var attempted = new HashSet<TrackReference>();
        HashSet<TrackReference>? original = null;
        string stage = "session", error = "";
        using var connection = new ConnectionViewModel();
        using var observer = new ConnectionViewModel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await connection.InitializeAsync(); await observer.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            var other = observer.AuthenticatedClient ?? throw new InvalidOperationException();
            if (api.SourceInstanceId != other.SourceInstanceId) throw new InvalidOperationException();
            original = await ReadAllAsync(api, timeout.Token);
            checks[stage] = true;
            stage = "select_nonfavorite_samples";
            var samples = (await api.ListTracksAsync(1, 100, timeout.Token)).Tracks
                .Where(track => !track.IsFavorite && !original.Contains(track.Reference))
                .Select(track => track.Reference).Distinct().Take(2).ToArray();
            if (samples.Length != 2) throw new InvalidOperationException();
            checks[stage] = true;
            foreach (var reference in samples)
            {
                // Recheck immediately before adding; never remove a preexisting favorite.
                if ((await ReadAllAsync(api, timeout.Token)).Contains(reference)) throw new InvalidOperationException();
                attempted.Add(reference); // A timeout can follow a successful server write.
                await api.SetFavoriteAsync(reference, true, timeout.Token);
            }
            stage = "second_client_observes_additions";
            var expected = original.Union(samples).ToHashSet();
            checks[stage] = expected.SetEquals(await ReadAllAsync(other, timeout.Token));
            stage = "single_item_pagination";
            var first = await other.ListFavoritesAsync(1, 1, timeout.Token);
            var second = await other.ListFavoritesAsync(2, 1, timeout.Token);
            checks[stage] = first.Total == expected.Count && second.Total == expected.Count &&
                first.Tracks.Count == 1 && second.Tracks.Count == 1 &&
                first.Tracks[0].Reference != second.Tracks[0].Reference &&
                first.Tracks[0].IsFavorite && second.Tracks[0].IsFavorite;
            stage = checks.Values.All(value => value) ? "complete" : "contract_mismatch";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            if (original is not null && connection.AuthenticatedClient is { } api)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                bool restored = true;
                foreach (var reference in attempted)
                {
                    try
                    {
                        if (!original.Contains(reference)) await api.SetFavoriteAsync(reference, false, cleanup.Token);
                    }
                    catch (Exception) { restored = false; }
                }
                try
                {
                    var current = await ReadAllAsync(observer.AuthenticatedClient ?? api, cleanup.Token);
                    restored &= !current.Overlaps(attempted) && original.IsSubsetOf(current);
                    checks["original_favorites_unchanged"] = original.IsSubsetOf(current);
                }
                catch (Exception) { restored = false; }
                checks["temporary_additions_removed"] = restored;
                if (!restored) stage = "cleanup_incomplete";
            }
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "favorite-write-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks }));
    }

    private static async Task<HashSet<TrackReference>> ReadAllAsync(NasApiClient api, CancellationToken ct)
    {
        var result = new HashSet<TrackReference>();
        int? total = null;
        for (int page = 1; page <= 100; page++)
        {
            var batch = await api.ListFavoritesAsync(page, 100, ct);
            total ??= batch.Total;
            if (batch.Total != total || total > 10000 || batch.Tracks.Any(track => !result.Add(track.Reference)))
                throw new InvalidOperationException();
            if (result.Count == total) return result;
            if (batch.Tracks.Count != 100) throw new InvalidOperationException();
        }
        throw new InvalidOperationException();
    }
}
#endif
