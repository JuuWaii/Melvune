using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FnMusic.App.Services;
using FnMusic.Infrastructure;
using Windows.Media.Playback;

/// <summary>Production Range input throughout repeated playback, not local streams after opening.</summary>
internal static class RangeStressVerification
{
    private sealed record Cycle(string Format, int Number, long BytesRead, int Requests, double Milliseconds,
        long PrivateBytes, bool SourceReleased);

    internal static async Task RunAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "range-stress-fixture-" + Guid.NewGuid().ToString("N"));
        var paths = new List<string>(); var cycles = new List<Cycle>();
        var checks = new Dictionary<string, bool>(); var sizes = new Dictionary<string, long>();
        string stage = "prepare", error = ""; bool cleanup = true;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        void Check(string name, bool value)
        { checks[name] = value; if (!value) throw new InvalidOperationException("Range stress condition failed"); }
        long Memory()
        { using var process = Process.GetCurrentProcess(); process.Refresh(); return process.PrivateMemorySize64; }
        try
        {
            if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < 1024L * 1024 * 1024) throw new IOException("Insufficient fixture space");
            Directory.CreateDirectory(root);
            foreach (string format in new[] { "mp3", "ogg" })
            {
                stage = "encode_" + format;
                string path = Path.Combine(root, "noise." + format); paths.Add(path);
                var start = new ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                string[] codec = format == "ogg" ? ["-c:a", "libvorbis", "-q:a", "6"] : ["-c:a", "libmp3lame", "-b:a", "192k"];
                foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i",
                    "anoisesrc=color=pink:amplitude=0.3:sample_rate=48000:duration=600:seed=42", "-ac", "2" }.Concat(codec).Append(path))
                    start.ArgumentList.Add(arg);
                using var encoder = Process.Start(start) ?? throw new IOException("Encoder unavailable");
                var stderr = encoder.StandardError.ReadToEndAsync();
                try { await encoder.WaitForExitAsync(lifetime.Token).WaitAsync(TimeSpan.FromMinutes(2)); }
                finally { if (!encoder.HasExited) { encoder.Kill(true); await encoder.WaitForExitAsync(); } }
                await stderr; if (encoder.ExitCode != 0) throw new IOException("Encoding failed");
                sizes[format] = new FileInfo(path).Length;
                Check(format + "_encoded_file_larger_than_five_mib", sizes[format] > 5L * 1024 * 1024);
                byte[] original;
                using (var input = File.OpenRead(path)) original = await SHA256.HashDataAsync(input, lifetime.Token);
                using var audio = new NativeMusicPlayer(); audio.Player.IsMuted = true;
                long warmup = 0;
                for (int number = 1; number <= 60; number++)
                {
                    stage = format + "_cycle_" + number;
                    using var handler = new RangeHandler(path, versioned: true);
                    using var client = new HttpClient(handler, false);
                    using var input = await HttpRangeStream.OpenAsync(client, new Uri("https://seek.invalid/audio"), lifetime.Token);
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Opened(MediaPlayer _, object args) => ready.TrySetResult();
                    void Failed(MediaPlayer _, MediaPlayerFailedEventArgs args) => failed.TrySetException(new IOException("Media pipeline failed"));
                    audio.Player.MediaOpened += Opened; audio.Player.MediaFailed += Failed;
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        await audio.OpenAsync(input, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(15));
                        await Await(ready.Task);
                        Check(format + "_cycle_" + number + "_seekable", audio.Player.PlaybackSession.CanSeek);
                        foreach (int target in new[] { 500, 20, 300, 100 })
                        {
                            audio.Player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
                            await Task.Delay(25, lifetime.Token);
                        }
                        await Await(Progress());
                    }
                    finally
                    {
                        audio.Stop();
                        await audio.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5));
                        audio.Player.MediaOpened -= Opened; audio.Player.MediaFailed -= Failed;
                    }
                    long memory = Memory();
                    if (number == 20) warmup = memory;
                    Check(format + "_cycle_" + number + "_released", audio.Player.Source is null);
                    Check(format + "_cycle_" + number + "_range_reads_bounded", handler.Requests is > 1 and < 2048
                        && handler.BytesRead > 0 && handler.BytesRead < sizes[format] * 8 + 20L * 1024 * 1024);
                    cycles.Add(new(format, number, handler.BytesRead, handler.Requests, timer.Elapsed.TotalMilliseconds,
                        memory, audio.Player.Source is null));
                    if (number % 10 == 0) Console.WriteLine($"INFO {format} Range cycle {number}: {memory / 1024} KiB private, {handler.BytesRead} bytes, {handler.Requests} requests");

                    async Task Await(Task operation)
                    { var finished = await Task.WhenAny(operation, failed.Task).WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token); await finished; }
                    async Task Progress()
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        while (audio.Player.PlaybackSession.Position.TotalSeconds is < 100.25 or > 105
                            || audio.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
                            await Task.Delay(25, timeout.Token);
                    }
                }
                Check(format + "_post_warmup_growth_under_96_mib", Memory() - warmup <= 96L * 1024 * 1024);
                byte[] after;
                using (var input = File.OpenRead(path)) after = await SHA256.HashDataAsync(input, lifetime.Token);
                Check(format + "_generated_audio_hash_unchanged", original.SequenceEqual(after));
            }
            Check("both_encodings_complete_sixty_range_cycles", cycles.Count == 120);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; throw; }
        finally
        {
            try
            {
                string prefix = Path.GetFullPath(AppContext.BaseDirectory);
                if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(root).StartsWith("range-stress-fixture-", StringComparison.Ordinal)) throw new IOException();
                foreach (string path in paths) { if (Path.GetDirectoryName(path) != root) throw new IOException(); File.Delete(path); }
                if (Directory.Exists(root)) Directory.Delete(root);
            }
            catch (Exception) { cleanup = false; }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "range-stress-verification.json"),
                JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, cleanup, checks, sizes,
                    cycles, decoderSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "FFmpegInteropX.dll")))) }));
        }
    }
}
