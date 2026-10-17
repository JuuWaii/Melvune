using Melvune.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private void ConfigureSongList(ListView list)
    {
        list.ItemTemplate = (DataTemplate)Root.Resources["SongRowTemplate"];
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 64d));
        list.ItemContainerStyle = style;
        EnableSongDoubleTap(list);
    }

    private readonly Dictionary<Image, bool> songArtworkViewports = [];
    private void SongArtwork_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image image) songArtworkViewports.TryAdd(image, false);
    }
    private async void SongArtwork_ViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender is not Image image || closed) return;
        var viewport = args.EffectiveViewport;
        bool visible = viewport.Width > 0 && viewport.Height > 0 && viewport.X < image.ActualWidth
            && viewport.Y < image.ActualHeight && viewport.X + viewport.Width > 0 && viewport.Y + viewport.Height > 0;
        songArtworkViewports[image] = visible;
        if (visible && image.IsLoaded) await LoadArtworkAsync(image);
        else if (!visible) CancelSongArtwork(image);
    }
    private async void SongArtwork_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not Image image) return;
        CancelSongArtwork(image);
        if (image.IsLoaded && songArtworkViewports.GetValueOrDefault(image)) await LoadArtworkAsync(image);
    }
    private void SongArtwork_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image image) return;
        songArtworkViewports.Remove(image); CancelSongArtwork(image); Artwork_Unloaded(sender, args);
    }
    private static void CancelSongArtwork(Image image)
    {
        if (image.Tag is CancellationTokenSource pending) pending.Cancel();
        image.Source = null;
    }

    private void EnableSongDoubleTap(ListView list) => list.AddHandler(UIElement.DoubleTappedEvent,
        new DoubleTappedEventHandler(SongList_DoubleTapped), handledEventsToo: true);

    private async void SongList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (sender is not ListView list || closed || !list.IsEnabled) return;
        var item = ResolveContextItem(list, args.OriginalSource as DependencyObject, false);
        if (ContextTrack(item) is not { } track || !ContextPlayable(track)) return;
        args.Handled = true;
        list.SelectedItem = item;
        await RunContextCommandAsync(() => PlayContextTrackAsync(list, track));
    }
}
