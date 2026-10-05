using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private PlaylistPageView? queuePage;
    private Button? queueRemoveButton, queueClearButton;
    private Control? queueReturnFocus;
    private Task? queueExit;
    private int queueTransitionVersion;

    private void ShowQueuePage()
    {
        if (closed || fullPageView is not null || !CloseQueuePage()) return;
        CloseTrackContextMenu();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, ItemTemplate = (DataTemplate)Root.Resources["QueueTrackTemplate"] };
        var itemStyle = new Style { TargetType = typeof(ListViewItem) };
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        list.ItemContainerStyle = itemStyle;
        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(new TextBlock { Text = "双击歌曲播放，右键打开更多操作。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var remove = new Button { Content = "移除所选" };
        var clear = new Button { Content = "清空队列" };
        ToolTipService.SetToolTip(remove, "移除当前歌曲会停止播放；移除其他歌曲保留当前播放。");
        ToolTipService.SetToolTip(clear, "清空播放队列并停止播放。");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(remove); actions.Children.Add(clear);
        var view = new PlaylistPageView("播放队列", fields, list, actions, new TextBlock { TextWrapping = TextWrapping.Wrap }, localLifetime.Token);
        view.BackButton.Content = "关闭";
        ToolTipService.SetToolTip(view.BackButton, "关闭播放队列 (Esc)");
        list.ContextRequested += TrackList_ContextRequested;
        list.ContextCanceled += TrackContext_Canceled;
        EnableSongDoubleTap(list);
        list.ContainerContentChanging += QueueItem_ContainerContentChanging;
        void UpdateButtons()
        {
            view.PlayButton.IsEnabled = list.SelectedItem is MusicTrack track && ContextPlayable(track);
            remove.IsEnabled = list.SelectedItem is MusicTrack;
            clear.IsEnabled = queue.Count > 0;
        }
        async Task Play(MusicTrack track)
        {
            if (ContextPlayable(track) && queue.Select(track.Reference) is { } selected) await PlayTrackAsync(selected);
        }
        list.SelectionChanged += (_, _) => UpdateButtons();
        view.PlayButton.Click += async (_, _) => { if (list.SelectedItem is MusicTrack track) await Play(track); };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not MusicTrack track) return;
            if (queue.Current?.Reference == track.Reference) Stop_Click(this, new RoutedEventArgs());
            queue.Remove(track.Reference); UpdateQueueStatus();
        };
        clear.Click += (_, _) => { Stop_Click(this, new RoutedEventArgs()); queue.Clear(); UpdateQueueStatus(); };
        view.SourceChanged = UpdateButtons;
        view.Reload = () => { RenderQueuePage(); return Task.CompletedTask; };
        view.Closing = () =>
        {
            if (ReferenceEquals(queuePage, view)) { queuePage = null; queueRemoveButton = queueClearButton = null; }
        };
        queueReturnFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        queuePage = view; queueRemoveButton = remove; queueClearButton = clear;
        QueuePaneHost.IsEnabled = true;
        QueuePaneHost.Content = view.Layout;
        QueueOverlay.Visibility = Visibility.Visible;
        view.BackButton.Click += async (_, _) => await CloseQueuePageAsync();
        Root.UpdateLayout(); UpdateQueuePaneLayout(); RenderQueuePage();
        ++queueTransitionVersion;
        _ = AnimateUiAsync(QueuePane, QueuePane.ActualWidth + 8, 0, 240, horizontal: true);
        view.BackButton.Focus(FocusState.Programmatic);
    }

    private void UpdateQueuePaneLayout()
    {
        if (closed || QueueOverlay.Visibility != Visibility.Visible) return;
        QueueOverlay.Margin = new Thickness(0, 0, 0, LyricsPanel.Visibility == Visibility.Visible ? 0 : PlayerBar.ActualHeight);
        QueuePane.Width = Math.Min(420, Math.Max(0, Root.ActualWidth - 16));
        queuePage?.Adapt(QueueOverlay.ActualHeight - 16);
    }

    private bool CloseQueuePage(bool force = false, bool restoreFocus = true)
    {
        if (queuePage is not { } view) return true;
        if (!force && view.BlockNavigation)
        { view.Status.Text = "正在保存，请等待核对完成后关闭队列。"; return false; }
        ++queueTransitionVersion;
        CloseTrackContextMenu();
        queueExit = null;
        if (uiMotions.TryGetValue(QueuePane, out var motion)) motion.Finish(true);
        queuePage = null; queueRemoveButton = queueClearButton = null;
        view.Dispose(); QueuePaneHost.Content = null; QueueOverlay.Visibility = Visibility.Collapsed;
        var focus = queueReturnFocus; queueReturnFocus = null;
        if (restoreFocus && !closed && focus is { IsLoaded: true, IsEnabled: true }) focus.Focus(FocusState.Programmatic);
        return true;
    }
    private Task CloseQueuePageAsync() => queueExit is { IsCompleted: false } pending ? pending : queueExit = CloseQueuePageCoreAsync();
    private async Task CloseQueuePageCoreAsync()
    {
        if (queuePage is not { } view || view.BlockNavigation) { CloseQueuePage(); return; }
        int version = queueTransitionVersion;
        CloseTrackContextMenu(); view.Editor.IsEnabled = false; QueuePaneHost.IsEnabled = false;
        double from = FindUiMotionTransform(QueuePane)?.TranslateX ?? 0;
        await AnimateUiAsync(QueuePane, from, QueuePane.ActualWidth + 8, 180, horizontal: true);
        if (ReferenceEquals(queuePage, view) && version == queueTransitionVersion) CloseQueuePage();
    }
    private async void QueueBackdrop_PointerPressed(object sender, PointerRoutedEventArgs args)
    { args.Handled = true; await CloseQueuePageAsync(); }

    private void RenderQueuePage()
    {
        if (queuePage is not { IsDisposed: false } view) return;
        var selected = (view.List.SelectedItem as MusicTrack)?.Reference ?? queue.Current?.Reference;
        view.List.ItemsSource = queue.Tracks.ToArray();
        view.List.SelectedItem = queue.Tracks.FirstOrDefault(track => track.Reference == selected);
        view.PlayButton.IsEnabled = view.List.SelectedItem is MusicTrack track && ContextPlayable(track);
        if (queueRemoveButton is { } remove) remove.IsEnabled = view.List.SelectedItem is MusicTrack;
        if (queueClearButton is { } clear) clear.IsEnabled = queue.Count > 0;
        view.Status.Text = queue.Count == 0 ? "队列为空，开始播放后在此查看歌曲。" : queue.Current is null
            ? $"共 {queue.Count} 首 · 尚未开始播放" : $"共 {queue.Count} 首 · 当前第 {queue.Index + 1} 首";
    }
    private void QueueItem_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Phase == 0) args.RegisterUpdateCallback(1, QueueItem_ContainerContentChanging);
        if (args.Item is MusicTrack track && args.ItemContainer.ContentTemplateRoot is FrameworkElement row
            && row.FindName("QueueCurrentMarker") is FontIcon marker)
            marker.Visibility = queue.Current?.Reference == track.Reference ? Visibility.Visible : Visibility.Collapsed;
    }
}
