#if DEBUG
using Melvune.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;

/// <summary>Counts actual dispatcher/render/text work; does not infer GPU utilization or frame rate.</summary>
internal static class DesktopLyricsEfficiencyVerification
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var hub = new PlaybackSnapshotHub();
        using var window = new DesktopLyricsWindow(hub, new DesktopLyricsOptions());
        var generation = Guid.NewGuid();
        var snapshot = hub.Current with { State = PlaybackStatus.Playing, TrackGeneration = generation,
            Title = "效率回归", LyricsKind = PlaybackLyricsKind.Timed, CurrentLine = "初始歌词" };
        int dispatches = window.SnapshotDispatches, renders = window.SnapshotRenders, writes = window.TextUpdates;
        for (int i = 0; i < 100; i++) hub.Publish(snapshot with { PositionSeconds = i });
        await Task.Delay(80);
        check("render_hidden_100_publications_zero_ui_dispatch", window.SnapshotDispatches == dispatches
            && window.SnapshotRenders == renders && window.TextUpdates == writes);
        window.ShowLyrics();
        check("render_reopen_reads_latest_snapshot_immediately", window.DisplayedLyrics == "初始歌词");

        dispatches = window.SnapshotDispatches; renders = window.SnapshotRenders; writes = window.TextUpdates;
        for (int i = 0; i < 100; i++) hub.Publish(snapshot with { PositionSeconds = i, CurrentLine = "最新歌词" + i });
        check("render_burst_100_publications_one_pending_dispatch", window.SnapshotDispatches - dispatches == 1);
        await Task.Delay(80);
        check("render_burst_displays_latest_once", window.DisplayedLyrics == "最新歌词99"
            && window.SnapshotRenders - renders == 1 && window.TextUpdates - writes == 9);

        renders = window.SnapshotRenders; writes = window.TextUpdates;
        snapshot = hub.Current;
        for (int i = 0; i < 100; i++) hub.Publish(snapshot with { PositionSeconds = i });
        await Task.Delay(80);
        check("render_clock_only_zero_text_or_scroll_updates", window.SnapshotRenders == renders && window.TextUpdates == writes);

        await Task.Run(() => hub.Publish(snapshot with { CurrentLine = "工作线程最新歌词", State = PlaybackStatus.Paused }));
        await Task.Delay(80);
        check("render_worker_publication_marshals_latest_state", window.DisplayedLyrics == "工作线程最新歌词"
            && window.DisplayedStatus.Contains("已暂停"));

        window.HideLyrics();
        dispatches = window.SnapshotDispatches; renders = window.SnapshotRenders;
        hub.SetLyrics(generation, PlaybackLyricsKind.Plain, Lyrics.Parse("隐藏期间的纯文本"));
        hub.Publish(snapshot with { LyricsKind = PlaybackLyricsKind.Plain });
        window.SetAppTheme(ElementTheme.Light);
        window.ApplyOptions(window.Options with { Locked = true });
        await Task.Delay(80);
        check("render_hidden_settings_and_snapshot_skip_text", window.SnapshotDispatches == dispatches && window.SnapshotRenders == renders);
        window.ShowLyrics();
        check("render_reopen_catches_plain_lyrics_lock_and_theme", window.DisplayedLyrics == "隐藏期间的纯文本"
            && window.DisplayedStatus.Contains("已锁定") && window.EffectiveTheme == ElementTheme.Light
            && window.HasTransparentSurface && window.IsNativeInputPassThrough);
        hub.SetLyrics(generation, PlaybackLyricsKind.Plain, Lyrics.Parse("同一歌曲更新的纯文本"));
        hub.Publish(hub.Current);
        await Task.Delay(80);
        check("render_plain_document_change_not_lost", window.DisplayedLyrics == "同一歌曲更新的纯文本");
        window.Dispose();
        dispatches = window.SnapshotDispatches;
        hub.Publish(snapshot);
        await Task.Delay(80);
        check("render_disposed_window_enqueues_nothing", window.SnapshotDispatches == dispatches);
    }
}

public sealed partial class MainWindow
{
    private void VerifyPlaybackTimeEfficiency(Action<string, bool> check)
    {
        string oldTime = PlaybackTime.Text, oldDuration = PlaybackDuration.Text;
        int timeWrites = 0, durationWrites = 0;
        long timeToken = PlaybackTime.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => timeWrites++);
        long durationToken = PlaybackDuration.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => durationWrites++);
        try
        {
            UpdatePlaybackTimes(TimeSpan.FromSeconds(12.1), TimeSpan.FromSeconds(120));
            timeWrites = durationWrites = 0;
            for (int i = 0; i < 100; i++) UpdatePlaybackTimes(TimeSpan.FromSeconds(12.8), TimeSpan.FromSeconds(120));
            check("render_same_second_100_updates_zero_time_writes", timeWrites == 0 && durationWrites == 0);
            UpdatePlaybackTimes(TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(120));
            check("render_next_second_updates_only_position", timeWrites == 1 && durationWrites == 0 && PlaybackTime.Text == "00:13");
            UpdatePlaybackTimes(TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(121));
            check("render_duration_change_updates_only_duration", timeWrites == 1 && durationWrites == 1 && PlaybackDuration.Text == "02:01");
        }
        finally
        {
            PlaybackTime.UnregisterPropertyChangedCallback(TextBlock.TextProperty, timeToken);
            PlaybackDuration.UnregisterPropertyChangedCallback(TextBlock.TextProperty, durationToken);
            PlaybackTime.Text = oldTime; PlaybackDuration.Text = oldDuration;
        }
    }
}
#endif
