using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private void UpdateAdaptiveLayout(double width)
    {
        if (width <= 0 || closed) return;
        UpdateTopNavigation();
        UpdateLibraryHeaderLayout();
        activePlaylistPage?.Adapt(Root.ActualHeight - PlayerBar.ActualHeight);
        bool compact = width < 1100;
        fullPageView?.Adapt(Root.ActualHeight);
        SidebarColumn.Width = new GridLength(compact ? 176 : 220);
        bool lyrics = LyricsPanel.Visibility == Visibility.Visible;
        bool tight = width < 760;
        VolumeSlider.Width = tight ? 64 : 88;
        TransportButtons.Spacing = tight ? 0 : 8;
        double side = Math.Clamp((width - 48) * .22, tight ? 172 : 196, 270);
        NowPlayingColumn.Width = PlayerRightColumn.Width = new GridLength(side);
        TransportLeftColumn.Width = lyrics ? new GridLength(0) : new GridLength(side);
        TransportRightColumn.Width = lyrics ? GridLength.Auto : new GridLength(side);
        Grid.SetColumnSpan(NowPlayingDetails, 1);
        Grid.SetRow(TransportPanel, 0); Grid.SetColumn(TransportPanel, 0); Grid.SetColumnSpan(TransportPanel, 3);
        Grid.SetColumn(PlayerProgress, lyrics ? 0 : 1);
        Grid.SetColumnSpan(PlayerProgress, lyrics ? 3 : 1);
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
