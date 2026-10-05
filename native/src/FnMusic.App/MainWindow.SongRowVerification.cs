#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private static T? FindSongVisual<T>(DependencyObject? root, Func<T, bool>? match = null) where T : DependencyObject
    {
        if (root is T value && (match is null || match(value))) return value;
        if (root is null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindSongVisual(VisualTreeHelper.GetChild(root, i), match) is { } child) return child;
        return null;
    }

    private async Task VerifySharedSongRowAsync(Action<string, bool> verify, ListView list, string prefix, bool artwork)
    {
        verify(prefix + "_shared_song_template", ReferenceEquals(list.ItemTemplate, TrackList.ItemTemplate));
        Root.UpdateLayout(); list.ScrollIntoView(list.Items[0]); await Task.Delay(60);
        var row = (list.ContainerFromIndex(0) as ListViewItem)?.ContentTemplateRoot;
        var track = ContextTrack(list.Items[0])!;
        verify(prefix + "_rendered_metadata", FindSongVisual<TextBlock>(row, text => text.Text == track.Title) is not null
            && FindSongVisual<TextBlock>(row, text => text.Text == track.DisplayDuration) is not null);
        verify(prefix + "_row_context_preserves_source", ContextTrack(list.Items[0])?.Reference == track.Reference);
        if (artwork)
        {
            var image = FindSongVisual<Image>(row) ?? throw new InvalidOperationException();
            await LoadArtworkAsync(image);
            verify(prefix + "_rendered_cover", image.Source is not null);
        }
    }
}
#endif
