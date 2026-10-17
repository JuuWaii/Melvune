#if DEBUG
using Melvune.App.Services;
using System.Text.Json;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private async Task VerifyMusicManagementWindowAsync()
    {
        var checks = new Dictionary<string, bool>(); string stage = "setup", error = "";
        void Check(string key, bool ok)
        {
            stage = key; checks[key] = ok;
            File.WriteAllText(Path.Combine(AppDataDirectory.Resolve(), "music-management-progress.json"), JsonSerializer.Serialize(new { stage, checks }));
            if (!ok) throw new InvalidOperationException(key);
        }
        async Task Wait(Func<bool> predicate)
        { for (int i = 0; i < 150 && !predicate(); i++) await Task.Delay(40); if (!predicate()) throw new TimeoutException(stage); }
        void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        var original = await playlistStore.LoadAsync(); bool originalShow = separateNasPlaylistsInSidebar;
        var audio = music.Player.Source; var queued = queue.Current?.Reference;
        var first = new LocalPlaylist(Guid.NewGuid(), "相同名字", []);
        var second = new LocalPlaylist(Guid.NewGuid(), "另一首歌单", []);
        using var api = new NasApiClient(ServerEndpoint.Parse("https://nas.example.invalid/"), new PlaylistFixtureHandler(), Guid.NewGuid());
        try
        {
            await ShowHomeAsync(); motionPreferenceOverride = false;
            Check("sidebar_default_merges_all_sources", !separateNasPlaylistsInSidebar);
            playlistTestClient = api; remoteCapabilityTestSource = api.SourceInstanceId; ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api));
            NasPlaylistNavigation.IsEnabled = true;
            var catalog = await playlistStore.SaveAsync(original with { Playlists = [first, second] });
            RenderLocalPlaylistNavigation(catalog); RenderNasPlaylistNavigation(api, await api.ListPlaylistsAsync(default));
            Check("sidebar_default_includes_nas_without_toggle", UnifiedPlaylistNavigation.Items.Count == 3
                && UnifiedPlaylistNavigation.Items.Cast<SidebarPlaylistRow>().Count(row => row.Value is NasPlaylist) == 1);
            await VerifyPlaylistGroupingLayoutAsync(Check);
            Check("sidebar_no_redundant_controls", RemotePlaylistsNavigation.ActualHeight == 0 && SidebarGroupedPlaylists.Visibility == Visibility.Collapsed
                && FindSongVisual<Button>(SidebarHost, button => Equals(button.Content, "导入文件夹") || Equals(button.Content, "设置")) is null);
            int openedPages = 0; musicManagementPageOpened = (_, _) => openedPages++;
            Invoke(MusicManagementNavigation); await Wait(() => fullPageView is { Editor.IsEnabled: true });
            var library = fullPageView!; var tabs = library.Fields.Children.OfType<SelectorBar>().Single();
            Check("management_library_tab_rendered", library.TitleText.Text == "音乐管理" && tabs.Items.Count == 2 && tabs.SelectedItem!.Text == "音乐库");
            library.BlockNavigation = true; tabs.SelectedItem = tabs.Items[1]; await Task.Delay(100);
            Check("management_busy_tab_cannot_abandon_write", ReferenceEquals(fullPageView, library) && tabs.SelectedItem.Text == "音乐库");
            library.BlockNavigation = false; tabs.SelectedItem = tabs.Items[1];
            await Wait(() => fullPageView is { Editor.IsEnabled: true } view && !ReferenceEquals(view, library));
            var view = fullPageView!;
            Check("management_unified_playlist_tab", view.TitleText.Text == "音乐管理" && view.List.Items.Count == 3 && library.IsDisposed
                && view.Fields.Children.OfType<SelectorBar>().Single().SelectedItem!.Text == "歌单");
            var visibility = view.Fields.Children.OfType<ToggleSwitch>().Single(); visibility.IsOn = true; await Task.Delay(60);
            Check("sidebar_toggle_separates_sources", UnifiedPlaylistNavigation.Items.Count == 3 && separateNasPlaylistsInSidebar
                && UnifiedPlaylistNavigation.Visibility == Visibility.Collapsed && GroupedNasPlaylistNavigation.Items.Count == 1
                && GroupedOtherPlaylistNavigation.Items.Count == 2 && SeparateNasPlaylistsToggle.IsOn);
            Check("sidebar_preference_persisted", JsonSerializer.Deserialize<PlaylistNavigationPreferences>(File.ReadAllText(PlaylistNavigationPreferencesFile))!.SeparateNasPlaylists);
            Check("sidebar_nas_badge_has_local_theme_container", UnifiedPlaylistNavigation.Items.Cast<SidebarPlaylistRow>().Single(row => row.Value is NasPlaylist).NasVisibility == Visibility.Visible
                && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "FnOsMusic.png")));
            visibility.IsOn = false; await Task.Delay(60);
            Check("sidebar_toggle_off_keeps_remote_in_merged_list", UnifiedPlaylistNavigation.Visibility == Visibility.Visible
                && UnifiedPlaylistNavigation.Items.Count == 3 && !SeparateNasPlaylistsToggle.IsOn);
            var remoteRow = view.List.Items.Cast<PlaylistManagerRow>().Single(row => row.Value is NasPlaylist);
            view.List.ScrollIntoView(remoteRow); Root.UpdateLayout(); await Task.Delay(70);
            var remoteContainer = view.List.ContainerFromItem(remoteRow) as ListViewItem;
            Check("manager_nas_logo_rendered", remoteContainer is not null
                && FindSongVisual<Image>(remoteContainer, image => image.IsLoaded && image.ActualWidth > 0) is not null);
            var search = view.Fields.Children.OfType<TextBox>().Single(); search.Text = "不存在"; await Task.Delay(80);
            Check("management_search_empty_state", view.List.Items.Count == 0 && view.Status.Text.Contains("没有匹配")); search.Text = ""; await Task.Delay(80);
            var localRow = view.List.Items.Cast<PlaylistManagerRow>().Single(row => row.Value is LocalPlaylist local && local.Id == second.Id);
            await playlistManagerAction!(localRow, "open"); await Wait(() => activePlaylistPage?.LocalPlaylistId == second.Id);
            Check("management_detail_opens_correct_songlist", fullPageView is null && view.IsDisposed && activePlaylistPage!.TitleText.Text == second.Name);
            await ReturnFromPlaylistPageAsync(); await Wait(() => fullPageView is { Editor.IsEnabled: true }); view = fullPageView!;
            Check("management_detail_back_restores_selection", view.List.SelectedItem is PlaylistManagerRow { Value: LocalPlaylist selected } && selected.Id == second.Id);
            await VerifyPlaylistCreationDialogAsync(Check, () => playlistManagerAction!(null, "create-local"), "管理新增", "unified_create", 120);
            Check("management_create_refreshes_unified_and_sidebar", view.List.Items.Count == 4 && UnifiedPlaylistNavigation.Items.Count == 4);
            localRow = view.List.Items.Cast<PlaylistManagerRow>().Single(row => row.Name == "管理新增");
            await VerifyPlaylistCreationDialogAsync(Check, () => playlistManagerAction!(localRow, "rename"), "管理改名", "unified_rename", 120, primaryLabel: "保存");
            Check("management_rename_syncs_sidebar", UnifiedPlaylistNavigation.Items.Cast<SidebarPlaylistRow>().Any(row => row.Name == "管理改名"));
            localRow = view.List.Items.Cast<PlaylistManagerRow>().Single(row => row.Name == "管理改名");
            await VerifyPlaylistDeleteDialogAsync(() => playlistManagerAction!(localRow, "delete"), false);
            Check("management_delete_cancel_retains_rows", view.List.Items.Count == 4);
            await VerifyPlaylistDeleteDialogAsync(() => playlistManagerAction!(localRow, "delete"), true);
            Check("management_delete_confirm_syncs_sidebar", view.List.Items.Count == 3 && UnifiedPlaylistNavigation.Items.Count == 3);
            await VerifyPlaylistCreationDialogAsync(Check, () => playlistManagerAction!(null, "create-nas"), "同步新增", "unified_nas_create", 32);
            Check("management_remote_create_readback", view.List.Items.Cast<PlaylistManagerRow>().Any(row => row.Value is NasPlaylist && row.Name == "同步新增"));
            remoteRow = view.List.Items.Cast<PlaylistManagerRow>().Single(row => row.Value is NasPlaylist);
            await VerifyPlaylistCreationDialogAsync(Check, () => playlistManagerAction!(remoteRow, "rename"), "同步改名", "unified_nas_rename", 32, primaryLabel: "保存");
            Check("management_remote_rename_sidebar_readback", UnifiedPlaylistNavigation.Items.Cast<SidebarPlaylistRow>().Any(row => row.Name == "同步改名"));
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            ViewModel.PlaybackRouter.Register(new RemoteCapabilityFixture(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists)); view.SourceChanged!();
            remoteRow = view.List.Items.Cast<PlaylistManagerRow>().Single(row => row.Value is NasPlaylist);
            Check("management_readonly_source_keeps_local_write", !remoteRow.CanWrite && remoteRow.CanOpen && view.List.Items.Cast<PlaylistManagerRow>().Where(row => row.Value is LocalPlaylist).All(row => row.CanWrite));
            await playlistManagerAction!(remoteRow, "delete");
            Check("management_readonly_cannot_open_delete", !themedDialogOpen && view.List.Items.Count == 3);
            ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); ViewModel.PlaybackRouter.Register(new NasPlaybackSource(api)); view.SourceChanged!();
            remoteRow = view.List.Items.Cast<PlaylistManagerRow>().Single(row => row.Value is NasPlaylist);
            await VerifyPlaylistDeleteDialogAsync(() => playlistManagerAction!(remoteRow, "delete"), true);
            Check("management_remote_delete_confirm_readback", view.List.Items.Count == 2 && UnifiedPlaylistNavigation.Items.Count == 2);
            playlistTestClient = null; remoteCapabilityTestSource = null; ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId); view.SourceChanged!();
            await Wait(() => view.Editor.IsEnabled && view.List.Items.Count == 2);
            Check("management_remote_revoked_local_remains", view.List.Items.Cast<PlaylistManagerRow>().All(row => row.Value is LocalPlaylist && row.CanWrite));
            Check("management_no_remote_create_when_disconnected", !view.HeadingActions!.Children.OfType<Button>().Single(button => Equals(button.Tag, "create-nas")).IsEnabled);
            Check("management_page_opened_hooks", openedPages >= 3);
            Check("management_navigation_preserves_audio_queue", ReferenceEquals(music.Player.Source, audio) && queue.Current?.Reference == queued);
            await VerifyPlaylistWatermarkAsync(Check);
            CloseFullPage();
            Check("management_back_restores_original_home", HomePanel.Visibility == Visibility.Visible && activePlaylistPage is null && NormalContent.IsEnabled);
            await ShowSettingsPanelAsync(PlaybackCategory); Root.UpdateLayout();
            Check("settings_separate_control_and_readonly_purpose", SystemMediaEnabled.Header.ToString() == "SMTC 开关"
                && InformationEnabled.Header.ToString() == "允许本机应用读取播放信息与歌词"
                && FindSongVisual<TextBlock>(SettingsPlaybackSection, text => text.Text.Contains("与 Windows SMTC 媒体控制互相独立")) is not null);
            await VerifyParallelNativeMetadataAsync(Check);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            musicManagementPageOpened = null; CloseFullPage(force: true); ClosePlaylistPage(); playlistTestClient = null; remoteCapabilityTestSource = null; ViewModel.PlaybackRouter.Unregister(api.SourceInstanceId);
            var current = await playlistStore.LoadAsync(); await playlistStore.SaveAsync(current with { Playlists = original.Playlists });
            SetNasPlaylistSidebarGrouping(originalShow); motionPreferenceOverride = null;
        }
        Directory.CreateDirectory(AppDataDirectory.Resolve());
        await File.WriteAllTextAsync(Path.Combine(AppDataDirectory.Resolve(), "music-management-verification.json"), JsonSerializer.Serialize(new { stage, error, checks }));
    }
}
#endif
