#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task VerifyArtworkWindowAsync()
    {
        var checks = new Dictionary<string, bool>();
        string stage = "setup", error = "";
        void Verify(string name, bool value)
        { stage = name; checks[name] = value; if (!value) throw new InvalidOperationException(); }
        try
        {
            motionPreferenceOverride = false;
            string directory = Path.Combine(AppContext.BaseDirectory, "artwork-fixture", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string wavePath = Path.Combine(directory, "artwork.wav");
            using (var wave = Services.PlaybackVerification.CreateWave())
            using (var file = File.Create(wavePath)) await wave.CopyToAsync(file);
            string sidecar = Path.ChangeExtension(wavePath, ".png");
            await File.WriteAllBytesAsync(sidecar, await Services.ArtworkFixture.EncodeAsync(32, 16));
            var source = await Melvune.Infrastructure.LocalMusicSource.ScanAsync(new(Guid.NewGuid(), directory), default);
            ReplaceLocalSource(source);
            SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible;
            await LoadLyricsAsync(source.Tracks.Single());
            await VerifyArtworkResolutionAsync(Verify, sidecar);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { motionPreferenceOverride = null; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "artwork-window-verification.json"),
            System.Text.Json.JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks }));
    }

    private async Task VerifyArtworkResolutionAsync(Action<string, bool> verify, string sidecarPath)
    {
        Task<byte[]> Encode(int width, int height, bool rotated = false) => Services.ArtworkFixture.EncodeAsync(width, height, rotated);
        async Task<WriteableBitmap> Decode(byte[] bytes, int edge) => await Services.ArtworkDecoder.DecodeAsync(bytes, edge, default);
        var large = await Encode(2000, 1000);
        var thumbnail = await Decode(large, 256);
        var full = await Decode(large, 1024);
        verify("artwork_landscape_thumbnail_and_full_resolution", thumbnail.PixelWidth == 256 && thumbnail.PixelHeight == 128
            && full.PixelWidth == 1024 && full.PixelHeight == 512);
        var portraitBytes = await Encode(64, 4096);
        var portrait = await Decode(portraitBytes, 256);
        var portraitFull = await Decode(portraitBytes, 1024);
        verify("artwork_tall_cover_limits_long_edge_and_preserves_ratio", portrait.PixelWidth == 4 && portrait.PixelHeight == 256
            && portraitFull.PixelWidth == 16 && portraitFull.PixelHeight == 1024);
        var small = await Decode(await Encode(32, 16), 1024);
        verify("artwork_small_cover_does_not_upscale", small.PixelWidth == 32 && small.PixelHeight == 16);
        var rotated = await Decode(await Encode(2000, 1000, rotated: true), 1024);
        verify("artwork_exif_rotation_preserves_bounded_orientation", rotated.PixelWidth == 512 && rotated.PixelHeight == 1024);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            bool rejected = false;
            try { await Services.ArtworkDecoder.DecodeAsync(large, 1024, canceled.Token); }
            catch (OperationCanceledException) { rejected = true; }
            verify("artwork_decode_cancellation", rejected);
        }
        var lru = new Services.ArtworkCache(maxEntries: 3);
        lru.Set("a", thumbnail); lru.Set("b", full); lru.Set("c", portrait);
        bool hit = lru.TryGetValue("a", out var retrieved) && ReferenceEquals(retrieved, thumbnail);
        lru.Set("d", portraitFull);
        verify("artwork_lru_keeps_recent_hits_and_evicts_one_old_entry", hit && lru.Count == 3
            && !lru.TryGetValue("b", out _) && lru.TryGetValue("c", out _) && lru.TryGetValue("a", out _));
        var productionLimits = new Services.ArtworkCache();
        for (int i = 0; i < 100; i++) productionLimits.Set("entry" + i, small);
        verify("artwork_production_cache_retains_96_entries", productionLimits.Count == 96
            && !productionLimits.TryGetValue("entry3", out _) && productionLimits.TryGetValue("entry4", out _)
            && productionLimits.TryGetValue("entry99", out _));
        for (int i = 0; i < 100; i++) productionLimits.Set("full" + i, full);
        verify("artwork_production_pixel_budget_bounds_full_covers", productionLimits.PixelBytes <= 48 * 1024 * 1024
            && productionLimits.Count == 24 && productionLimits.TryGetValue("full99", out _) && !productionLimits.TryGetValue("full75", out _));
        long fullBytes = (long)full.PixelWidth * full.PixelHeight * 4;
        var budget = new Services.ArtworkCache(maxPixelBytes: fullBytes * 2);
        budget.Set("one", full); budget.Set("two", full); budget.TryGetValue("one", out _); budget.Set("three", full);
        verify("artwork_pixel_budget_evicts_oldest_without_clearing_all", budget.Count == 2 && budget.PixelBytes == fullBytes * 2
            && budget.TryGetValue("one", out _) && !budget.TryGetValue("two", out _));
        budget.Set("one", thumbnail);
        verify("artwork_replacing_entry_updates_pixel_budget", budget.Count == 2
            && budget.PixelBytes == fullBytes + (long)thumbnail.PixelWidth * thumbnail.PixelHeight * 4);
        var insufficient = new Services.ArtworkCache(maxPixelBytes: fullBytes - 1);
        insufficient.Set("small", small); insufficient.Set("too_large", full);
        verify("artwork_oversized_cache_entry_keeps_other_covers", insufficient.Count == 1 && insufficient.TryGetValue("small", out _));
        budget.Clear();
        verify("artwork_cache_clear_releases_entries_and_budget", budget.Count == 0 && budget.PixelBytes == 0 && !budget.Values.Any());

        byte[] original = await File.ReadAllBytesAsync(sidecarPath);
        try
        {
            await CloseNowPlayingAsync();
            await File.WriteAllBytesAsync(sidecarPath, large);
            coverCache.Clear();
            await LoadArtworkAsync(NowCover);
            await LoadArtworkAsync(FullNowCover);
            verify("artwork_full_cover_is_lazy_while_page_closed", NowCover.Source is WriteableBitmap { PixelWidth: 256, PixelHeight: 128 }
                && FullNowCover.Source is null && coverCache.Count == 1);
            var currentThumbnail = NowCover.Source;
            await OpenNowPlayingAsync();
            await LoadArtworkAsync(FullNowCover);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "artwork-page-diagnostic.json"), System.Text.Json.JsonSerializer.Serialize(new
            { fullType = FullNowCover.Source?.GetType().Name, width = (FullNowCover.Source as BitmapSource)?.PixelWidth,
                height = (FullNowCover.Source as BitmapSource)?.PixelHeight, cacheCount = coverCache.Count,
                visibility = LyricsPanel.Visibility.ToString(), fullContext = FullNowCover.DataContext?.GetType().Name,
                sameContext = ReferenceEquals(FullNowCover.DataContext, NowCover.DataContext), active = FullNowCover.Tag is CancellationTokenSource,
                cachePixels = coverCache.Values.Select(bitmap => new[] { bitmap.PixelWidth, bitmap.PixelHeight }).ToArray() }));
            verify("artwork_full_page_uses_separate_high_resolution_cache", FullNowCover.Source is WriteableBitmap { PixelWidth: 1024, PixelHeight: 512 }
                && !ReferenceEquals(FullNowCover.Source, currentThumbnail) && coverCache.Count == 2);
            var currentFull = FullNowCover.Source;
            await LoadArtworkAsync(NowCover);
            verify("artwork_thumbnail_hit_preserves_full_cover_cache", ReferenceEquals(NowCover.Source, currentThumbnail)
                && ReferenceEquals(FullNowCover.Source, currentFull) && coverCache.Count == 2);
            await CloseNowPlayingAsync();
            verify("artwork_close_releases_hidden_full_cover_but_keeps_cache", FullNowCover.Source is null && coverCache.Values.Contains(currentFull));
            await OpenNowPlayingAsync();
            await LoadArtworkAsync(FullNowCover);
            verify("artwork_reopen_reuses_full_cover", ReferenceEquals(FullNowCover.Source, currentFull));
            await CloseNowPlayingAsync();
            coverCache.Clear();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int fullReads = 0;
            localArtworkBeforeCommit = image =>
            {
                if (!ReferenceEquals(image, FullNowCover)) return Task.CompletedTask;
                fullReads++; entered.TrySetResult(); return release.Task;
            };
            try
            {
                await OpenNowPlayingAsync();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var work = FullNowCover.Tag as CancellationTokenSource;
                var duplicate = LoadArtworkAsync(FullNowCover);
                Artwork_Loaded(FullNowCover, new RoutedEventArgs());
                verify("artwork_duplicate_loads_share_pending_request", work is not null && !work.IsCancellationRequested
                    && ReferenceEquals(FullNowCover.Tag, work) && !duplicate.IsCompleted && fullReads == 1);
                await CloseNowPlayingAsync();
                verify("artwork_close_cancels_pending_full_request", work is { IsCancellationRequested: true } && FullNowCover.Source is null);
                release.TrySetResult(); await duplicate;
                verify("artwork_closed_page_discards_late_full_cover", FullNowCover.Source is null
                    && !coverCache.Values.Any(bitmap => bitmap.PixelWidth == 1024));
            }
            finally { localArtworkBeforeCommit = null; release.TrySetResult(); }
            await OpenNowPlayingAsync();
            await LoadArtworkAsync(FullNowCover);
            verify("artwork_reopen_after_cancellation_loads_full_cover", FullNowCover.Source is WriteableBitmap { PixelWidth: 1024, PixelHeight: 512 });
        }
        finally
        {
            await CloseNowPlayingAsync();
            int held = 0;
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                for (; held < 4; held++) await coverSlots.WaitAsync(cleanupTimeout.Token);
                await File.WriteAllBytesAsync(sidecarPath, original);
            }
            finally { if (held > 0) coverSlots.Release(held); }
            coverCache.Clear();
            await LoadArtworkAsync(NowCover);
        }
    }
}
#endif
