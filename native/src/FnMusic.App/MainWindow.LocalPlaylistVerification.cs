#if DEBUG
using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async Task VerifyLocalPlaylistWindowAsync(Action<string, bool> verify, MusicTrack track)
    {
        var available = LocalPlaylistEntry.FromTrack(track);
        var offline = LocalPlaylistEntry.FromTrack(track with { SourceInstanceId = Guid.NewGuid(), Title = "离线夹具" });
        var saved = await playlistStore.LoadAsync();
        await playlistStore.SaveAsync(saved with { Playlists = [new(Guid.NewGuid(), "窗口夹具", [available, offline])] });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playlistPageOpened = async view =>
        {
            try
            {
                var editor = view.Editor;
                await VerifyPlaylistPagePresentationAsync(verify, view, "playlist_page");
                var panel = view.Fields;
                verify("playlist_detail_has_no_catalog_form", !panel.Children.OfType<ComboBox>().Any() && !panel.Children.OfType<TextBox>().Any() && !panel.Children.OfType<CheckBox>().Any());
                var list = view.List;
                await VerifySharedSongRowAsync(verify, list, "playlist", artwork: true);
                var buttons = panel.Children.OfType<Button>().Concat(panel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>())).Concat(view.Actions.Children.OfType<Button>()).ToArray();
                async Task Click(string label)
                {
                    var button = buttons.Single(b => b.Content.ToString() == label);
                    if (!button.IsEnabled) throw new InvalidOperationException("Disabled fixture control");
                    ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Task.Delay(100);
                    for (int i = 0; i < 100 && !editor.IsEnabled; i++) await Task.Delay(50);
                    if (!editor.IsEnabled) throw new TimeoutException();
                }
                verify("playlist_offline_retained", list.Items.Count == 2 && ((PlaylistRow)list.Items[1]).Text.Contains("离线"));
                list.SelectedIndex = 1;
                verify("playlist_offline_play_disabled", !view.PlayButton.IsEnabled);
                await Click("上移");
                verify("playlist_move_persisted", (await playlistStore.LoadAsync()).Playlists[0].Entries[0].Id == offline.Id);
                await Click("下移");
                list.SelectedIndex = 0;
                await Click("移除歌曲");
                verify("playlist_remove_persisted", (await playlistStore.LoadAsync()).Playlists[0].Entries.Count == 1);
                await Click("将所选条目重新关联到列表歌曲");
                var relinked = (await playlistStore.LoadAsync()).Playlists[0].Entries.Single();
                verify("playlist_relink_keeps_entry_identity", relinked.Id == offline.Id && relinked.Track == available.Track && view.PlayButton.IsEnabled);
                verify("playlist_relink_keeps_library_membership", relinked.ToTrack().LibraryIds.SequenceEqual(track.LibraryIds));
                await VerifyPlaylistCreationDialogAsync(verify, () => RenameLocalPlaylistAsync((LocalPlaylist)LocalPlaylistNavigation.Items[0]), "已改名", "playlist_sidebar_rename", 120, primaryLabel: "保存");
                verify("playlist_rename_persisted", (await playlistStore.LoadAsync()).Playlists[0].Name == "已改名");
                verify("playlist_sidebar_rename_updated", LocalPlaylistNavigation.Items.Cast<LocalPlaylist>().Single().Name == "已改名");
                verify("playlist_sidebar_rename_updates_open_title", view.TitleText.Text == "已改名");
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { if (ReferenceEquals(activePlaylistPage, view)) await ReturnFromPlaylistPageAsync(); }
            done.TrySetResult();
        };
        try { await ShowLocalPlaylistsAsync(); await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { playlistPageOpened = null; }
        await VerifyPlaylistCreationDialogAsync(verify, () => ShowLocalPlaylistsAsync(createNew: true), "新建夹具", "playlist_sidebar_new", 120);
        var createdView = activePlaylistPage!;
        verify("playlist_create_selected", createdView.TitleText.Text == "新建夹具" && createdView.List.Items.Count == 0);
        var add = createdView.Fields.Children.OfType<Button>().Single();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(100);
        }
        verify("playlist_add_deduplicated", (await playlistStore.LoadAsync()).Playlists[1].Entries.Count == 1);
        var createdPlaylist = LocalPlaylistNavigation.Items.Cast<LocalPlaylist>().Single(p => p.Name == "新建夹具");
        await VerifyPlaylistDeleteDialogAsync(() => DeleteLocalPlaylistAsync(createdPlaylist), accept: false);
        verify("playlist_sidebar_cancel_delete_keeps_catalog", (await playlistStore.LoadAsync()).Playlists.Count == 2);
        await VerifyPlaylistDeleteDialogAsync(() => DeleteLocalPlaylistAsync(createdPlaylist), accept: true);
        verify("playlist_delete_only_catalog", (await playlistStore.LoadAsync()).Playlists.Count == 1 && PlaylistTrackAvailable(available));
        verify("playlist_sidebar_delete_updated", LocalPlaylistNavigation.Items.Count == 1 && createdView.TitleText.Text == "已改名");
        await ReturnFromPlaylistPageAsync();
        var expected = (await playlistStore.LoadAsync()).Playlists.Single();
        bool conflict = false;
        try
        {
            await VerifyPlaylistCreationDialogAsync(verify, () => RenameLocalPlaylistAsync(expected), "过期改名", "playlist_sidebar_stale_rename", 120, primaryLabel: "保存", beforeSubmit: async () =>
            {
                var external = await playlistStore.LoadAsync();
                await playlistStore.SaveAsync(external with { Playlists = external.Playlists.Select(p => p.Id == expected.Id ? p with { Name = "外部改名" } : p).ToArray() });
            });
        }
        catch (PlaylistConflictException) { conflict = true; }
        verify("playlist_sidebar_stale_rename_preserves_external_changes", conflict && (await playlistStore.LoadAsync()).Playlists.Single().Name == "外部改名" && !contextPlaylistWriteBusy && !themedDialogOpen);
        var final = (await playlistStore.LoadAsync()).Playlists.Single();
        await PlayLocalPlaylistAsync(final with { Entries = [available, offline] }, available);
        for (int i = 0; i < 80 && !mediaReady; i++) await Task.Delay(100);
        verify("playlist_play_skips_offline", queue.Count == 1 && queue.Current?.Reference == track.Reference && mediaReady);
        verify("playlist_play_restores_library_membership", queue.Current?.LibraryIds.SequenceEqual(track.LibraryIds) == true);
        verify("playlist_play_lyrics", currentLyrics.Lines.Count == 3);
        await LoadArtworkAsync(NowCover);
        verify("playlist_play_cover", NowCover.Source is not null);
        Stop_Click(this, new RoutedEventArgs());
    }
}
#endif
