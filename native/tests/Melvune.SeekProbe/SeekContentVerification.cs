using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FFmpegInteropX;
using Melvune.App.Services;
using Melvune.Infrastructure;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

internal static class SeekContentVerification
{
    private const int Seconds = 600;
    private static int Rate = 48000, Channels = 1;
    private sealed record Marker(double Timestamp, float[] Samples, int Channel = 0);
    private sealed record Alignment(double OffsetMilliseconds, double Correlation);
    private sealed record FrameResult(double Timestamp, double OffsetMilliseconds, double Correlation, bool Aligned, int Channel);
    private sealed record Result(string Format, bool FastSeek, int Target, double Timestamp,
        double? TransientMilliseconds, long Milliseconds, long BytesRead, long EncodedBytes, FrameResult[] Frames, bool Passed);

    public static async Task RunAsync(string[] args)
    {
        Rate = int.Parse(args.FirstOrDefault(value => value.StartsWith("--sample-rate=", StringComparison.Ordinal))?.Split('=')[1] ?? "48000");
        Channels = int.Parse(args.FirstOrDefault(value => value.StartsWith("--channels=", StringComparison.Ordinal))?.Split('=')[1] ?? "1");
        if (Rate is not (44100 or 48000) || Channels is not (1 or 2)) throw new ArgumentException("Unsupported content fixture layout.");
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(Path.Combine(temporaryRoot, $"melvune-content-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory);
        var results = new List<Result>();
        string error = "";
        try
        {
            var reference = CreateReference(Path.Combine(directory, "reference.wav"));
            foreach (var channel in reference) VerifyMatcher(channel);
            string requested = args.FirstOrDefault(value => value.StartsWith("--format=", StringComparison.Ordinal))?[9..] ?? "all";
            var formats = requested == "all" ? new[] { "wav", "flac", "mp3-cbr", "mp3-vbr", "ogg" } : new[] { requested };
            if (formats.Any(format => format is not ("wav" or "flac" or "mp3-cbr" or "mp3-vbr" or "ogg")))
                throw new ArgumentException("Unsupported content verification format.");
            foreach (string format in formats)
            {
                string path = await EncodeAsync(directory, format);
                var decodedReference = format == "wav" ? reference : await DecodeReferenceAsync(path, directory);
                bool readAhead = args.Contains("--read-ahead", StringComparer.Ordinal);
                int[] positions = args.Contains("--near-seeks", StringComparer.Ordinal)
                    ? [5, 6, 7, 8, 7, 6, 0, 1, 579] : [571, 31, 451, 5];
                bool versioned = args.Contains("--etag", StringComparer.Ordinal);
                await VerifyAsync(path, format, decodedReference, false, readAhead, versioned, positions, results);
                if (args.Contains("--fast-seek", StringComparer.Ordinal))
                    await VerifyAsync(path, format, decodedReference, true, readAhead, versioned, positions, results);
            }
            if (results.Any(result => !result.Passed)) throw new InvalidOperationException("Decoded audio alignment failed.");
        }
        catch (Exception exception) { error = exception.GetType().Name; throw; }
        finally
        {
            string report = args.Contains("--fast-seek", StringComparer.Ordinal) ? "seek-content-comparison.json"
                : args.Contains("--etag", StringComparer.Ordinal) ? "seek-content-etag.json" : "seek-content-verification.json";
            if (Rate != 48000 || Channels != 1) report = report[..^5] + $"-{Rate}-{Channels}ch.json";
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, report),
                JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, error, sampleRate = Rate, channels = Channels, seconds = Seconds,
                    decoder = GetDecoderIdentity(), readAhead = args.Contains("--read-ahead", StringComparer.Ordinal),
                    strongVersion = args.Contains("--etag", StringComparer.Ordinal),
                    consumer = "MediaPlayer with MediaStreamSource sample observation",
                    gate = "stable aligned audio within 50ms; correlation >0.8; offset <=50ms; all later observed frames aligned", results }));
            // Only this probe's randomly named, synthetic temporary directory.
            if (!Path.GetFullPath(directory).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(directory).StartsWith("melvune-content-", StringComparison.Ordinal))
                throw new IOException("Refusing cleanup outside the synthetic temporary directory.");
            Directory.Delete(directory, true);
        }
    }

    private static short[][] CreateReference(string path)
    {
        var samples = Enumerable.Range(0, Channels).Select(_ => new short[Rate * Seconds]).ToArray();
        var states = Enumerable.Range(0, Channels).Select(channel => 0x1234abcdU + (uint)channel * 0x1e3779b9U).ToArray();
        var filtered = new double[Channels];
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        int size = Rate * Seconds * Channels * sizeof(short);
        writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)Channels);
        writer.Write(Rate); writer.Write(Rate * Channels * 2); writer.Write((short)(Channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(size);
        for (int index = 0; index < Rate * Seconds; index++)
        {
            for (int channel = 0; channel < Channels; channel++)
            {
                uint state = states[channel];
                state ^= state << 13; state ^= state >> 17; state ^= state << 5; states[channel] = state;
                double noise = (state / (double)uint.MaxValue - .5) * 2;
                // Every position/channel is distinguishable; alternating complexity
                // gives VBR a nonuniform byte/time relation without silent markers.
                bool complex = (index / Rate + channel * 13) % 40 < 10;
                double alpha = complex ? .7 : .12;
                filtered[channel] += alpha * (noise - filtered[channel]);
                samples[channel][index] = (short)(filtered[channel] * (complex ? 16000 : 2500));
                writer.Write(samples[channel][index]);
            }
        }
        return samples;
    }

    private static async Task<string> EncodeAsync(string directory, string format)
    {
        string wave = Path.Combine(directory, "reference.wav");
        if (format == "wav") return wave;
        string path = Path.Combine(directory, format + (format.StartsWith("mp3", StringComparison.Ordinal) ? ".mp3" : "." + format));
        var start = new ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", wave }) start.ArgumentList.Add(argument);
        foreach (string argument in format switch
        {
            "mp3-cbr" => new[] { "-c:a", "libmp3lame", "-b:a", "128k" },
            "mp3-vbr" => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
            "ogg" => new[] { "-c:a", "libvorbis", "-q:a", "4" },
            _ => Array.Empty<string>()
        }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new IOException("Synthetic encoding did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        await stderr;
        if (process.ExitCode != 0) throw new IOException("Synthetic encoding failed.");
        return path;
    }

    private static async Task<short[][]> DecodeReferenceAsync(string path, string directory)
    {
        // Linear decoding of the entire encoded file is an independent content oracle:
        // no seeking and no assumptions about MP3/Vorbis lossy sample differences.
        string pcm = Path.Combine(directory, "linear-reference.pcm");
        var start = new ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", path,
            "-map", "0:a:0", "-ac", Channels.ToString(), "-ar", Rate.ToString(), "-f", "s16le", pcm }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Linear reference decoder did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        await stderr;
        if (process.ExitCode != 0) throw new IOException("Linear reference decoding failed.");
        using var file = File.OpenRead(pcm);
        if (file.Length % (2 * Channels) != 0 || file.Length < Rate * 2L * Channels * (Seconds - 1) || file.Length > Rate * 2L * Channels * (Seconds + 1))
            throw new IOException("Linear reference duration is invalid.");
        var samples = new short[checked((int)(file.Length / 2))];
        file.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
        return Enumerable.Range(0, Channels).Select(channel =>
        {
            var values = new short[samples.Length / Channels];
            for (int index = 0; index < values.Length; index++) values[index] = samples[index * Channels + channel];
            return values;
        }).ToArray();
    }

    private static object? GetDecoderIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var module = process.Modules.Cast<ProcessModule>().FirstOrDefault(module => module.ModuleName.Equals("FFmpegInteropX.dll", StringComparison.OrdinalIgnoreCase));
        if (module is null) return null;
        using var file = File.OpenRead(module.FileName);
        return new { name = module.ModuleName, version = module.FileVersionInfo.FileVersion,
            sha256 = Convert.ToHexString(SHA256.HashData(file)) };
    }

    private static async Task VerifyAsync(string path, string format, short[][] reference, bool fast, bool readAhead, bool versioned, int[] positions, List<Result> results)
    {
        using var handler = new RangeHandler(path, versioned);
        using var client = new HttpClient(handler, false);
        using var input = await HttpRangeStream.OpenAsync(client, new Uri("https://seek.invalid/audio"), CancellationToken.None);
        SeekablePlayback? production = null;
        FFmpegMediaSource? experiment = null;
        using var random = fast || readAhead ? input.AsRandomAccessStream() : null;
        using var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
        try
        {
            FFmpegMediaSource decoded;
            if (fast || readAhead)
            {
                var config = new MediaSourceConfig();
                config.FFmpegOptions.Add("protocol_whitelist", "none");
                if (fast) config.FFmpegOptions.Add("fflags", "+fastseek");
                if (readAhead)
                {
                    config.General.ReadAheadBufferEnabled = true;
                    config.General.ReadAheadBufferSize = 1024 * 1024;
                    config.General.ReadAheadBufferDuration = TimeSpan.FromSeconds(4);
                }
                decoded = experiment = await FFmpegMediaSource.CreateFromStreamAsync(random!, config);
            }
            else
            {
                production = new SeekablePlayback(input);
                decoded = await production.Opening.WaitAsync(TimeSpan.FromSeconds(15));
            }
            var item = decoded.CreateMediaPlaybackItem();
            var stream = decoded.GetMediaStreamSource() ?? throw new IOException("Playback item has no media stream source.");
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (_, _) => opened.TrySetResult();
            player.MediaFailed += (_, _) => failed.TrySetException(new IOException("Media pipeline failed."));
            object gate = new();
            TaskCompletionSource<Marker>? pending = null;
            var markers = new List<Marker>();
            int target = -1;
            void Observe(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
            {
                lock (gate)
                {
                    if (pending is null || markers.Count >= 32 || args.Request.StreamDescriptor is not AudioStreamDescriptor descriptor) return;
                    var sample = args.Request.Sample;
                    // A packet can straddle the requested presentation position. Crop
                    // its preroll rather than treating packet size as a silent delay.
                    if (sample is null || (sample.Timestamp + sample.Duration).TotalSeconds <= target || sample.Timestamp.TotalSeconds > target + .5) return;
                    try
                    {
                        var captured = ReadMarkers(sample, descriptor, target);
                        if (captured.Length > 0) { markers.AddRange(captured); pending.TrySetResult(captured[0]); }
                    }
                    catch (Exception exception) { pending.TrySetException(exception); }
                }
            }
            stream.SampleRequested += Observe;
            try
            {
                player.Source = item;
                await AwaitOrFailure(opened.Task, failed.Task);
                if (!player.PlaybackSession.CanSeek) throw new IOException("Source is not seekable.");
                foreach (int position in positions)
                {
                    player.Pause();
                    Task<Marker> observed;
                    lock (gate) { target = position; markers.Clear(); pending = new(TaskCreationOptions.RunContinuationsAsynchronously); observed = pending.Task; }
                    long before = handler.BytesRead;
                    var watch = Stopwatch.StartNew();
                    player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
                    player.Play();
                    await AwaitOrFailure(observed, failed.Task);
                    var marker = await observed;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while (player.PlaybackSession.Position.TotalSeconds is var clock &&
                        (clock < position + .25 || clock > position + 5 || player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing))
                    { await Task.Delay(25, deadline.Token); if (failed.Task.IsCompleted) await failed.Task; }
                    player.Pause();
                    long bytes = handler.BytesRead - before, elapsed = watch.ElapsedMilliseconds;
                    Marker[] captured; lock (gate) captured = markers.ToArray();
                    var frames = captured.Select(frame =>
                    {
                        var match = Match(reference[frame.Channel], frame, frame.Timestamp);
                        return new FrameResult(frame.Timestamp, match.OffsetMilliseconds, match.Correlation,
                            match.Correlation > .8 && Math.Abs(match.OffsetMilliseconds) <= 50, frame.Channel);
                    }).ToArray();
                    // A deliberately displaced timeline must not pass the same matcher.
                    var control = captured.Last();
                    var displaced = Match(reference[control.Channel], control, control.Timestamp + 20);
                    if (displaced.Correlation > .8) throw new InvalidOperationException("Negative alignment control passed.");
                    var recoveries = Enumerable.Range(0, Channels).Select(channel =>
                    {
                        var observedChannel = frames.Where(frame => frame.Channel == channel).ToArray();
                        int recovery = Array.FindIndex(observedChannel, frame => frame.Aligned);
                        double? delay = recovery < 0 ? null : (observedChannel[recovery].Timestamp - position) * 1000;
                        bool passed = delay is <= 50 && observedChannel.Skip(recovery).Count() >= 2 && observedChannel.Skip(recovery).All(frame => frame.Aligned);
                        return (Delay: delay, Passed: passed);
                    }).ToArray();
                    double? transient = recoveries.Any(value => value.Delay is null) ? null : recoveries.Max(value => value.Delay);
                    bool passed = recoveries.All(value => value.Passed);
                    results.Add(new(format, fast, position, marker.Timestamp, transient, elapsed, bytes, input.Length, frames, passed));
                    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {format} {(fast ? "fast" : "accurate")} content {position}s: transient {transient?.ToString("F3") ?? "unresolved"}ms, {frames.Count(frame => frame.Aligned)}/{frames.Length} aligned frames, {elapsed}ms, read {bytes}");
                }
            }
            finally { stream.SampleRequested -= Observe; player.Source = null; }
        }
        finally { player.Source = null; if (production is not null) await production.Stop().WaitAsync(TimeSpan.FromSeconds(5)); experiment?.Dispose(); }
    }

    private static async Task AwaitOrFailure(Task operation, Task failure)
    {
        var completed = await Task.WhenAny(operation, failure).WaitAsync(TimeSpan.FromSeconds(15));
        await completed;
    }

    private static Marker[] ReadMarkers(MediaStreamSample sample, AudioStreamDescriptor descriptor, int target)
    {
        var properties = descriptor.EncodingProperties;
        if (properties.SampleRate != Rate || properties.ChannelCount != Channels || properties.Subtype is not ("PCM" or "Float"))
            throw new NotSupportedException("Decoded PCM differs from the requested content fixture layout.");
        int width = checked((int)properties.BitsPerSample / 8);
        if (width is not (2 or 4)) throw new NotSupportedException("Unsupported PCM width.");
        var buffer = sample.Buffer ?? throw new IOException("Sample has no buffer.");
        int skip = Math.Max(0, (int)Math.Ceiling((target - sample.Timestamp.TotalSeconds) * Rate));
        int count = Math.Min(2048, checked((int)buffer.Length / (width * Channels)) - skip);
        if (count < 256) return [];
        var bytes = new byte[count * width * Channels];
        using (var reader = DataReader.FromBuffer(buffer))
        { if (skip > 0) reader.ReadBuffer(checked((uint)(skip * width * Channels))); reader.ReadBytes(bytes); }
        return Enumerable.Range(0, Channels).Select(channel =>
        {
            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                int offset = (i * Channels + channel) * width;
                values[i] = properties.Subtype == "Float" ? BitConverter.ToSingle(bytes, offset)
                    : width == 2 ? BitConverter.ToInt16(bytes, offset) / 32768f : BitConverter.ToInt32(bytes, offset) / 2147483648f;
                if (!float.IsFinite(values[i])) throw new IOException("Decoded marker is not finite PCM.");
            }
            return new Marker(sample.Timestamp.TotalSeconds + skip / (double)Rate, values, channel);
        }).ToArray();
    }

    private static Alignment Match(short[] reference, Marker marker, double timestamp)
    {
        int expected = (int)Math.Round(timestamp * Rate);
        int first = Math.Max(0, expected - Rate * 2), last = Math.Min(reference.Length - marker.Samples.Length, expected + Rate * 2);
        int best = first;
        double score = double.NegativeInfinity;
        // Near the beginning, clamping the search to zero changes its coarse
        // phase. Check the expected alignment directly before the broad search:
        // narrow-band correlation peaks must not fall between coarse candidates.
        for (int candidate = Math.Max(first, expected - 8); candidate <= Math.Min(last, expected + 8); candidate++)
        {
            double correlation = Correlate(reference, marker.Samples, candidate, 1);
            if (correlation > score) { score = correlation; best = candidate; }
        }
        for (int candidate = first; candidate <= last; candidate += 8)
        {
            double correlation = Correlate(reference, marker.Samples, candidate, 8);
            if (correlation > score) { score = correlation; best = candidate; }
        }
        int coarse = best; score = double.NegativeInfinity;
        for (int candidate = Math.Max(first, coarse - 8); candidate <= Math.Min(last, coarse + 8); candidate++)
        {
            double correlation = Correlate(reference, marker.Samples, candidate, 1);
            if (correlation > score) { score = correlation; best = candidate; }
        }
        return new((best - expected) * 1000d / Rate, score);
    }

    private static void VerifyMatcher(short[] reference)
    {
        foreach (int position in new[] { 47, 6959, 48001, 143999, Rate * 31 + 113 })
        {
            float[] samples = reference.AsSpan(position, 1024).ToArray().Select(sample => sample / 32768f).ToArray();
            var marker = new Marker(position / (double)Rate, samples);
            var correct = Match(reference, marker, marker.Timestamp);
            if (correct.OffsetMilliseconds != 0 || correct.Correlation < .999)
                throw new InvalidOperationException("Known PCM alignment failed.");
            var displaced = Match(reference, marker, marker.Timestamp + 20);
            if (displaced.Correlation > .8)
                throw new InvalidOperationException("Displaced PCM control passed.");
        }
    }

    private static double Correlate(short[] reference, float[] sample, int start, int stride)
    {
        double dot = 0, energyReference = 0, energySample = 0;
        for (int index = 0; index < sample.Length; index += stride)
        {
            double left = reference[start + index], right = sample[index];
            dot += left * right; energyReference += left * left; energySample += right * right;
        }
        return dot / Math.Sqrt(Math.Max(double.Epsilon, energyReference * energySample));
    }
}
