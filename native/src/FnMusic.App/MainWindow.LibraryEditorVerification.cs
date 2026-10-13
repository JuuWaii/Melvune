#if DEBUG
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private static void InvokeLibraryCard(PlaylistPageView view, LibraryManagerRow row, string label)
    {
        view.List.ScrollIntoView(row); view.List.UpdateLayout();
        var container = view.List.ContainerFromItem(row) as ListViewItem ?? throw new InvalidOperationException();
        var button = FindSongVisual<Button>(container, b => AutomationProperties.GetName(b) == label) ?? throw new InvalidOperationException();
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
    private async Task WithLibraryEditorAsync(PlaylistPageView view, Action open, Func<LibraryEditorView, Task> configure, bool save, Func<Task>? afterSubmit = null, bool allowClosed = false)
    {
        ContentDialog? opened = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        libraryEditorOpened = async editor =>
        {
            opened = editor.Dialog;
            try
            {
                await Task.Delay(100); await configure(editor); await Task.Delay(50); Root.UpdateLayout();
                var button = FindSongVisual<Button>(editor.Dialog, b => Equals(b.Content, save ? "保存" : "取消")) ?? throw new InvalidOperationException();
                if (!button.IsEnabled) throw new InvalidOperationException("Library dialog action disabled.");
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); editor.Dialog.Hide(); }
        };
        try
        {
            open(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (afterSubmit is not null) await afterSubmit();
            if (allowClosed && view.IsDisposed) return;
            for (int i = 0; i < 200 && (themedDialogOpen || !view.Editor.IsEnabled); i++) await Task.Delay(50);
            if (themedDialogOpen || !view.Editor.IsEnabled) throw new TimeoutException();
        }
        finally { libraryEditorOpened = null; opened?.Hide(); }
    }
    private async Task WithLibraryDeleteAsync(PlaylistPageView view, LibraryManagerRow row, bool accept, Action<string, bool> verify)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ContentDialog? opened = null;
        libraryDeleteOpened = async dialog =>
        {
            opened = dialog;
            try
            {
                await Task.Delay(100);
                verify("library_delete_dialog_defaults_to_cancel_" + row.Key, dialog.DefaultButton == ContentDialogButton.Close && dialog.ActualTheme == Root.ActualTheme);
                var button = FindSongVisual<Button>(dialog, b => Equals(b.Content, accept ? "删除" : "取消"))!;
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); dialog.Hide(); }
        };
        try
        {
            InvokeLibraryCard(view, row, "删除音乐库"); await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 200 && (themedDialogOpen || !view.Editor.IsEnabled); i++) await Task.Delay(50);
            if (themedDialogOpen || !view.Editor.IsEnabled) throw new TimeoutException();
        }
        finally { libraryDeleteOpened = null; opened?.Hide(); }
    }
    private async Task VerifyLibraryEditorWindowAsync(Action<string, bool> verify, PlaylistPageView view, LocalLibrary selected, object? audio)
    {
        LibraryManagerRow Row() => view.List.Items.Cast<LibraryManagerRow>().Single(row => row.Local?.SourceId == selected.SourceId);
        Action Edit() => () => InvokeLibraryCard(view, Row(), "编辑音乐库");
        long revision = (await localLibraryStore.LoadAsync()).Revision;
        await WithLibraryEditorAsync(view, () => ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "新建音乐库")).GetPattern(PatternInterface.Invoke)).Invoke(), editor =>
        {
            verify("library_create_dialog_local_defaults_and_theme", editor.Kind.Items.Count == 4 && editor.Kind.SelectedIndex == 0
                && editor.LocalFields.Visibility == Visibility.Visible && editor.NasFields.Visibility == Visibility.Collapsed
                && editor.Dialog.ActualTheme == Root.ActualTheme && !editor.Dialog.IsPrimaryButtonEnabled);
            editor.LocalPath.Text = selected.Directory; editor.Name.Text = "未保存名称";
            editor.Kind.SelectedIndex = 2;
            verify("library_other_provider_is_explicit_future_option", editor.OtherFields.Visibility == Visibility.Visible
                && editor.LocalFields.Visibility == Visibility.Collapsed && !editor.Dialog.IsPrimaryButtonEnabled);
            editor.Kind.SelectedIndex = 1;
            verify("library_disconnected_nas_fields_are_read_only", editor.NasFields.Visibility == Visibility.Visible
                && editor.LocalFields.Visibility == Visibility.Collapsed && !editor.NasPath.IsEnabled && !editor.Dialog.IsPrimaryButtonEnabled);
            editor.Kind.SelectedIndex = 0;
            verify("library_type_switch_retains_local_draft", editor.LocalPath.Text == selected.Directory && editor.Name.Text == "未保存名称" && editor.Dialog.IsPrimaryButtonEnabled);
            return Task.CompletedTask;
        }, false);
        verify("library_create_cancel_has_no_write", (await localLibraryStore.LoadAsync()).Revision == revision && view.Fields.Visibility == Visibility.Collapsed);
        await WithLibraryEditorAsync(view, Edit(), async editor =>
        {
            verify("library_edit_dialog_selected_identity_and_type", !editor.Kind.IsEnabled && editor.Name.Text == selected.Name && editor.LocalPath.Text == selected.Directory);
            editor.Name.Text = " "; await Task.Delay(50); verify("library_edit_blank_name_disabled", !editor.Dialog.IsPrimaryButtonEnabled);
            editor.Name.Text = "弹窗改名"; editor.Group.Text = "弹窗分组";
        }, true);
        var renamed = await localLibraryStore.LoadAsync();
        verify("library_dialog_rename_group_persisted_with_audio", renamed.Libraries.Single(l => l.SourceId == selected.SourceId) is { Name: "弹窗改名", Group: "弹窗分组" }
            && ReferenceEquals(audio, music.Player.Source));
        await WithLibraryEditorAsync(view, Edit(), async editor =>
        {
            await localLibraryStore.SaveAsync(renamed with { Libraries = renamed.Libraries.Select(item => item.SourceId == selected.SourceId ? item with { Name = "外部更新" } : item).ToArray() });
            editor.Name.Text = "过期名称";
        }, true);
        verify("library_dialog_conflict_preserves_external_and_requires_refresh", (await localLibraryStore.LoadAsync()).Libraries.Single(l => l.SourceId == selected.SourceId).Name == "外部更新"
            && view.Status.Text.Contains("刷新") && !FindFullPageButton(view, "新建音乐库").IsEnabled);
        await ClickFullPageButtonAsync(view, "刷新音乐库");
        revision = (await localLibraryStore.LoadAsync()).Revision;
        await WithLibraryEditorAsync(view, Edit(), editor => { editor.LocalPath.Text = Path.Combine(AppContext.BaseDirectory, "missing-" + Guid.NewGuid().ToString("N")); return Task.CompletedTask; }, true);
        verify("library_dialog_missing_path_keeps_catalog_and_audio", (await localLibraryStore.LoadAsync()).Revision == revision
            && view.Status.Text.Contains("原音乐库保留") && ReferenceEquals(audio, music.Player.Source));
        string moved = Path.Combine(AppContext.BaseDirectory, "library-dialog-relocated"); Directory.CreateDirectory(moved);
        var originalFiles = Directory.GetFiles(selected.Directory);
        var hashes = originalFiles.ToDictionary(file => file, file => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        foreach (var file in originalFiles) File.Copy(file, Path.Combine(moved, Path.GetFileName(file)), true);
        var reference = localSources[selected.SourceId].Tracks.First().Reference;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        localPageMetadataReader = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new("取消夹具", "夹具", 60); };
        try
        {
            await WithLibraryEditorAsync(view, Edit(), editor => { editor.LocalPath.Text = moved; return Task.CompletedTask; }, true, async () =>
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                verify("library_dialog_relink_scan_cancel_visible_and_navigation_available", view.CancelButton.IsEnabled && view.CancelButton.Visibility == Visibility.Visible && !view.BlockNavigation && view.BackButton.IsEnabled && MetadataActivity.IsActive);
                ((IInvokeProvider)new ButtonAutomationPeer(view.CancelButton).GetPattern(PatternInterface.Invoke)).Invoke();
            });
            verify("library_dialog_relink_cancel_preserves_catalog", (await localLibraryStore.LoadAsync()).Revision == revision && view.Status.Text.Contains("取消") && ReferenceEquals(audio, music.Player.Source));
        }
        finally { localPageMetadataReader = null; }
        await WithLibraryEditorAsync(view, Edit(), editor => { editor.LocalPath.Text = moved; return Task.CompletedTask; }, true);
        verify("library_dialog_relink_preserves_source_and_track_reference", (await localLibraryStore.LoadAsync()).Libraries.Single(l => l.SourceId == selected.SourceId).Directory == moved
            && localSources[selected.SourceId].Tracks.Any(track => track.Reference == reference));
        verify("library_dialog_original_files_unchanged", hashes.All(item => File.Exists(item.Key) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(item.Key))) == item.Value));
        revision = (await localLibraryStore.LoadAsync()).Revision;
        await WithLibraryDeleteAsync(view, Row(), false, verify);
        verify("library_delete_cancel_preserves_catalog", (await localLibraryStore.LoadAsync()).Revision == revision);
        var playlist = await playlistStore.LoadAsync();
        await WithLibraryDeleteAsync(view, Row(), true, verify);
        verify("library_delete_confirm_removes_only_import_and_keeps_references", !(await localLibraryStore.LoadAsync()).Libraries.Any(l => l.SourceId == selected.SourceId)
            && (await playlistStore.LoadAsync()).Revision == playlist.Revision && File.Exists(Path.Combine(moved, Path.GetFileName(originalFiles[0]))));
        var remainingTrack = localSources.Values.SelectMany(source => source.Tracks).First();
        queue.Replace([remainingTrack], remainingTrack.Reference);
        await PlayTrackAsync(remainingTrack);
        for (int i = 0; i < 80 && !mediaReady; i++) await Task.Delay(100);
        var creationAudio = music.Player.Source;
        verify("library_create_fixture_plays_remaining_source", mediaReady && creationAudio is not null && queue.Current?.Reference == remainingTrack.Reference);
        string createdPath = Path.Combine(AppContext.BaseDirectory, "library-dialog-created"); Directory.CreateDirectory(createdPath);
        using (var wave = Services.PlaybackVerification.CreateWave())
        using (var file = File.Create(Path.Combine(createdPath, "new.wav"))) await wave.CopyToAsync(file);
        await WithLibraryEditorAsync(view, () => ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "新建音乐库")).GetPattern(PatternInterface.Invoke)).Invoke(), editor =>
        { editor.LocalPath.Text = createdPath; editor.Name.Text = "新建窗口库"; editor.Group.Text = "新建分组"; return Task.CompletedTask; }, true);
        var created = (await localLibraryStore.LoadAsync()).Libraries.Single(l => l.Directory == createdPath);
        verify("library_local_create_scans_persists_and_renders_card", created.Name == "新建窗口库" && created.Group == "新建分组"
            && localSources[created.SourceId].Tracks.Count == 1 && view.List.Items.Cast<LibraryManagerRow>().Any(row => row.Local?.SourceId == created.SourceId));
        verify("library_local_create_preserves_existing_source_and_playlist", (await playlistStore.LoadAsync()).Revision == playlist.Revision
            && mediaReady && ReferenceEquals(creationAudio, music.Player.Source) && queue.Current?.Reference == remainingTrack.Reference);
        await VerifyLibraryNasDialogAsync(verify, view);
    }
    private async Task VerifyLibraryNasDialogAsync(Action<string, bool> verify, PlaylistPageView view)
    {
        var handler = new SettingsFixtureHandler();
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), handler, Guid.NewGuid());
        settingsTestClient = api; ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
        try
        {
            await ClickFullPageButtonAsync(view, "刷新音乐库");
            verify("library_manager_combines_nas_and_local_cards", view.List.Items.Cast<LibraryManagerRow>().Any(row => row.Remote is not null)
                && view.List.Items.Cast<LibraryManagerRow>().Any(row => row.Local is not null));
            await WithLibraryEditorAsync(view, () => ((IInvokeProvider)new ButtonAutomationPeer(FindFullPageButton(view, "新建音乐库")).GetPattern(PatternInterface.Invoke)).Invoke(), editor =>
            {
                editor.Kind.SelectedIndex = 1; editor.NasPath.Text = "/music/new"; editor.Metadata.SelectedIndex = 2; editor.Lyric.IsChecked = true;
                verify("library_nas_create_switches_editable_fields", editor.NasFields.Visibility == Visibility.Visible && editor.LocalFields.Visibility == Visibility.Collapsed
                    && editor.NasPath.IsEnabled && editor.Dialog.IsPrimaryButtonEnabled); return Task.CompletedTask;
            }, true);
            verify("library_nas_create_verified_and_rendered", handler.Libraries["created"] == ("/music/new", true, "local_only")
                && view.List.Items.Cast<LibraryManagerRow>().Any(row => row.Remote?.Library.Id == "created"));
            LibraryManagerRow Row() => view.List.Items.Cast<LibraryManagerRow>().Single(row => row.Remote?.Library.Id == "created");
            await WithLibraryEditorAsync(view, () => InvokeLibraryCard(view, Row(), "编辑音乐库"), editor =>
            {
                verify("library_nas_edit_keeps_server_path_and_source", editor.Kind.SelectedIndex == 1 && !editor.Kind.IsEnabled && editor.NasPath.IsReadOnly && editor.NasPath.Text == "/music/new");
                editor.Metadata.SelectedIndex = 1; editor.Lyric.IsChecked = false; return Task.CompletedTask;
            }, true);
            verify("library_nas_edit_reads_back_server_options", handler.Libraries["created"] == ("/music/new", false, "local_preferred"));
            int writes = handler.Writes; handler.Admin = false;
            await ClickFullPageButtonAsync(view, "刷新音乐库");
            await WithLibraryEditorAsync(view, () => InvokeLibraryCard(view, Row(), "编辑音乐库"), editor =>
            { verify("library_nas_member_edit_and_delete_read_only", !editor.Dialog.IsPrimaryButtonEnabled && !editor.Metadata.IsEnabled && !Row().CanDelete); return Task.CompletedTask; }, false);
            verify("library_nas_member_has_no_write", handler.Writes == writes);
            handler.Admin = true; await ClickFullPageButtonAsync(view, "刷新音乐库");
            await WithLibraryDeleteAsync(view, Row(), true, verify);
            verify("library_nas_delete_confirmed_and_refreshed", !handler.Libraries.ContainsKey("created") && view.List.Items.Cast<LibraryManagerRow>().All(row => row.Remote?.Library.Id != "created"));
        }
        finally { ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); settingsTestClient = null; await ClickFullPageButtonAsync(view, "刷新音乐库"); }
    }
}
#endif
