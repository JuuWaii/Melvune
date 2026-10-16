#if DEBUG
using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async Task VerifyTopNavigationAsync(Action<string, bool> verify)
    {
        var size = AppWindow.Size; var theme = Root.RequestedTheme;
        var audio = music.Player.Source; var playing = queue.Current?.Reference;
        try
        {
            await ShowHomeAsync(); Root.UpdateLayout();
            verify("top_brand_replaces_sidebar_brand", TitleBrandName.IsLoaded && TitleBrandName.Text == "Melvune" && Title.StartsWith("Melvune", StringComparison.Ordinal) && TitleBrandName.FontSize == 20 && TitleBrandIcon.Width == 28 && TitleBrandIcon.Height == 28
                && FindSongVisual<TextBlock>(SidebarHost, text => text.Text == "飞牛音乐") is null);
            verify("top_home_search_available_without_remote_and_back_disabled", !TopBack.IsEnabled && SearchAction.IsEnabled && SearchInput.IsEnabled);
            verify("welcome_copy_only_on_home", FindSongVisual<TextBlock>(HomePanel, text => text.Text == "音乐，陪伴每一个时刻") is not null
                && FindSongVisual<TextBlock>(LibraryPanel, text => text.Text == "音乐，陪伴每一个时刻" || text.Text == "浏览你的音乐资料库，或从本地文件夹开始聆听。") is null);
            foreach (int width in new[] { 1360, 980, 820 })
            {
                AppWindow.Resize(new global::Windows.Graphics.SizeInt32(width, 680)); await Task.Delay(100); WindowRoot.UpdateLayout();
                var bounds = new[] { TopBack, (FrameworkElement)SearchInput, SearchAction, ClearSearchAction }.Select(control =>
                {
                    var point = control.TransformToVisual(WindowRoot).TransformPoint(new global::Windows.Foundation.Point());
                    return new { X = point.X, Y = point.Y, Width = control.ActualWidth, Height = control.ActualHeight };
                }).ToArray();
                var settings = TitleBarSettings.TransformToVisual(WindowRoot).TransformPoint(new global::Windows.Foundation.Point());
                var brand = TitleBrand.TransformToVisual(WindowRoot).TransformPoint(new global::Windows.Foundation.Point());
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"top-navigation-{width}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { Width = WindowRoot.ActualWidth, AppTitleBar.ActualHeight, bounds, SettingsX = settings.X }));
                verify("top_controls_fit_caption_without_overlap_" + width, bounds.All(item => item.X >= 0 && item.Y >= 0 && item.Width > 0
                    && item.Y + item.Height <= AppTitleBar.ActualHeight + 1) && bounds[0].X >= brand.X + TitleBrand.ActualWidth
                    && bounds[^1].X + bounds[^1].Width <= settings.X && SearchInput.ActualWidth >= 64
                    && bounds.Zip(bounds.Skip(1)).All(pair => pair.First.X + pair.First.Width <= pair.Second.X));
                double captionGap = WindowRoot.ActualWidth - AppWindow.TitleBar.RightInset / WindowRoot.XamlRoot.RasterizationScale
                    - settings.X - TitleBarSettings.ActualWidth;
                verify("settings_adjacent_to_minimize_" + width, captionGap >= 0 && captionGap <= 10);
            }
            AppWindow.Resize(size); await Task.Delay(100);
            foreach (var mode in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                Root.RequestedTheme = mode; await Task.Delay(80); WindowRoot.UpdateLayout();
                await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"top-theme-{mode}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { Mode = mode.ToString(), RootTheme = Root.ActualTheme.ToString(),
                        TitleTheme = AppTitleBar.ActualTheme.ToString(), BrandTheme = TitleBrandName.ActualTheme.ToString(),
                        SearchTheme = SearchInput.ActualTheme.ToString(), BackTheme = TopBack.ActualTheme.ToString() }));
                verify("top_controls_follow_theme_" + mode, TitleBrandName.ActualTheme == mode && SearchInput.ActualTheme == mode && TopBack.ActualTheme == mode);
            }
            SearchInput.Text = "same"; InvokeSettingsButton(SearchAction); await Task.Delay(100); await WaitAllMusicViewAsync();
            verify("top_home_search_enters_all_music_with_query", LibraryPanel.Visibility == Visibility.Visible && allMusicView && browseScope == BrowseScope.All
                && searchQuery == "same" && total > 0 && TrackList.Items.Cast<MusicTrack>().All(track => track.Title.Contains("same", StringComparison.OrdinalIgnoreCase)));
            InvokeSettingsButton(TopBack); await Task.Delay(100); await WaitAllMusicViewAsync();
            verify("top_back_clears_search_before_leaving_browse", LibraryPanel.Visibility == Visibility.Visible && searchQuery.Length == 0 && SearchInput.Text.Length == 0);
            InvokeSettingsButton(TopBack); await Task.Delay(100);
            verify("top_back_from_browse_returns_home", HomePanel.Visibility == Visibility.Visible && !TopBack.IsEnabled);
            await ShowSettingsPanelAsync(); SearchInput.Text = "无匹配音乐"; await SearchFromTopAsync();
            verify("top_settings_search_enters_all_music", allMusicView && searchQuery == "无匹配音乐" && LibraryPanel.Visibility == Visibility.Visible && total == 0);
            await ShowPlaylistManagerAsync(false); var manager = activePlaylistPage!;
            verify("top_playlist_has_one_back_and_keeps_own_filter", TopBack.IsEnabled && manager.BackButton.Visibility == Visibility.Collapsed
                && manager.Fields.Children.OfType<TextBox>().Single().PlaceholderText == "筛选歌单名称");
            manager.BlockNavigation = true; string oldQuery = searchQuery;
            SearchInput.Text = "保存时搜索"; await SearchFromTopAsync(); await ReturnFromTopAsync();
            verify("top_actions_cannot_leave_playlist_during_write", ReferenceEquals(activePlaylistPage, manager) && !manager.IsDisposed && searchQuery == oldQuery && manager.Status.Text.Contains("保存"));
            manager.BlockNavigation = false;
            ShowQueuePage(); var drawer = queuePage;
            verify("top_queue_disables_search", drawer is not null && !SearchAction.IsEnabled && !SearchInput.IsEnabled);
            await ReturnFromTopAsync();
            verify("top_back_closes_drawer_without_leaving_playlist", queuePage is null && ReferenceEquals(activePlaylistPage, manager) && SearchAction.IsEnabled);
            await OpenNowPlayingAsync();
            verify("top_lyrics_disables_search", !SearchAction.IsEnabled && TopBack.IsEnabled);
            await ReturnFromTopAsync();
            verify("top_back_closes_lyrics_to_same_playlist", LyricsPanel.Visibility == Visibility.Collapsed && ReferenceEquals(activePlaylistPage, manager) && SearchAction.IsEnabled);
            await VerifyPlaylistCreationDialogAsync(verify, () => CreateLocalPlaylistFromDialogAsync(), null, "top_modal_guard", 120, beforeSubmit: async () =>
            {
                await SearchFromTopAsync(); await ReturnFromTopAsync();
                InvokeSettingsButton(TitleBarSettings); await Task.Delay(80);
                verify("top_actions_do_not_navigate_under_modal_dialog", themedDialogOpen && ReferenceEquals(activePlaylistPage, manager) && !manager.IsDisposed);
            });
            var form = new PlaylistPageView("顶部返回夹具", new StackPanel(), new ListView(), new StackPanel(), new TextBlock(), localLifetime.Token);
            form.UseFormLayout(); MountFullPage(form);
            verify("top_full_page_disables_search", ReferenceEquals(fullPageView, form) && !SearchAction.IsEnabled);
            form.BlockNavigation = true; await ReturnFromTopAsync();
            verify("top_full_page_write_blocks_return", ReferenceEquals(fullPageView, form) && !form.IsDisposed);
            form.BlockNavigation = false; await ReturnFromTopAsync();
            verify("top_full_page_return_restores_underlying_manager", fullPageView is null && ReferenceEquals(activePlaylistPage, manager) && SearchInput.IsEnabled);
            InvokeSettingsButton(TopBack); await Task.Delay(100);
            verify("top_playlist_back_restores_browse_context", activePlaylistPage is null && allMusicView && searchQuery == oldQuery);
            await ShowLibraryAsync(); await SwitchCollectionAsync(CollectionKind.Album);
            await OpenCollectionAsync(((IEnumerable<MusicCollection>)CollectionList.ItemsSource!).First());
            InvokeSettingsButton(TopBack); await Task.Delay(100); await WaitAllMusicViewAsync();
            verify("top_album_detail_back_returns_to_collection_list", collectionKind == CollectionKind.Album && collection is null && CollectionList.Visibility == Visibility.Visible
                && CollectionBack.Visibility == Visibility.Collapsed);
            verify("top_navigation_keeps_player_and_queue", ReferenceEquals(audio, music.Player.Source) && playing == queue.Current?.Reference);
        }
        finally { CloseFullPage(force: true); CloseQueuePage(force: true); await CloseNowPlayingAsync(); ClosePlaylistPage(); Root.RequestedTheme = theme;
            AppWindow.Resize(size); await ShowHomeAsync(); }
    }
}
#endif
