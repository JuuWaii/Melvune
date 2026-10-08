using System.Text.Json;
using FnMusic.Infrastructure;

internal static class LocalIndexVerification
{
    private static void Require(bool value) { if (!value) throw new Exception("Local index assertion failed."); }
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "fnmusic-local-index-" + Guid.NewGuid().ToString("N"));
        string music = Path.Combine(root, "music"), state = Path.Combine(root, "state"); Directory.CreateDirectory(music);
        string first = Path.Combine(music, "第一首.wav"), second = Path.Combine(music, "second.flac"), added = Path.Combine(music, "added.mp3");
        var settings = new LocalMusicSettings(Guid.NewGuid(), music); var store = new LocalMusicIndexStore(state);
        int reads = 0;
        async Task<LocalMusicMetadata> Read(string file, CancellationToken ct)
        { reads++; return new(await File.ReadAllTextAsync(file, ct), "歌手", 60) { Album = "专辑" }; }
        try
        {
            await File.WriteAllTextAsync(first, "first"); await File.WriteAllTextAsync(second, "second");
            var initial = await LocalMusicSource.ScanAsync(settings, default, Read); Require(reads == 2);
            await store.SaveAsync(initial);
            var restored = await new LocalMusicIndexStore(state).LoadAsync(settings) ?? throw new Exception("Missing index");
            Require(restored.Tracks.Select(track => track.Reference).SequenceEqual(initial.Tracks.Select(track => track.Reference))
                && restored.Tracks.All(track => track.Artist == "歌手" && track.Album == "专辑" && track.DurationSeconds == 60));
            await using (var stream = await restored.OpenAsync(restored.Tracks[0].Reference, default)) Require(stream.Length == 5);
            // An offline library is restored with no directory enumeration or file metadata reads.
            Directory.Move(music, music + "-offline");
            try
            {
                var offline = await store.LoadAsync(settings); Require(offline?.Tracks.Count == 2 && reads == 2);
                try { await LocalMusicSource.ScanAsync(settings, default, Read, previous: offline); throw new Exception("Offline scan succeeded"); }
                catch (DirectoryNotFoundException) { }
                Require((await store.LoadAsync(settings))?.Tracks.Count == 2);
            }
            finally { Directory.Move(music + "-offline", music); }
            reads = 0; var unchanged = await LocalMusicSource.ScanAsync(settings, default, Read, previous: restored); Require(reads == 0);
            var legacyIndex = initial.CreateIndex() with { MetadataRevision = 0 };
            var legacyCacheFile = Path.Combine(state, "local-index", settings.SourceId.ToString("N") + ".json");
            // The actual old JSON has no revision property. Loading it must not touch the music files.
            await File.WriteAllTextAsync(legacyCacheFile, JsonSerializer.Serialize(new { Version = 1,
                Index = new { legacyIndex.Settings, legacyIndex.Entries } }));
            var legacy = await store.LoadAsync(settings) ?? throw new Exception("Legacy index lost");
            Require(reads == 0 && legacy.Tracks.Count == 2 && legacy.CreateIndex().MetadataRevision == 0);
            await using (var playable = await legacy.OpenAsync(legacy.Tracks[0].Reference, default)) Require(playable.Length == 5);
            byte[] oldBytes = await File.ReadAllBytesAsync(legacyCacheFile);
            using (var upgradeCanceled = new CancellationTokenSource())
            {
                try
                {
                    await LocalMusicSource.ScanAsync(settings, upgradeCanceled.Token, (_, ct) =>
                    { upgradeCanceled.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new LocalMusicMetadata("", "", 0)); }, previous: legacy);
                    throw new Exception("Canceled metadata upgrade returned");
                }
                catch (OperationCanceledException) { }
                byte[] afterUpgradeCanceled = await File.ReadAllBytesAsync(legacyCacheFile);
                Require(oldBytes.SequenceEqual(afterUpgradeCanceled) && legacy.Tracks.Count == 2);
            }
            var upgraded = await LocalMusicSource.ScanAsync(settings, default, Read, previous: legacy);
            Require(reads == 2 && upgraded.CreateIndex().MetadataRevision > 0
                && upgraded.Tracks.Select(track => track.Reference).SequenceEqual(legacy.Tracks.Select(track => track.Reference)));
            await store.SaveAsync(upgraded); reads = 0;
            var persistedUpgrade = await store.LoadAsync(settings);
            await LocalMusicSource.ScanAsync(settings, default, Read, previous: persistedUpgrade); Require(reads == 0);
            string originalId = unchanged.Tracks.Single(track => track.Title == "first").Id;
            await File.WriteAllTextAsync(first, "first changed"); File.Delete(second); await File.WriteAllTextAsync(added, "added");
            var refreshed = await LocalMusicSource.ScanAsync(settings, default, Read, previous: unchanged);
            Require(reads == 2 && refreshed.Tracks.Count == 2 && refreshed.Tracks.Single(track => track.Title == "first changed").Id == originalId
                && refreshed.Tracks.All(track => track.Title != "second"));
            restored.ApplyIndexRefresh(refreshed);
            Require(restored.Tracks.Select(track => track.Title).SequenceEqual(refreshed.Tracks.Select(track => track.Title)));
            try { restored.ApplyIndexRefresh(await LocalMusicSource.ScanAsync(settings with { SourceId = Guid.NewGuid() }, default)); throw new Exception("Foreign refresh accepted"); }
            catch (ArgumentException) { }
            await store.SaveAsync(refreshed);
            var cacheFile = Path.Combine(state, "local-index", settings.SourceId.ToString("N") + ".json");
            byte[] committed = await File.ReadAllBytesAsync(cacheFile);
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(3));
            using (var midScan = new CancellationTokenSource())
            {
                try
                {
                    await LocalMusicSource.ScanAsync(settings, midScan.Token, async (_, ct) =>
                    { midScan.Cancel(); await Task.Delay(1, ct); return new("never", "never", 60); }, previous: refreshed);
                    throw new Exception("Partial scan returned");
                }
                catch (OperationCanceledException) { }
                byte[] afterScan = await File.ReadAllBytesAsync(cacheFile); Require(committed.SequenceEqual(afterScan));
            }
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await store.SaveAsync(initial, canceled.Token); throw new Exception("Canceled save succeeded"); }
            catch (OperationCanceledException) { }
            byte[] afterCanceled = await File.ReadAllBytesAsync(cacheFile); Require(committed.SequenceEqual(afterCanceled));
            Require(await store.LoadAsync(settings with { Directory = root }) is null && await store.LoadAsync(settings with { SourceId = Guid.NewGuid() }) is null);
            var index = refreshed.CreateIndex();
            foreach (string invalid in new[] { "../escape.wav", "sub/../escape.wav", first, "file.wav:stream", "sub//file.wav", "file.exe" })
            {
                var bad = index with { Entries = [index.Entries[0] with { RelativePath = invalid }] };
                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(new { Version = 1, Index = bad }));
                Require(await store.LoadAsync(settings) is null);
            }
            foreach (var bad in new[]
            {
                index with { MetadataRevision = -1 },
                index with { Entries = [index.Entries[0], index.Entries[0]] },
                index with { Entries = [index.Entries[0] with { Modified = long.MaxValue }] },
                index with { Entries = Enumerable.Repeat(index.Entries[0], 10001).ToArray() }
            })
            {
                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(new { Version = 1, Index = bad })); Require(await store.LoadAsync(settings) is null);
            }
            await File.WriteAllTextAsync(cacheFile, "{bad"); Require(await store.LoadAsync(settings) is null);
            using (var huge = new FileStream(cacheFile, FileMode.Create)) huge.SetLength(32 * 1024 * 1024 + 1);
            Require(await store.LoadAsync(settings) is null);
            await store.SaveAsync(refreshed); Require((await store.LoadAsync(settings))?.Tracks.Count == 2);
            Require(!Directory.EnumerateFiles(Path.GetDirectoryName(cacheFile)!).Any(file => file.EndsWith(".tmp")));
        }
        finally
        {
            // Only the uniquely created fixture root is owned by this check.
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(dir => dir.Length)) Directory.Delete(dir);
            Directory.Delete(root);
        }
    }

    internal static async Task CapacityAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "fnmusic-local-index-capacity-" + Guid.NewGuid().ToString("N"));
        var settings = new LocalMusicSettings(Guid.NewGuid(), Path.Combine(root, "offline-music"));
        var entries = Enumerable.Range(0, 10000).Select(i => new LocalMusicIndexEntry($"folder/track-{i}.flac", new("title " + i, "artist", 60), 1000, 100)).ToArray();
        var source = LocalMusicSource.FromIndex(new(settings, entries)); var store = new LocalMusicIndexStore(root);
        try
        {
            await store.SaveAsync(source); var restored = await store.LoadAsync(settings);
            Require(restored?.Tracks.Count == 10000 && !Directory.Exists(settings.Directory)
                && restored.Tracks.Select(track => track.Reference).Distinct().Count() == 10000);
            var empty = LocalMusicSource.FromIndex(new(settings, []));
            var reader = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++) Require(source.CreateIndex().Entries.Count is 0 or 10000);
            });
            for (int i = 0; i < 50; i++) { source.ApplyIndexRefresh(empty); source.ApplyIndexRefresh(restored!); }
            await reader;
            await store.SaveAsync(empty); Require((await store.LoadAsync(settings))?.Tracks.Count == 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
                Directory.Delete(Path.Combine(root, "local-index")); Directory.Delete(root);
            }
        }
    }
}
