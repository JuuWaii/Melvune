using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? localLibraryWork, backgroundLocalScan;
    private Action? libraryBackgroundStateChanged;
    private PlaylistPageView? localLibraryWorkOwner;
    private int libraryReadWorkCount;
    private string metadataActivitySummary = "正在同步飞牛资料…";
    private string localActivitySummary = "正在加载本地音乐…";
    private void UpdateBackgroundActivity()
    {
        if (closed) return;
        bool remote = metadataWork is { IsCancellationRequested: false };
        bool local = localLibraryWork is { IsCancellationRequested: false } || localScan is { IsCancellationRequested: false }
            || localIndexWork is { IsCancellationRequested: false } || libraryReadWorkCount > 0;
        MetadataActivity.IsActive = remote || local;
        MetadataActivity.Visibility = remote || local ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(MetadataActivity, string.Join("\n", new[] { remote ? metadataActivitySummary : "", local ? localActivitySummary : "" }.Where(text => text.Length > 0)));
    }
    private void ReportLocalLoading(string text, bool background)
    {
        if (closed || (!background && localLibraryWork is { IsCancellationRequested: false })) return;
        localActivitySummary = LocalScanStatus.Text = text;
        if (background) libraryBackgroundStateChanged?.Invoke();
        UpdateBackgroundActivity();
    }
    private async Task RefreshVisibleLocalCatalogAsync()
    {
        if (closed || !(localView || allMusicView) || LibraryPanel.Visibility != Visibility.Visible
            || LyricsPanel.Visibility == Visibility.Visible || fullPageView is not null || activePlaylistPage is not null) return;
        await LoadPageAsync(page);
    }
}
