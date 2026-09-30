#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using FnMusic.Infrastructure;
using Windows.Media.Playback;

namespace FnMusic.App.Services;

/// <summary>Generated audio only; uses the production scanner, metadata reader and player.</summary>
internal static class LocalFormatVerification
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "format-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var created = new List<string>();
        var results = new List<object>();
        var checks = new Dictionary<string, bool>();
        string[] formats = ["wav", "flac", "mp3", "ogg", "opus", "m4a", "aac", "wma", "aiff", "aif"];
        foreach (string format in formats)
        {
            string stage = "generate", error = "";
            bool title = false, artist = false, album = false, duration = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var audio = new NativeMusicPlayer();
            audio.Player.IsMuted = true;
            int failed = 0;
            audio.Player.MediaFailed += (_, _) => Interlocked.Exchange(ref failed, 1);
            async Task Until(Func<bool> predicate)
            {
                while (!predicate())
                {
                    if (Volatile.Read(ref failed) != 0) throw new IOException("Media failed");
                    await Task.Delay(50, deadline.Token);
                }
                if (Volatile.Read(ref failed) != 0) throw new IOException("Media failed");
            }
            try
            {
                string path = Path.Combine(root, "sample." + format); created.Add(path);
                var start = new ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i",
                    "sine=frequency=440:sample_rate=48000:duration=12", "-metadata", "title=Format Fixture",
                    "-metadata", "artist=FNMusic Test", "-metadata", "album=Local Formats", path }) start.ArgumentList.Add(arg);
                using (var encoder = Process.Start(start) ?? throw new IOException("Encoder unavailable"))
                {
                    var stderr = encoder.StandardError.ReadToEndAsync();
                    try { await encoder.WaitForExitAsync(deadline.Token); }
                    finally { if (!encoder.HasExited) { encoder.Kill(true); await encoder.WaitForExitAsync(); } }
                    await stderr;
                    if (encoder.ExitCode != 0) throw new IOException("Encoding failed");
                }
                stage = "scan";
                var source = await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), root), deadline.Token, LocalMusicMetadataReader.ReadAsync);
                var track = source.Tracks.Single(t => Path.GetExtension(source.GetFilePath(t.Reference)) == "." + format);
                title = track.Title == "Format Fixture"; artist = track.Artist == "FNMusic Test";
                album = track.Album == "Local Formats"; duration = track.DurationSeconds is > 11 and < 14;
                stage = "play";
                await audio.OpenAsync(await source.OpenAsync(track.Reference, deadline.Token), deadline.Token);
                await Until(() => audio.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing &&
                    audio.Player.PlaybackSession.Position.TotalSeconds >= 1);
                stage = "seek";
                if (!audio.Player.PlaybackSession.CanSeek) throw new IOException("Seek unavailable");
                audio.Player.PlaybackSession.Position = TimeSpan.FromSeconds(7);
                await Until(() => audio.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing &&
                    audio.Player.PlaybackSession.Position.TotalSeconds is >= 7.5 and < 10);
                stage = "stop";
                audio.Stop(); await audio.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5));
                if (audio.Player.Source is not null) throw new IOException("Source retained");
                stage = "complete";
            }
            catch (Exception ex) { error = ex.GetType().Name; }
            finally
            {
                audio.Stop();
                try { await audio.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { stage = "cleanup"; error = ex.GetType().Name; }
            }
            results.Add(new { format, stage, error, metadata = new { title, artist, album, duration } });
        }
        string invalid = Path.Combine(root, "invalid.aiff"); created.Add(invalid);
        try
        {
            await File.WriteAllBytesAsync(invalid, [1, 2, 3]);
            var metadata = await LocalMusicMetadataReader.ReadAsync(invalid, default);
            checks["invalid_metadata_fallback"] = metadata.DurationSeconds == 0;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await LocalMusicMetadataReader.ReadAsync(invalid, canceled.Token); checks["metadata_cancellation"] = false; }
            catch (OperationCanceledException) { checks["metadata_cancellation"] = true; }
        }
        catch (Exception) { checks["metadata_failure_handling"] = false; }
        bool cleanup = true;
        try { foreach (string path in created) File.Delete(path); Directory.Delete(root); }
        catch (Exception) { cleanup = false; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "local-format-verification.json"),
            JsonSerializer.Serialize(new { generated = true, results, checks, cleanup, untested = new[] { "ape" } }));
    }
}
#endif
