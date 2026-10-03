#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task<LocalMusicSource> VerifyLyricsSourceChangesAsync(Action<string, bool> verify, LocalMusicSource source, MusicTrack track)
    {
        string sidecar = Path.ChangeExtension(source.GetFilePath(track.Reference), ".lrc");
        string original = await File.ReadAllTextAsync(sidecar);
        try
        {
            await File.WriteAllTextAsync(sidecar, "[offset:-250]\r[00:00]第一句\r[00:10]第二句", System.Text.Encoding.Unicode);
            await LoadLyricsAsync(track);
            verify("lyrics_cr_utf16_rows_and_offset_presented", currentLyrics.IsTimed && LyricsList.Items.Count == 2
                && currentLyrics.Lines[0] == new LyricLine(.25, "第一句") && currentLyrics.Lines[1] == new LyricLine(10.25, "第二句"));
            await File.WriteAllTextAsync(sidecar, "[ti:fixture]\r第一句\r第二句");
            await LoadLyricsAsync(track);
            verify("lyrics_cr_plain_rows_presented", !currentLyrics.IsTimed && LyricsList.Items.Count == 2
                && currentLyrics.Lines.Select(line => line.Text).SequenceEqual(new[] { "第一句", "第二句" }) && LyricsStatus.Text.Contains("纯文本"));
        }
        finally { await File.WriteAllTextAsync(sidecar, original); }
        await LoadLyricsAsync(track);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lyricsBeforeCommit = () => { entered.TrySetResult(); return release.Task; };
        try
        {
            var pending = LoadLyricsAsync(track);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(sidecar, "[00:00]刷新后的歌词");
            source = await LocalMusicSource.ScanAsync(source.Settings, default);
            ReplaceLocalSource(source);
            lyricsBeforeCommit = null; release.TrySetResult(); await pending;
            verify("lyrics_replaced_source_discards_late_result", currentLyrics.Lines.Count == 0 && LyricsList.ItemsSource is null
                && LyricsStatus.Text.Contains("来源已变化"));
            var currentAudio = music.Player.Source;
            await CloseNowPlayingAsync();
            await OpenNowPlayingAsync();
            for (int i = 0; i < 100 && currentLyrics.Lines.Count == 0; i++) await Task.Delay(20);
            verify("lyrics_replaced_source_retry_reads_current_file", currentLyrics.Lines.Count == 1 && currentLyrics.Lines[0].Text == "刷新后的歌词"
                && !lyricsNeedsRetry && ReferenceEquals(currentAudio, music.Player.Source));

            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lyricsBeforeCommit = () => { entered.TrySetResult(); return release.Task; };
            pending = LoadLyricsAsync(track);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ViewModel.PlaybackRouter.Unregister(source.Source.Id);
            ViewModel.PlaybackRouter.Register(new CapabilityPlaybackSource(source, source.Source.Capabilities & ~MusicSourceCapabilities.Lyrics));
            lyricsBeforeCommit = null; release.TrySetResult(); await pending;
            verify("lyrics_removed_capability_discards_late_result", currentLyrics.Lines.Count == 0 && LyricsStatus.Text.Contains("不支持歌词"));
            ViewModel.PlaybackRouter.Unregister(source.Source.Id); ViewModel.PlaybackRouter.Register(source);
            await LoadLyricsAsync(track);
            verify("lyrics_restored_capability_reloads", currentLyrics.Lines.Count == 1 && currentLyrics.Lines[0].Text == "刷新后的歌词");

            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lyricsBeforeCommit = () => { entered.TrySetResult(); return release.Task; };
            pending = LoadLyricsAsync(track);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lyricsBeforeCommit = null;
            var changed = new MusicTrack("changed-presentation", "新的呈现曲目", "夹具", 60, false) { Album = "新的专辑" };
            await LoadLyricsAsync(changed);
            var current = currentLyrics;
            release.TrySetResult(); await pending;
            verify("lyrics_changed_track_discards_late_result", ReferenceEquals(current, currentLyrics)
                && currentLyrics.Lines[0].Text == "合成歌词验证" && ReferenceEquals(NowCover.DataContext, changed) && NowAlbum.Text == "新的专辑"
                && FullNowTitle.Text == changed.Title && ReferenceEquals(FullNowCover.DataContext, changed));
        }
        finally
        {
            lyricsBeforeCommit = null; release.TrySetResult();
            ViewModel.PlaybackRouter.Unregister(source.Source.Id); ViewModel.PlaybackRouter.Register(source);
            await File.WriteAllTextAsync(sidecar, original);
        }
        await LoadLyricsAsync(track);
        return source;
    }
    private async Task VerifyStoppedLyricsRequestAsync(Action<string, bool> verify, MusicTrack track)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lyricsBeforeCommit = () => { entered.TrySetResult(); return release.Task; };
        try
        {
            var pending = LoadLyricsAsync(track);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lyricsBeforeCommit = null;
            Stop_Click(this, new Microsoft.UI.Xaml.RoutedEventArgs());
            string stoppedStatus = LyricsStatus.Text;
            release.TrySetResult(); await pending;
            verify("lyrics_stop_discards_late_result", currentLyrics.Lines.Count == 0 && LyricsList.ItemsSource is null
                && LyricsStatus.Text == stoppedStatus && NowCover.DataContext is null);
            await OpenNowPlayingAsync();
            verify("fullscreen_stopped_presentation_has_empty_state", FullNowTitle.Text == "尚未播放歌曲" && FullNowArtist.Text.Length == 0
                && FullNowCover.Source is null && LyricsStatus.Text.Contains("播放歌曲后"));
            await CloseNowPlayingAsync();
        }
        finally { lyricsBeforeCommit = null; release.TrySetResult(); }
    }
}
#endif
