using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private bool compactLayout;
    private void RevealBrowseContent()
    {
        if (!compactLayout) return;
        LyricsPanel.Visibility = Visibility.Collapsed;
        TrackList.IsEnabled = CollectionList.IsEnabled = true;
    }
    private void UpdateAdaptiveLayout(double width)
    {
        if (width <= 0 || closed) return;
        bool compact = width < 1100;
        compactLayout = compact;
        SidebarColumn.Width = new GridLength(compact ? 176 : 220);
        NowPlayingColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(270);
        Grid.SetColumnSpan(NowPlayingDetails, compact ? 2 : 1);
        Grid.SetRow(TransportPanel, compact ? 1 : 0);
        Grid.SetColumn(TransportPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(TransportPanel, compact ? 3 : 1);
        while (PlayerLayout.RowDefinitions.Count < 4) PlayerLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(PlaybackStatus, compact ? 2 : 1);
        Grid.SetRow(QueueStatus, compact ? 3 : 2);
        Grid.SetRow(OutputDeviceButton, compact ? 2 : 1);
        // Compact lyrics occupy the content area instead of squeezing two unreadable columns.
        Grid.SetColumn(LyricsPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(LyricsPanel, compact ? 2 : 1);
        Canvas.SetZIndex(LyricsPanel, 1);
        LyricsPanel.Width = compact ? double.NaN : 300;
        LyricsPanel.Margin = compact ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        bool covered = compact && LyricsPanel.Visibility == Visibility.Visible;
        TrackList.IsEnabled = CollectionList.IsEnabled = !covered;
    }
}
