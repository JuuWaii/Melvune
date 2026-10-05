using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private void ConfigureSongList(ListView list)
    {
        list.ItemTemplate = (DataTemplate)Root.Resources["SongRowTemplate"];
        var style = new Style { TargetType = typeof(ListViewItem) };
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = style;
        EnableSongDoubleTap(list);
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
