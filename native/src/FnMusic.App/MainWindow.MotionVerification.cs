#if DEBUG
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task VerifyUiMotionWindowAsync()
    {
        var checks = new Dictionary<string, bool>();
        string stage = "setup", error = "";
        try
        {
            await VerifyUiMotionAsync((name, valid) =>
            { stage = name; checks[name] = valid; if (!valid) throw new InvalidOperationException(); });
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "ui-motion-verification.json"),
                JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, stage, error, checks, motionUnloadSamples }));
    }
    // Sample real Storyboard properties between frames, then check production navigation cleanup.
    private async Task VerifyUiMotionAsync(Action<string, bool> verify)
    {
        var samples = new Dictionary<string, double>();
        CompositeTransform? Transform(FrameworkElement element) => element.RenderTransform as CompositeTransform
            ?? (element.RenderTransform as TransformGroup)?.Children.LastOrDefault() as CompositeTransform;
        double Y(FrameworkElement element) => Transform(element)?.TranslateY ?? 0;
        bool Settled(FrameworkElement element) => Math.Abs(Y(element)) < .01 && element.Opacity == 1
            && Transform(element) is { ScaleX: 1, ScaleY: 1 } && !uiMotions.ContainsKey(element);
        async Task WaitForFrameAsync(Func<bool> sample)
        {
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Rendering(object? sender, object args) { if (sample()) observed.TrySetResult(); }
            CompositionTarget.Rendering += Rendering;
            try
            {
                if (sample()) return;
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { CompositionTarget.Rendering -= Rendering; }
        }
        try
        {
            motionPreferenceOverride = false;
            await CloseNowPlayingAsync();
            await ShowSettingsPanelAsync();
            StopAllUiMotions();
            var track = (await GetSyntheticPageAsync("", 1)).Tracks[0];
            queue.Replace([track], track.Reference);
            await PlayTrackAsync(track);
            for (int i = 0; i < 100 && !mediaReady; i++) await Task.Delay(20);
            verify("motion_fixture_has_live_audio", mediaReady && music.Player.Source is not null);
            music.Player.Pause();
            var size = AppWindow.Size;
            var audio = music.Player.Source;
            var current = queue.Current?.Reference;
            motionPreferenceOverride = true;
            double height = Root.ActualHeight;
            var enter = OpenNowPlayingAsync();
            samples["entry_start_y"] = Y(LyricsPanel);
            verify("motion_lyrics_starts_below_with_background_blocked", !enter.IsCompleted && Y(LyricsPanel) >= height - 1
                && SettingsPanel.Visibility == Visibility.Visible && SidebarHost.Visibility == Visibility.Visible && !NormalContent.IsEnabled);
            await WaitForFrameAsync(() => Y(LyricsPanel) > 0 && Y(LyricsPanel) < height - 1);
            samples["entry_middle_y"] = Y(LyricsPanel);
            verify("motion_lyrics_moves_up_between_frames", Y(LyricsPanel) > 0 && Y(LyricsPanel) < height - 1);
            await enter;
            verify("motion_lyrics_entry_settles_and_keeps_window_audio", Settled(LyricsPanel)
                && SettingsPanel.Visibility == Visibility.Collapsed && SidebarHost.Visibility == Visibility.Collapsed
                && AppWindow.Size.Width == size.Width && AppWindow.Size.Height == size.Height
                && ReferenceEquals(audio, music.Player.Source) && queue.Current?.Reference == current);
            var exit = CloseNowPlayingAsync();
            verify("motion_lyrics_exit_reveals_original_page_before_completion", !exit.IsCompleted
                && SettingsPanel.Visibility == Visibility.Visible && !NormalContent.IsEnabled && LyricsPanel.Visibility == Visibility.Visible);
            await WaitForFrameAsync(() => Y(LyricsPanel) > 0 && Y(LyricsPanel) < height);
            samples["exit_middle_y"] = Y(LyricsPanel);
            verify("motion_lyrics_exit_moves_down_between_frames", Y(LyricsPanel) > 0 && Y(LyricsPanel) < height);
            await exit;
            verify("motion_lyrics_exit_restores_controls_once", Settled(LyricsPanel) && NormalContent.IsEnabled
                && LyricsPanel.Visibility == Visibility.Collapsed && PlayerLayout.Children.Count(child => ReferenceEquals(child, TransportPanel)) == 1
                && FullTransportHost.Content is null && SettingsPanel.Visibility == Visibility.Visible);

            enter = OpenNowPlayingAsync();
            await Task.Delay(35);
            exit = CloseNowPlayingAsync();
            await Task.WhenAll(enter, exit);
            verify("motion_rapid_enter_exit_does_not_leave_overlay_or_controls", LyricsPanel.Visibility == Visibility.Collapsed
                && NormalContent.IsEnabled && FullTransportHost.Content is null && PlayerLayout.Children.Contains(TransportPanel)
                && SettingsPanel.Visibility == Visibility.Visible && !uiMotions.ContainsKey(LyricsPanel));

            enter = OpenNowPlayingAsync();
            await Task.Delay(35);
            exit = CloseNowPlayingAsync();
            var reenter = OpenNowPlayingAsync();
            await Task.WhenAll(enter, exit, reenter);
            verify("motion_reentry_waits_for_return_then_opens_once", LyricsPanel.Visibility == Visibility.Visible && Settled(LyricsPanel)
                && !NormalContent.IsEnabled && ReferenceEquals(FullTransportHost.Content, TransportPanel));
            await CloseNowPlayingAsync();

            var pageMotion = AnimateUiAsync(SettingsPanel, -18, 0, 180, fade: true);
            await WaitForFrameAsync(() => Y(SettingsPanel) > -18 && Y(SettingsPanel) < 0
                && SettingsPanel.Opacity > .25 && SettingsPanel.Opacity < 1);
            samples["page_middle_y"] = Y(SettingsPanel);
            samples["page_middle_opacity"] = SettingsPanel.Opacity;
            verify("motion_page_drops_and_fades_between_frames", Y(SettingsPanel) > -18 && Y(SettingsPanel) < 0
                && SettingsPanel.Opacity > .25 && SettingsPanel.Opacity < 1);
            var replacement = AnimateUiAsync(SettingsPanel, -18, 0, 180, fade: true);
            await Task.WhenAll(pageMotion, replacement);
            verify("motion_replaced_page_animation_settles", Settled(SettingsPanel));

            motionPreferenceOverride = false;
            enter = OpenNowPlayingAsync();
            verify("motion_disabled_lyrics_entry_immediate", enter.IsCompleted && Settled(LyricsPanel)
                && LyricsPanel.Visibility == Visibility.Visible);
            await enter;
            exit = CloseNowPlayingAsync();
            verify("motion_disabled_lyrics_exit_immediate", exit.IsCompleted && Settled(LyricsPanel)
                && LyricsPanel.Visibility == Visibility.Collapsed && NormalContent.IsEnabled);
            await exit;
            motionPreferenceOverride = true;
            pageMotion = AnimateUiAsync(SettingsPanel, -18, 0, 180, fade: true);
            await Task.Delay(35);
            motionPreferenceOverride = false;
            SystemMotionChanged(systemTheme, null!);
            await Task.Delay(25);
            verify("motion_preference_change_finishes_inflight_animation", pageMotion.IsCompleted && Settled(SettingsPanel));
            await pageMotion;

            motionPreferenceOverride = true;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "动画验证", Content = "隔离的合成窗口", CloseButtonText = "关闭" };
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Opened += (_, _) => opened.TrySetResult();
            var showing = ShowThemedDialogAsync(dialog);
            FrameworkElement? dialogSurface = null;
            try
            {
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForFrameAsync(() => (dialogSurface ??= FindDialogMotionSurface(dialog)) is { } frameSurface
                    && Transform(frameSurface) is { ScaleX: > .94 } transform && transform.ScaleX != 1
                    && Y(frameSurface) < 20 && frameSurface.Opacity > .25);
                dialogSurface = FindDialogMotionSurface(dialog) ?? throw new InvalidOperationException();
                var scale = Transform(dialogSurface)?.ScaleX ?? 1;
                samples["dialog_middle_scale"] = scale;
                samples["dialog_middle_y"] = Y(dialogSurface);
                samples["dialog_middle_opacity"] = dialogSurface.Opacity;
                verify("motion_dialog_bounces_from_smaller_size", scale > .94 && scale != 1
                    && Y(dialogSurface) < 20 && dialogSurface.Opacity > .25 && themedDialogOpen);
                await WaitForFrameAsync(() => Settled(dialogSurface));
                verify("motion_dialog_settles_without_changing_theme", Settled(dialogSurface) && dialog.RequestedTheme == Root.ActualTheme);
            }
            finally { dialog.Hide(); await showing; }
            verify("motion_closed_dialog_leaves_no_pending_animation", !themedDialogOpen && dialogSurface is not null && !uiMotions.ContainsKey(dialogSurface));
            var quickDialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "提前关闭验证", CloseButtonText = "关闭" };
            opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            quickDialog.Opened += (_, _) => opened.TrySetResult();
            showing = ShowThemedDialogAsync(quickDialog);
            FrameworkElement? quickSurface = null;
            string? quickDiagnostic = null;
            try
            {
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (int i = 0; i < 10; i++)
                {
                    quickSurface = FindDialogMotionSurface(quickDialog);
                    if (quickSurface is not null && uiMotions.ContainsKey(quickSurface)) break;
                    await Task.Delay(20);
                }
                // Keep network/disk I/O out of the short animation assertion and early-close window.
                quickDiagnostic = JsonSerializer.Serialize(new { SurfaceFound = quickSurface is not null, Loaded = quickSurface?.IsLoaded,
                        Visibility = quickSurface?.Visibility.ToString(), Scale = quickSurface is null ? null : Transform(quickSurface)?.ScaleX,
                        Y = quickSurface is null ? 0 : Y(quickSurface), TransformType = quickSurface?.RenderTransform?.GetType().Name,
                        Enabled = UiMotionEnabled, Closed = closed, ShowingCompleted = showing.IsCompleted,
                        Motions = uiMotions.Keys.Select(element => element.Name).ToArray() });
                verify("motion_quick_close_fixture_is_animating", quickSurface is not null && uiMotions.ContainsKey(quickSurface));
            }
            finally { quickDialog.Hide(); await showing; }
            verify("motion_early_dialog_close_cleans_up_animation", quickSurface is not null && Settled(quickSurface) && !themedDialogOpen);
            if (quickDiagnostic is not null)
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "motion-quick-dialog-diagnostic.json"), quickDiagnostic);
            motionPreferenceOverride = false;
            await ShowHomeAsync();
            var stillDialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "无动画验证", CloseButtonText = "关闭" };
            opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stillDialog.Opened += (_, _) => opened.TrySetResult();
            showing = ShowThemedDialogAsync(stillDialog);
            try
            {
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (int i = 0; i < 10 && (FindDialogMotionSurface(stillDialog) is not { } candidateSurface || !Settled(candidateSurface)); i++)
                    await Task.Delay(20);
                verify("motion_disabled_dialog_is_immediately_settled", FindDialogMotionSurface(stillDialog) is { } surface && Settled(surface));
                InvokeSettingsButton(TitleBarSettings); await Task.Delay(80);
                verify("modal_dialog_blocks_titlebar_settings_navigation", themedDialogOpen && HomePanel.Visibility == Visibility.Visible
                    && SettingsPanel.Visibility == Visibility.Collapsed);
            }
            finally { stillDialog.Hide(); await showing; }
            verify("motion_all_transitions_preserve_audio_and_queue", ReferenceEquals(audio, music.Player.Source)
                && queue.Current?.Reference == current && NormalContent.IsEnabled && uiMotions.Count == 0);
        }
        finally
        {
            motionPreferenceOverride = false;
            StopAllUiMotions();
            await CloseNowPlayingAsync();
            motionPreferenceOverride = null;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "motion-frame-samples.json"), JsonSerializer.Serialize(samples));
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "motion-unload-samples.json"), JsonSerializer.Serialize(motionUnloadSamples));
        }
    }
}
#endif
