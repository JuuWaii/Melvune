#if DEBUG
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Melvune.Infrastructure;
using Windows.Media.Playback;

namespace Melvune.App.Services;

/// <summary>Real encoded media through the production local source and native player.</summary>
internal static class LocalFormatMatrixVerification
{
    private const string ApeSha256 = "6A7B79A6D530E9847C18119D627BD43C8D27DCEFB3EC7EC979B9B6306E34AC15";
    private sealed record Fixture(string Id, string Extension, int Rate, int Channels, string[] Encoding,
        double Seconds = 12, bool Imported = false);

    internal static async Task RunAsync()
    {
        var fixtures = new Fixture[]
        {
            new("wav_pcm16_44100_mono", "wav", 44100, 1, ["-c:a", "pcm_s16le"]),
            new("wav_pcm24_96000_stereo", "wav", 96000, 2, ["-c:a", "pcm_s24le"]),
            new("wav_float32_48000_stereo", "wav", 48000, 2, ["-c:a", "pcm_f32le"]),
            new("wav_pcm24_48000_surround", "wav", 48000, 6, ["-c:a", "pcm_s24le"]),
            new("flac_16_44100_stereo", "flac", 44100, 2, ["-c:a", "flac", "-sample_fmt", "s16"]),
            new("flac_24_96000_stereo", "flac", 96000, 2, ["-c:a", "flac", "-sample_fmt", "s32", "-compression_level", "12"]),
            new("flac_24_48000_surround", "flac", 48000, 6, ["-c:a", "flac", "-sample_fmt", "s32"]),
            new("mp3_cbr320_44100_stereo", "mp3", 44100, 2, ["-c:a", "libmp3lame", "-b:a", "320k"]),
            new("mp3_vbr0_48000_stereo", "mp3", 48000, 2, ["-c:a", "libmp3lame", "-q:a", "0"]),
            new("mp3_cbr64_22050_mono", "mp3", 22050, 1, ["-c:a", "libmp3lame", "-b:a", "64k"]),
            new("ogg_q0_44100_mono", "ogg", 44100, 1, ["-c:a", "libvorbis", "-q:a", "0"]),
            new("ogg_q9_48000_stereo", "ogg", 48000, 2, ["-c:a", "libvorbis", "-q:a", "9"]),
            new("opus_32_48000_mono", "opus", 48000, 1, ["-c:a", "libopus", "-b:a", "32k"]),
            new("opus_160_48000_stereo", "opus", 48000, 2, ["-c:a", "libopus", "-b:a", "160k"]),
            new("m4a_aac_44100_stereo", "m4a", 44100, 2, ["-c:a", "aac", "-b:a", "192k"]),
            new("m4a_alac16_44100_stereo", "m4a", 44100, 2, ["-c:a", "alac", "-sample_fmt", "s16p"]),
            new("m4a_alac24_96000_stereo", "m4a", 96000, 2, ["-c:a", "alac", "-sample_fmt", "s32p"]),
            new("aac_adts_48000_stereo", "aac", 48000, 2, ["-c:a", "aac", "-b:a", "128k"]),
            new("wma2_44100_stereo", "wma", 44100, 2, ["-c:a", "wmav2", "-b:a", "128k"]),
            new("aiff_pcm24_96000_stereo", "aiff", 96000, 2, ["-c:a", "pcm_s24be"]),
            new("aif_pcm16_44100_mono", "aif", 44100, 1, ["-c:a", "pcm_s16be"]),
            new("aiff_id3_24_48000_stereo", "aiff", 48000, 2, ["-c:a", "pcm_s24be", "-write_id3v2", "1"]),
            new("ape_16_44100_stereo", "ape", 44100, 2, [], 60.48, true)
        };
        string root = Path.Combine(AppContext.BaseDirectory, "format-matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var createdFiles = new List<string>();
        var createdDirectories = new List<string>();
        var results = new List<object>();
        var checks = new Dictionary<string, bool>();
        string[] args = Environment.GetCommandLineArgs();
        int apeArgument = Array.IndexOf(args, "--ape-fixture");
        string? ape = apeArgument >= 0 && apeArgument + 1 < args.Length ? Path.GetFullPath(args[apeArgument + 1]) : null;
        string? originalHash = null;
        bool cleanup = true;
        try
        {
            if (ape is not null && new FileInfo(ape).Length <= 16 * 1024 * 1024)
                originalHash = await HashAsync(ape);
            checks["ape_fixture_pinned"] = originalHash == ApeSha256;
            foreach (var fixture in fixtures)
            {
                string stage = "prepare", error = "";
                double metadataDuration = 0, playbackDuration = 0;
                object? encodedStream = null;
                bool title = false, artist = false, album = false;
                var seeks = new List<object>();
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
                        await Task.Delay(40, deadline.Token);
                    }
                    if (Volatile.Read(ref failed) != 0) throw new IOException("Media failed");
                }
                try
                {
                    string directory = Path.Combine(root, fixture.Id);
                    Directory.CreateDirectory(directory); createdDirectories.Add(directory);
                    string path = Path.Combine(directory, "sample." + fixture.Extension); createdFiles.Add(path);
                    if (fixture.Imported)
                    {
                        if (ape is null || originalHash != ApeSha256) throw new IOException("Pinned APE fixture required");
                        File.Copy(ape, path, false);
                    }
                    else
                    {
                        stage = "encode";
                        var start = new ProcessStartInfo(FfmpegPcmDecoder.FindExecutable())
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i",
                            $"sine=frequency=440:sample_rate={fixture.Rate}:duration=12", "-ac", fixture.Channels.ToString() }
                            .Concat(fixture.Encoding).Concat(new[] { "-metadata", "title=Format Matrix", "-metadata", "artist=Melvune Test",
                                "-metadata", "album=Local Matrix", path })) start.ArgumentList.Add(arg);
                        using var encoder = Process.Start(start) ?? throw new IOException("Encoder unavailable");
                        var stderr = encoder.StandardError.ReadToEndAsync();
                        try { await encoder.WaitForExitAsync(deadline.Token); }
                        finally { if (!encoder.HasExited) { encoder.Kill(true); await encoder.WaitForExitAsync(); } }
                        await stderr;
                        if (encoder.ExitCode != 0) throw new IOException("Encoding failed");
                    }
                    string hash = await HashAsync(path);
                    stage = "stream_info";
                    var probe = new SeekablePlayback(File.OpenRead(path));
                    try
                    {
                        var decoded = await probe.Opening.WaitAsync(deadline.Token);
                        var info = decoded.AudioStreams.Single();
                        encodedStream = new { codec = info.CodecName, rate = info.SampleRate, channels = info.Channels, bits = info.BitsPerSample };
                        checks[fixture.Id + "_encoded_layout"] = info.SampleRate == fixture.Rate && info.Channels == fixture.Channels;
                    }
                    finally { await probe.Stop().WaitAsync(TimeSpan.FromSeconds(5)); }
                    stage = "scan";
                    var source = await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), directory), deadline.Token, LocalMusicMetadataReader.ReadAsync);
                    var track = source.Tracks.Single();
                    metadataDuration = track.DurationSeconds;
                    title = track.Title == (fixture.Imported ? "Lucky Night" : "Format Matrix");
                    artist = track.Artist == (fixture.Imported ? "Jody Marie Gnant" : "Melvune Test");
                    album = track.Album == (fixture.Imported ? "Treasure Quest Soundtrack" : "Local Matrix");
                    if (fixture.Extension != "aac" && (fixture.Extension is not ("aiff" or "aif") || fixture.Encoding.Contains("-write_id3v2")))
                        checks[fixture.Id + "_complete_tags"] = title && artist && album;
                    else if (fixture.Extension is "aiff" or "aif") checks[fixture.Id + "_available_title"] = title;
                    checks[fixture.Id + "_duration"] = Math.Abs(metadataDuration - fixture.Seconds) < .6;
                    var restored = LocalMusicSource.FromIndex(source.CreateIndex());
                    var cachedTrack = restored.Tracks.Single();
                    checks[fixture.Id + "_index_roundtrip"] = cachedTrack.Reference == track.Reference
                        && cachedTrack.Title == track.Title && cachedTrack.Artist == track.Artist && cachedTrack.Album == track.Album
                        && cachedTrack.DurationSeconds == track.DurationSeconds && cachedTrack.LibraryIds.SequenceEqual(track.LibraryIds);
                    stage = "play";
                    await audio.OpenAsync(await restored.OpenAsync(track.Reference, deadline.Token), deadline.Token);
                    await Until(() => audio.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing &&
                        audio.Player.PlaybackSession.Position.TotalSeconds >= .6);
                    playbackDuration = audio.Player.PlaybackSession.NaturalDuration.TotalSeconds;
                    checks[fixture.Id + "_playback_duration"] = Math.Abs(playbackDuration - fixture.Seconds) < .6;
                    if (!audio.Player.PlaybackSession.CanSeek) throw new IOException("Seek unavailable");
                    foreach (double target in new[] { 7d, 2d, 10d, 0d, 5d })
                    {
                        stage = "seek";
                        // Pause and observe the requested clock before resuming, so an old position
                        // cannot satisfy the new seek check while native playback is still switching.
                        audio.Player.Pause();
                        await Until(() => audio.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
                        audio.Player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
                        await Until(() => Math.Abs(audio.Player.PlaybackSession.Position.TotalSeconds - target) < .12);
                        audio.Player.Play();
                        await Until(() => audio.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing &&
                            audio.Player.PlaybackSession.Position.TotalSeconds >= target + .3 &&
                            audio.Player.PlaybackSession.Position.TotalSeconds < target + 1.5);
                        seeks.Add(new { target, position = audio.Player.PlaybackSession.Position.TotalSeconds });
                    }
                    stage = "stop";
                    audio.Stop(); await audio.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5));
                    checks[fixture.Id + "_stop_detaches"] = audio.Player.Source is null;
                    checks[fixture.Id + "_audio_unchanged"] = await HashAsync(path) == hash;
                    stage = "complete";
                }
                catch (Exception ex) { error = ex.GetType().Name; }
                finally
                {
                    audio.Stop();
                    try { await audio.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception ex) { stage = "cleanup"; error = ex.GetType().Name; }
                }
                results.Add(new { fixture.Id, fixture.Extension, fixture.Rate, fixture.Channels, fixture.Encoding,
                    fixture.Imported, stage, error, encodedStream, metadataDuration, playbackDuration, title, artist, album, seeks });
            }
            checks["ape_original_unchanged"] = ape is not null && originalHash == ApeSha256 && await HashAsync(ape) == originalHash;
        }
        finally
        {
            // Only exact paths created by this run; never recurse into a supplied fixture path.
            try
            {
                foreach (string file in createdFiles) File.Delete(file);
                foreach (string directory in createdDirectories) Directory.Delete(directory);
                Directory.Delete(root);
            }
            catch (Exception) { cleanup = false; }
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "local-format-matrix-verification.json"),
                JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, results, checks, cleanup }));
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input));
    }
}
#endif
