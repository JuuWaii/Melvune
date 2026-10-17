#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using Melvune.Infrastructure;

namespace Melvune.App.Services;

/// <summary>Ten thousand valid encoded files, with the production metadata reader and index.</summary>
internal static class LocalLibraryScaleVerification
{
    private sealed class DirectProgress(Action<int> report) : IProgress<int>
    { public void Report(int value) => report(value); }

    internal static async Task RunAsync(Func<LocalMusicSource, Action<string, bool>, Dictionary<string, double>, Task> inspectWindow)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scale-fixture-" + Guid.NewGuid().ToString("N"));
        string music = Path.Combine(root, "music"), state = Path.Combine(root, "state");
        var files = new List<string>(); var directories = new List<string>();
        var checks = new Dictionary<string, bool>(); var timings = new Dictionary<string, double>();
        var memory = new Dictionary<string, long>();
        string stage = "prepare", error = ""; bool cleanup = true;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = lifetime.Token;
        var elapsed = Stopwatch.StartNew();
        void Verify(string name, bool value)
        { checks[name] = value; if (!value) throw new InvalidOperationException("Scale check failed"); }
        void Memory(string name)
        { using var process = Process.GetCurrentProcess(); process.Refresh(); memory[name] = process.PrivateMemorySize64; }
        void Progress(string name, int count) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "local-scale-progress.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage = name, count, seconds = elapsed.Elapsed.TotalSeconds }));
        void DirectoryOwned(string path) { Directory.CreateDirectory(path); directories.Add(path); }
        try
        {
            if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < 1024L * 1024 * 1024) throw new IOException("Fixture needs free space");
            DirectoryOwned(root); DirectoryOwned(music); DirectoryOwned(state);
            string[] formats = ["wav", "flac", "mp3", "opus", "m4a"];
            Memory("before_generation"); stage = "generate";
            var timer = Stopwatch.StartNew();
            var seeds = new Dictionary<string, byte[]>();
            foreach (string format in formats)
            {
                string path = Path.Combine(root, "seed." + format); files.Add(path);
                var start = new ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i",
                    "sine=frequency=440:sample_rate=48000:duration=2", "-ac", "2", "-metadata", "title=Scale " + format,
                    "-metadata", "artist=Scale Artist " + format, "-metadata", "album=Scale Album", path }) start.ArgumentList.Add(arg);
                using var encoder = Process.Start(start) ?? throw new IOException("Encoder unavailable");
                var stderr = encoder.StandardError.ReadToEndAsync();
                try { await encoder.WaitForExitAsync(ct); }
                finally { if (!encoder.HasExited) { encoder.Kill(true); await encoder.WaitForExitAsync(); } }
                await stderr; if (encoder.ExitCode != 0) throw new IOException("Encoding failed");
                seeds[format] = await File.ReadAllBytesAsync(path, ct);
            }
            await Task.Run(async () =>
            {
                for (int directory = 0; directory < 100; directory++)
                {
                    string folder = Path.Combine(music, "folder-" + directory.ToString("D3")); DirectoryOwned(folder);
                    for (int index = 0; index < 100; index++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string format = formats[index % formats.Length];
                        string path = Path.Combine(folder, "track-" + index.ToString("D3") + "." + format); files.Add(path);
                        await File.WriteAllBytesAsync(path, seeds[format], ct);
                    }
                    Progress(stage, (directory + 1) * 100);
                }
            }, ct);
            timings["generate_seconds"] = timer.Elapsed.TotalSeconds;
            var settings = new LocalMusicSettings(Guid.NewGuid(), music); var store = new LocalMusicIndexStore(state);
            int reads = 0;
            async Task<LocalMusicMetadata> Read(string path, CancellationToken token)
            { Interlocked.Increment(ref reads); return await LocalMusicMetadataReader.ReadAsync(path, token); }
            stage = "initial_scan"; Progress(stage, 0); timer.Restart();
            var source = await LocalMusicSource.ScanAsync(settings, ct, Read, new DirectProgress(count =>
            { if (count % 1000 == 0) Progress("initial_scan", count); }));
            timings["initial_scan_seconds"] = timer.Elapsed.TotalSeconds; Memory("after_initial_scan");
            Verify("ten_thousand_valid_audio_rows_and_unique_references", source.Tracks.Count == 10000 && reads == 10000
                && source.Tracks.Select(track => track.Reference).Distinct().Count() == 10000);
            Verify("production_tags_and_duration_for_every_file", source.Tracks.All(track => track.Title.StartsWith("Scale ")
                && track.Artist.StartsWith("Scale Artist ") && track.Album == "Scale Album" && track.DurationSeconds is > 1.8 and < 2.4));
            Verify("five_encodings_each_two_thousand_rows", formats.All(format => source.Tracks.Count(track => track.Title == "Scale " + format) == 2000));
            stage = "save_index"; timer.Restart(); await store.SaveAsync(source, ct);
            string cache = Path.Combine(state, "local-index", settings.SourceId.ToString("N") + ".json");
            files.Add(cache); directories.Add(Path.GetDirectoryName(cache)!);
            timings["index_save_seconds"] = timer.Elapsed.TotalSeconds;
            stage = "restore_index"; timer.Restart();
            var restored = await new LocalMusicIndexStore(state).LoadAsync(settings, ct) ?? throw new IOException("Index missing");
            timings["index_restore_seconds"] = timer.Elapsed.TotalSeconds; Memory("after_restore");
            Verify("persistent_index_restores_all_fields_and_identities", restored.Tracks.Select(track => track.Reference).SequenceEqual(source.Tracks.Select(track => track.Reference))
                && restored.Tracks.Select(track => (track.Title, track.Artist, track.Album, track.DurationSeconds))
                    .SequenceEqual(source.Tracks.Select(track => (track.Title, track.Artist, track.Album, track.DurationSeconds))));
            byte[] committed = await File.ReadAllBytesAsync(cache, ct);
            stage = "unchanged_refresh"; reads = 0; timer.Restart();
            var unchanged = await LocalMusicSource.ScanAsync(settings, ct, Read, previous: restored);
            timings["unchanged_refresh_seconds"] = timer.Elapsed.TotalSeconds; Memory("after_unchanged_refresh");
            Verify("unchanged_refresh_skips_all_metadata_reads", reads == 0 && unchanged.Tracks.Count == 10000);
            stage = "cancel_refresh";
            using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                bool observed = false;
                try
                {
                    await LocalMusicSource.ScanAsync(settings, canceled.Token, Read, new DirectProgress(count =>
                    { if (count >= 5000) canceled.Cancel(); }), unchanged);
                }
                catch (OperationCanceledException) { observed = true; }
                byte[] retained = await File.ReadAllBytesAsync(cache, ct);
                Verify("cancel_halfway_preserves_snapshot_and_persisted_index", observed && reads == 0
                    && unchanged.Tracks.Count == 10000 && committed.SequenceEqual(retained));
            }
            stage = "capacity_overflow";
            string excess = Path.Combine(music, "overflow.mp3"); files.Add(excess); await File.WriteAllBytesAsync(excess, seeds["mp3"], ct);
            bool rejected = false;
            try { await LocalMusicSource.ScanAsync(settings, ct, Read, previous: unchanged); }
            catch (InvalidDataException) { rejected = true; }
            Verify("ten_thousand_and_one_rejected_without_replacing_index", rejected && (await store.LoadAsync(settings, ct))?.Tracks.Count == 10000);
            File.Delete(excess); reads = 0;
            stage = "single_file_refresh";
            var changed = unchanged.Tracks.First(track => track.Title == "Scale wav");
            string changedPath = unchanged.GetFilePath(changed.Reference); File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddMinutes(2));
            timer.Restart(); var updated = await LocalMusicSource.ScanAsync(settings, ct, Read, previous: unchanged);
            timings["single_file_refresh_seconds"] = timer.Elapsed.TotalSeconds;
            Verify("single_change_reads_one_file_and_keeps_all_identities", reads == 1
                && updated.Tracks.Select(track => track.Reference).SequenceEqual(unchanged.Tracks.Select(track => track.Reference)));
            stage = "window"; Progress(stage, 10000);
            await inspectWindow(restored, Verify, timings); Memory("after_window");
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally
        {
            // Exact owned paths only. Do not traverse or delete any caller-provided directory.
            try
            {
                string prefix = root + Path.DirectorySeparatorChar;
                foreach (string path in files.Distinct(StringComparer.OrdinalIgnoreCase))
                { if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); File.Delete(path); }
                foreach (string path in directories.OrderByDescending(path => path.Length))
                { if (path != root && !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); Directory.Delete(path); }
            }
            catch (Exception) { cleanup = false; }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "local-scale-verification.json"),
                JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, timings, memory, cleanup }));
        }
    }
}
#endif
