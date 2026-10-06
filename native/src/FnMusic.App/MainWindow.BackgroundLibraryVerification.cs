#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private async Task WaitBackgroundLibraryAsync()
    {
        for (int i = 0; i < 200 && localImportBusy; i++) await Task.Delay(50);
        if (localImportBusy) throw new TimeoutException();
        await Task.Delay(100); Root.UpdateLayout();
    }
    private async Task<PlaylistPageView> VerifyBackgroundLibraryWindowAsync(Action<string, bool> verify, PlaylistPageView view)
    {
        var catalog = await localLibraryStore.LoadAsync();
        var originalSources = localSources.Values.ToArray();
        var sources = localSources.ToDictionary(item => item.Key, item => item.Value);
        var audio = music.Player.Source;
        var current = queue.Current?.Reference;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken scanToken = default;
        localPageMetadataReader = async (_, ct) =>
        { scanToken = ct; entered.TrySetResult(); await release.Task.WaitAsync(ct); return new("后台刷新夹具", "夹具", 60); };
        try
        {
            ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "扫描音乐库")).GetPattern(PatternInterface.Invoke)).Invoke();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            verify("library_background_scan_has_activity_and_independent_lifetime", MetadataActivity.IsActive && localImportBusy && localLibraryWork is not null && !view.BlockNavigation);
            verify("library_background_scan_can_close_origin", CloseFullPage() && view.IsDisposed && !scanToken.IsCancellationRequested);
            await ShowHomeAsync();
            verify("library_background_scan_can_navigate_home", HomePanel.Visibility == Visibility.Visible && !scanToken.IsCancellationRequested && MetadataActivity.IsActive);
            await ShowLocalLibraryManagementAsync(); view = fullPageView ?? throw new InvalidOperationException();
            verify("library_background_manager_reopens_with_cancel_and_no_duplicate_write", view.CancelButton.Visibility == Visibility.Visible
                && view.CancelButton.IsEnabled && !FindFullPageButton(view, "新建音乐库").IsEnabled && !FindFullPageButton(view, "扫描音乐库").IsEnabled
                && view.List.Items.Cast<LibraryManagerRow>().All(row => !row.CanEdit && !row.CanDelete));
            double before = music.Player.PlaybackSession.Position.TotalSeconds;
            await Task.Delay(700);
            verify("library_background_scan_keeps_existing_playback_advancing", ReferenceEquals(audio, music.Player.Source) && queue.Current?.Reference == current
                && music.Player.PlaybackSession.Position.TotalSeconds > before + .1);
            release.TrySetResult(); await WaitBackgroundLibraryAsync();
            verify("library_background_completion_updates_reopened_manager", ReferenceEquals(fullPageView, view) && view.List.Items.Count == catalog.Libraries.Count
                && view.List.Items.Cast<LibraryManagerRow>().All(row => row.CanEdit && row.CanDelete) && !localImportBusy
                && sources.All(item => !ReferenceEquals(localSources[item.Key], item.Value)) && (await localLibraryStore.LoadAsync()).Revision == catalog.Revision);
            verify("library_background_completion_notifies_and_releases_activity", !MetadataActivity.IsActive && MetadataNotice.Visibility == Visibility.Visible && MetadataNotice.Text.Contains("扫描完成"));

            sources = localSources.ToDictionary(item => item.Key, item => item.Value);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "扫描音乐库")).GetPattern(PatternInterface.Invoke)).Invoke();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); verify("library_background_cancel_origin_can_close", CloseFullPage());
            await ShowHomeAsync();
            ((IInvokeProvider)new ButtonAutomationPeer(CancelLocalScan).GetPattern(PatternInterface.Invoke)).Invoke();
            await WaitBackgroundLibraryAsync();
            verify("library_background_cancel_from_home_keeps_catalog_and_sources", HomePanel.Visibility == Visibility.Visible && localLibraryWork is null
                && sources.All(item => ReferenceEquals(localSources[item.Key], item.Value)) && (await localLibraryStore.LoadAsync()).Revision == catalog.Revision
                && LocalScanStatus.Text.Contains("取消") && CancelLocalScan.Visibility == Visibility.Collapsed);

            await ShowLocalLibraryManagementAsync(); view = fullPageView ?? throw new InvalidOperationException();
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "扫描音乐库")).GetPattern(PatternInterface.Invoke)).Invoke();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); verify("library_background_conflict_origin_can_close", CloseFullPage());
            await ShowHomeAsync(); var external = await localLibraryStore.SaveAsync(catalog);
            release.TrySetResult(); await WaitBackgroundLibraryAsync();
            verify("library_background_late_conflict_does_not_overwrite_or_reopen_page", HomePanel.Visibility == Visibility.Visible && fullPageView is null
                && sources.All(item => ReferenceEquals(localSources[item.Key], item.Value)) && (await localLibraryStore.LoadAsync()).Revision == external.Revision
                && LocalScanStatus.Text.Contains("刷新"));

            await ShowLocalLibraryManagementAsync(); view = fullPageView ?? throw new InvalidOperationException();
            string directory = Path.Combine(AppContext.BaseDirectory, "background-library-created"); Directory.CreateDirectory(directory);
            using (var wave = Services.PlaybackVerification.CreateWave())
            using (var file = File.Create(Path.Combine(directory, "created.wav"))) await wave.CopyToAsync(file);
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithLibraryEditorAsync(view, () => ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "新建音乐库")).GetPattern(PatternInterface.Invoke)).Invoke(), editor =>
            { editor.LocalPath.Text = directory; editor.Name.Text = "后台导入夹具"; return Task.CompletedTask; }, true, async () =>
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                verify("library_background_create_can_leave_after_confirmed_settings", CloseFullPage() && !scanToken.IsCancellationRequested);
                await ShowHomeAsync(); release.TrySetResult();
            }, allowClosed: true);
            await WaitBackgroundLibraryAsync();
            var saved = await localLibraryStore.LoadAsync(); var created = saved.Libraries.Single(item => item.Directory == directory);
            verify("library_background_create_commits_and_registers_without_page_navigation", saved.Revision == external.Revision + 1
                && created.Name == "后台导入夹具" && localSources[created.SourceId].Tracks.Count == 1 && HomePanel.Visibility == Visibility.Visible && fullPageView is null);
        }
        finally
        {
            release.TrySetResult(); if (localImportBusy) { localLibraryWork?.Cancel(); await WaitBackgroundLibraryAsync(); }
            localPageMetadataReader = null;
            foreach (var source in originalSources) ReplaceLocalSource(source);
        }
        await ShowLocalLibraryManagementAsync(); return fullPageView ?? throw new InvalidOperationException();
    }
}
#endif
