using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private void UpdateAdaptiveLayout(double width)
    {
        if (width <= 0 || closed) return;
        UpdateLibraryHeaderLayout();
        activePlaylistPage?.Adapt(Root.ActualHeight - PlayerBar.ActualHeight);
        bool compact = width < 1100;
        fullPageView?.Adapt(Root.ActualHeight);
        SidebarColumn.Width = new GridLength(compact ? 176 : 220);
        NowPlayingColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(270);
        Grid.SetColumnSpan(NowPlayingDetails, compact ? 2 : 1);
        Grid.SetRow(TransportPanel, compact ? 1 : 0);
        Grid.SetColumn(TransportPanel, compact ? 0 : 1);
        Grid.SetColumnSpan(TransportPanel, compact ? 2 : 1);
        while (PlayerLayout.RowDefinitions.Count < 4) PlayerLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(PlaybackStatus, compact ? 2 : 1);
        Grid.SetRow(QueueStatus, compact ? 3 : 2);
        // Lyrics always own the page; short/narrow displays prioritize readable text.
        bool textOnly = width < 900 || Root.ActualHeight < 560;
        FullArtworkPanel.Visibility = textOnly ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(FullPlayerColumn, textOnly ? 2 : 1);
        Grid.SetRowSpan(FullPlayerColumn, textOnly ? 1 : 2);
        FullPlayerColumn.MaxWidth = textOnly ? double.PositiveInfinity : 480;
        FullPlayerColumn.HorizontalAlignment = textOnly ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        FullPlayerColumn.MaxHeight = textOnly ? Math.Max(140, Root.ActualHeight * .48) : Math.Max(200, Root.ActualHeight - 100);
        Grid.SetColumn(FullLyricsText, textOnly ? 0 : 1);
        Grid.SetRow(FullLyricsText, textOnly ? 1 : 0);
        Grid.SetRowSpan(FullLyricsText, textOnly ? 1 : 2);
        Grid.SetColumnSpan(FullLyricsText, textOnly ? 2 : 1);
        double artworkSize = Math.Clamp(Math.Min(width * .34, Root.ActualHeight - 460), 120, 480);
        FullArtworkPanel.Width = FullArtworkPanel.Height = artworkSize;
        UpdateQueuePaneLayout();
    }
    private void UpdateLibraryHeaderLayout()
    {
        if (Root.ActualHeight <= 0 || closed) return;
        LibraryHeaderScroll.MaxHeight = Math.Max(80, Root.ActualHeight - PlayerBar.ActualHeight - 48 - LibraryPanel.RowSpacing - 80);
    }
}
