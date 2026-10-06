#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using System.Text.Json;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async Task<LocalMusicSource> LoadEmbeddedLyricsPreviewAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "embedded-lyric-files");
        var source = await LocalMusicSource.ScanAsync(new(Guid.NewGuid(), directory), default, Services.LocalMusicMetadataReader.ReadAsync);
        ReplaceLocalSource(source); localView = true; allMusicView = false; favoritesView = false; collectionKind = null; collection = null;
        await ShowLocalMusicAsync(); TrackList.ItemsSource = source.Tracks; TrackList.SelectedIndex = 0;
        page = 1; total = source.Tracks.Count; PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        PageStatus.Text = $"内嵌歌词有效音频夹具 · {total} 首 · 无同名 LRC";
        return source;
    }
    private async Task VerifyEmbeddedLyricsWindowAsync()
    {
        string stage = "setup", error = ""; var checks = new Dictionary<string, bool>();
        void Verify(string key, bool value) { stage = key; checks[key] = value; if (!value) throw new InvalidOperationException(); }
        try
        {
            motionPreferenceOverride = false; music.Player.IsMuted = true; UpdateVolumeControl();
            var source = await LoadEmbeddedLyricsPreviewAsync();
            var before = source.Tracks.ToDictionary(t => t.Reference, t => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source.GetFilePath(t.Reference)))));
            Verify("embedded_fixture_has_twelve_files_and_no_sidecar", source.Tracks.Count == 12 && !Directory.EnumerateFiles(source.Settings.Directory, "*.lrc").Any());
            foreach (var track in source.Tracks)
            {
                string file = Path.GetFileName(source.GetFilePath(track.Reference)); bool timed = file.StartsWith("timed.", StringComparison.Ordinal);
                TrackList.SelectedItem = track; await StartQueueAsync(track);
                for (int i = 0; i < 100 && (!mediaReady || music.Player.PlaybackSession.Position.TotalSeconds <= 0); i++) await Task.Delay(50);
                Verify(file + "_native_playback", mediaReady && queue.Current?.Reference == track.Reference && music.Player.PlaybackSession.Position.TotalSeconds > 0);
                var decoded = await ReadDecoderLyricsAsync(track, default);
                Verify(file + "_shared_playback_stream_extracts_tags", decoded.IsTimed == timed && decoded.Lines.Count == (timed ? 3 : 2));
                await OpenNowPlayingAsync();
                for (int i = 0; i < 100 && currentLyrics.Lines.Count == 0; i++) await Task.Delay(20);
                Verify(file + "_embedded_lyrics_presented", LyricsPanel.Visibility == Visibility.Visible && currentLyrics.IsTimed == timed
                    && LyricsList.Items.Count == (timed ? 3 : 2) && currentLyrics.Lines[0].Text == (timed ? "内嵌第一句" : "内嵌纯文本"));
                if (timed)
                {
                    music.Player.Pause(); pendingSeek = 7; CommitSeek();
                    for (int i = 0; i < 100 && (submittedSeek is not null || Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 7) > 1); i++) await Task.Delay(50);
                    UpdateLyricsPosition();
                    Verify(file + "_seek_highlights_embedded_line", currentLyrics.FindLine(7) == 2 && LyricsList.SelectedIndex == 2 && Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 7) < 1);
                }
                else Verify(file + "_plain_has_no_timed_selection", !currentLyrics.IsTimed && LyricsList.SelectedIndex == -1 && LyricsStatus.Text.Contains("纯文本"));
                await CloseNowPlayingAsync();
                Verify(file + "_lyrics_close_keeps_source_and_queue", queue.Current?.Reference == track.Reference && music.Player.Source is not null);
            }
            Verify("embedded_fixture_audio_hashes_unchanged", source.Tracks.All(t => before[t.Reference] == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source.GetFilePath(t.Reference))))));
            var oldReference = queue.Current!.Reference; var oldTask = decoderLyrics!;
            var oldTrack = queue.Current!; var waiting = new TaskCompletionSource<Lyrics>(TaskCreationOptions.RunContinuationsAsynchronously);
            decoderLyrics = waiting; var pending = ReadDecoderLyricsAsync(oldTrack, default);
            decoderLyricsReference = new(Guid.NewGuid(), "replacement");
            waiting.TrySetResult(Lyrics.Parse("[00:00]过期标签"));
            Verify("decoder_late_replacement_is_discarded", (await pending).Lines.Count == 0);
            decoderLyricsReference = oldReference; decoderLyrics = oldTask;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await ReadDecoderLyricsAsync(oldTrack, canceled.Token); Verify("decoder_wait_preserves_caller_cancellation", false); }
            catch (OperationCanceledException) { Verify("decoder_wait_preserves_caller_cancellation", true); }
            Stop_Click(this, new RoutedEventArgs());
            Verify("decoder_stop_clears_previous_lyrics", decoderLyrics is null && music.EmbeddedLyrics.Lines.Count == 0 && (await ReadDecoderLyricsAsync(oldTrack, default)).Lines.Count == 0);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { Stop_Click(this, new RoutedEventArgs()); motionPreferenceOverride = null; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "embedded-lyrics-window-verification.json"), JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, stage, error, checks }));
    }
}
#endif
