using Melvune.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private void UpdateTopNavigationLayout()
    {
        if (WindowRoot.ActualWidth <= 0 || closed) return;
        TitleBrand.Width = WindowRoot.ActualWidth < 1100 ? 160 : 196;
        double scale = WindowRoot.XamlRoot?.RasterizationScale ?? 1;
        TopNavigation.Width = Math.Clamp(WindowRoot.ActualWidth - TitleBrand.Width
            - AppWindow.TitleBar.RightInset / scale - 120, 190, 560);
    }
    private void UpdateTopNavigation()
    {
        if (closed) return;
        bool overlay = fullPageView is not null || queuePage is not null || LyricsPanel.Visibility == Visibility.Visible;
        TopBack.IsEnabled = overlay || activePlaylistPage is not null || LibraryPanel.Visibility == Visibility.Visible || SettingsPanel.Visibility == Visibility.Visible;
        bool browse = LibraryPanel.Visibility == Visibility.Visible;
        SearchInput.IsEnabled = SearchAction.IsEnabled = ClearSearchAction.IsEnabled = !overlay
            && (!browse || allMusicView || localView || RemoteSupports(MusicSourceCapabilities.Search));
        ToolTipService.SetToolTip(SearchAction, SearchAction.IsEnabled
            ? browse ? "搜索当前音乐范围" : "在所有音乐中搜索" : overlay ? "返回音乐页面后搜索" : "当前来源未连接或不支持搜索。");
        ToolTipService.SetToolTip(TopBack, activePlaylistPage is not null ? "返回上一级页面"
            : browse && (collection is not null || parentArtist is not null) ? "返回列表" : "返回");
        UpdateTopNavigationLayout();
    }
    private async void TopBack_Click(object sender, RoutedEventArgs e) => await ReturnFromTopAsync();
    private async Task ReturnFromTopAsync()
    {
        if (closed || themedDialogOpen) return;
        CloseTrackContextMenu();
        if (queuePage is not null) { await CloseQueuePageAsync(); return; }
        if (fullPageView is not null) { CloseFullPage(); return; }
        if (LyricsPanel.Visibility == Visibility.Visible) { await CloseNowPlayingAsync(); return; }
        if (activePlaylistPage is not null) { await ReturnFromPlaylistPageAsync(); return; }
        if (LibraryPanel.Visibility == Visibility.Visible && (collection is not null || parentArtist is not null))
        { await ReturnToCollectionListAsync(); return; }
        if (LibraryPanel.Visibility == Visibility.Visible && searchQuery.Length > 0) { await ClearTopSearchAsync(); return; }
        if (LibraryPanel.Visibility == Visibility.Visible || SettingsPanel.Visibility == Visibility.Visible) await ShowHomeAsync();
    }
    private bool TopSearchAvailable() => !closed && !themedDialogOpen && fullPageView is null
        && queuePage is null && LyricsPanel.Visibility != Visibility.Visible;
    private async Task SearchFromTopAsync()
    {
        if (!TopSearchAvailable()) return;
        if (LibraryPanel.Visibility == Visibility.Visible) await SearchAsync();
        else await ShowAllMusicAsync(SearchInput.Text.Trim());
    }
    private async Task ClearTopSearchAsync()
    {
        if (!TopSearchAvailable()) return;
        if (LibraryPanel.Visibility != Visibility.Visible) { await ShowAllMusicAsync(); return; }
        if (allMusicView || localView) { SearchInput.Text = searchQuery = ""; await LoadPageAsync(1); }
        else await SwitchCollectionAsync(null);
    }
}
