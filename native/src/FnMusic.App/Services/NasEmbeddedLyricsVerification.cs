#if DEBUG
using System.Text.Json;
using System.Security.Cryptography;
using FnMusic.App.ViewModels;
using FnMusic.Core;
using FnMusic.Infrastructure;
using Windows.Media.Playback;

namespace FnMusic.App.Services;

// Explicit, read-only integration probe. Reports counts and predefined labels only.
internal static class NasEmbeddedLyricsVerification
{
    private sealed record Sample(int Index, bool CachedEmpty, int ApiLines, int EmbeddedLines, bool EmbeddedTimed, bool ApiMatchesEmbedded, bool Playing);
    public static async Task RunAsync()
    {
        string stage = "session", error = "";
        var checks = new Dictionary<string, bool>(); var samples = new List<Sample>();
        var skipped = new List<string>();
        int localLines = 0; bool localTimed = false;
        Lyrics? localLyrics = null;
        byte[]? localHash = null; string? sampleFile = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var connection = new ConnectionViewModel(); using var audio = new NativeMusicPlayer();
        audio.Player.IsMuted = true;
        int mediaFailed = 0;
        audio.Player.MediaFailed += (_, _) => Interlocked.Exchange(ref mediaFailed, 1);
        void Verify(string key, bool value)
        { stage = key; checks[key] = value; if (!value) throw new InvalidOperationException(); }
        try
        {
            var arguments = Environment.GetCommandLineArgs(); int sampleArgument = Array.IndexOf(arguments, "--embedded-sample-file");
            if (sampleArgument >= 0)
            {
                if (sampleArgument + 1 >= arguments.Length) throw new ArgumentException();
                sampleFile = Path.GetFullPath(arguments[sampleArgument + 1]);
                using var file = File.OpenRead(sampleFile);
                localHash = await SHA256.HashDataAsync(file, timeout.Token); file.Position = 0;
                var local = await EmbeddedLyricsReader.ReadAsync(file, timeout.Token);
                localLyrics = local;
                localLines = local.Lines.Count; localTimed = local.IsTimed;
                Verify("known_file_contains_timed_lyrics", localLines > 0 && localTimed);
            }
            await connection.InitializeAsync();
            var api = connection.AuthenticatedClient ?? throw new InvalidOperationException();
            Verify("restored_current_session", api.SourceInstanceId != Guid.Empty);
            var cache = new NasMetadataCache(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FnMusic.Native"));
            var snapshot = await cache.LoadAsync(api.SourceInstanceId, timeout.Token);
            var tracks = snapshot?.Tracks ?? (await api.ListTracksAsync(1, 100, timeout.Token)).Tracks;
            var chosen = new List<(MusicTrack Track, bool Empty)>();
            if (sampleFile is not null)
            {
                string filename = Path.GetFileName(sampleFile); var matches = new List<MusicTrack>(); bool complete = false;
                var seen = new HashSet<TrackReference>();
                for (int page = 1; page <= 101; page++)
                {
                    var files = await api.ListTrackFilesForVerificationAsync(page, 100, timeout.Token);
                    foreach (var entry in files)
                    {
                        Verify("indexed_files_unique_and_bounded", seen.Add(entry.Track.Reference) && seen.Count <= 10000);
                        if (entry.Path[(entry.Path.LastIndexOf('/') + 1)..].Equals(filename, StringComparison.OrdinalIgnoreCase)) matches.Add(entry.Track);
                    }
                    if (files.Count < 100) { complete = true; break; }
                }
                Verify("known_file_has_one_indexed_match", complete && matches.Count == 1 && !matches[0].IsCue);
                var track = matches[0];
                chosen.Add((track, (await cache.ReadLyricsAsync(track.Reference, timeout.Token)) is { Lines.Count: 0 }));
            }
            else
            {
                foreach (var track in tracks.Where(track => !track.IsCue).Take(10000))
                {
                    if ((await cache.ReadLyricsAsync(track.Reference, timeout.Token)) is { Lines.Count: 0 }) chosen.Add((track, true));
                    if (chosen.Count == 10) break;
                }
                chosen.AddRange(tracks.Where(track => !track.IsCue && chosen.All(item => item.Track.Reference != track.Reference)).Take(3).Select(track => (track, false)));
            }
            Verify("bounded_current_source_samples", chosen.Count is > 0 and <= 13 && chosen.All(item => item.Track.SourceInstanceId == api.SourceInstanceId));
            for (int index = 0; index < chosen.Count; index++)
            {
                var item = chosen[index]; stage = "sample_" + index;
                var apiLyrics = await api.GetLyricsAsync(item.Track.Reference, timeout.Token);
                using var sampleDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                sampleDeadline.CancelAfter(TimeSpan.FromSeconds(15)); Interlocked.Exchange(ref mediaFailed, 0);
                using var input = await connection.PlaybackRouter.OpenAsync(item.Track.Reference, sampleDeadline.Token);
                try
                {
                    if (localHash is not null)
                    {
                        // Compare the encoded file, not titles or decoded samples; keep both hashes out of the report.
                        var servedHash = await SHA256.HashDataAsync(input, sampleDeadline.Token);
                        Verify("known_file_matches_served_bytes", localHash.SequenceEqual(servedHash)); input.Position = 0;
                    }
                    await audio.OpenAsync(input, sampleDeadline.Token);
                    while (audio.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing || audio.Player.PlaybackSession.Position.TotalSeconds < .1)
                    {
                        sampleDeadline.Token.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref mediaFailed) != 0) throw new IOException();
                        await Task.Delay(50, sampleDeadline.Token);
                    }
                    var embedded = audio.EmbeddedLyrics;
                    samples.Add(new(index, item.Empty, apiLyrics.Lines.Count, embedded.Lines.Count, embedded.IsTimed,
                        apiLyrics.IsTimed == embedded.IsTimed && apiLyrics.Lines.SequenceEqual(embedded.Lines), true));
                    if (localLyrics is not null)
                    {
                        Verify("known_local_and_served_lyrics_agree", embedded.IsTimed == localTimed && embedded.Lines.SequenceEqual(localLyrics.Lines));
                        var positions = Enumerable.Range(0, embedded.Lines.Count - 1)
                            .Where(i => embedded.Lines[i].Seconds > 3 && embedded.Lines[i + 1].Seconds - embedded.Lines[i].Seconds > .6
                                && embedded.Lines[i].Seconds < item.Track.DurationSeconds - 2).ToArray();
                        Verify("known_lyric_seek_target_exists", positions.Length > 0 && audio.Player.PlaybackSession.CanSeek);
                        int line = positions[positions.Length / 2]; double target = embedded.Lines[line].Seconds + .2;
                        audio.Player.Pause();
                        while (audio.Player.PlaybackSession.PlaybackState != MediaPlaybackState.Paused) await Task.Delay(20, sampleDeadline.Token);
                        audio.Player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
                        while (Math.Abs(audio.Player.PlaybackSession.Position.TotalSeconds - target) > .25) await Task.Delay(20, sampleDeadline.Token);
                        Verify("known_native_seek_aligns_lyric", embedded.FindLine(audio.Player.PlaybackSession.Position.TotalSeconds) == line);
                    }
                    Verify("sample_" + index + "_muted_playback", audio.Player.IsMuted && Volatile.Read(ref mediaFailed) == 0);
                    Verify("sample_" + index + "_bounded_tag_text", embedded.Lines.Count <= 10000);
                }
                finally { audio.Stop(); await audio.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5)); }
                Verify("sample_" + index + "_stop_clears_snapshot", audio.EmbeddedLyrics.Lines.Count == 0);
            }
            if (!samples.Any(sample => sample.EmbeddedLines > 0)) skipped.Add("no_embedded_lyrics_in_selected_files");
            if (!samples.Any(sample => sample.ApiLines == 0 && sample.EmbeddedLines > 0)) skipped.Add("no_empty_api_with_embedded_positive_sample");
            if (sampleFile is not null)
            {
                using var file = File.OpenRead(sampleFile);
                var finalHash = await SHA256.HashDataAsync(file, timeout.Token);
                Verify("known_file_bytes_unchanged", localHash!.SequenceEqual(finalHash));
            }
            stage = skipped.Count == 0 ? "complete" : "complete_with_skips";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { audio.Stop(); try { await audio.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { error = "CleanupTimeout"; } }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "nas-embedded-lyrics-verification.json"),
            JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, stage, error, checks, skipped, samples, localLines, localTimed }));
    }
}
#endif
