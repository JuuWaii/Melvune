using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private sealed class PlaylistPageView : IDisposable
    {
        public Grid Layout { get; } = new() { RowSpacing = 12 };
        public ContentControl Editor { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        public ScrollViewer Header { get; }
        public StackPanel Fields { get; }
        public StackPanel Actions { get; }
        public ListView List { get; }
        public TextBlock Status { get; }
        public Button PlayButton { get; } = new() { Content = "播放所选", IsEnabled = false };
        public Button BackButton { get; } = new() { Content = "返回" };
        public Button CancelButton { get; } = new() { Content = "取消扫描", Visibility = Visibility.Collapsed };
        public CancellationTokenSource Lifetime { get; }
        public Guid? RemoteSource { get; init; }
        public bool BlockNavigation { get; set; }
        public Action? Closing { get; set; }
        public Action? SourceChanged { get; set; }
        public Func<Task>? Reload { get; set; }
        public bool IsDisposed { get; private set; }
        public bool IsForm { get; private set; }
        public TextBlock TitleText { get; } = new() { FontSize = 24, FontWeight = global::Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        public Guid? LocalPlaylistId { get; set; }
        public PlaylistReference? RemotePlaylistReference { get; set; }
        public double HeaderHeightLimit { get; init; } = 260;
        private readonly Grid heading = new() { ColumnSpacing = 12 };
        private readonly ScrollViewer actionScroll;
        public PlaylistPageView(string title, StackPanel fields, ListView list, StackPanel actions, TextBlock status, CancellationToken lifetime)
        {
            Fields = fields; Actions = actions; List = list; Status = status;
            fields.MinWidth = 0;
            ToolTipService.SetToolTip(BackButton, "返回原页面");
            Lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                Layout.RowDefinitions.Add(new RowDefinition { Height = height });
            heading.Children.Add(BackButton);
            heading.Children.Add(CancelButton);
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            Grid.SetColumn(CancelButton, 1); Grid.SetColumn(TitleText, 2);
            TitleText.Text = title;
            heading.Children.Add(TitleText);
            Header = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, MaxHeight = 260 };
            Editor.Content = Header;
            list.Height = double.NaN; list.MinHeight = 64; list.VerticalAlignment = VerticalAlignment.Stretch;
            actions.Children.Insert(0, PlayButton);
            actionScroll = new ScrollViewer { Content = actions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            list.SetBinding(Control.IsEnabledProperty, new Binding { Source = Editor, Path = new PropertyPath(nameof(Control.IsEnabled)) });
            actionScroll.SetBinding(Control.IsEnabledProperty, new Binding { Source = Editor, Path = new PropertyPath(nameof(Control.IsEnabled)) });
            status.MinHeight = 20; status.MaxLines = 3;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            var rows = new FrameworkElement[] { heading, Editor, list, actionScroll, status };
            for (int i = 0; i < rows.Length; i++) { Grid.SetRow(rows[i], i); Layout.Children.Add(rows[i]); }
            Layout.SizeChanged += (_, args) => Adapt(args.NewSize.Height + 48);
            heading.SizeChanged += (_, _) => Adapt(Layout.ActualHeight + 48);
            actionScroll.SizeChanged += (_, _) => Adapt(Layout.ActualHeight + 48);
            status.SizeChanged += (_, _) => Adapt(Layout.ActualHeight + 48);
        }
        public void Adapt(double availableHeight)
        {
            double height = Layout.ActualHeight > 0 ? Layout.ActualHeight : Math.Max(0, availableHeight - 48);
            Layout.RowSpacing = height < 380 ? 8 : 12;
            List.MinHeight = height < 380 ? 32 : 64;
            Status.MaxLines = height < 320 ? 2 : 3;
            if (IsForm) { Header.MaxHeight = double.PositiveInfinity; return; }
            Header.MaxHeight = Math.Clamp(height - heading.ActualHeight - actionScroll.ActualHeight
                - Status.ActualHeight - Layout.RowSpacing * 4 - List.MinHeight, 32, HeaderHeightLimit);
        }
        public void UseFormLayout()
        {
            IsForm = true;
            List.Visibility = Visibility.Collapsed; List.MinHeight = 0;
            PlayButton.Visibility = Visibility.Collapsed;
            Grid.SetRow(Editor, 2); Header.MaxHeight = double.PositiveInfinity;
        }
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true; Lifetime.Cancel(); Closing?.Invoke(); Lifetime.Dispose();
        }
    }

    private PlaylistPageView? activePlaylistPage;
    private FrameworkElement? playlistReturnPanel;
    private Control? playlistReturnFocus;
    private ScrollViewer? playlistReturnScroll;
    private ListView? playlistReturnList;
    private object? playlistReturnItems;
    private double playlistReturnHorizontal, playlistReturnVertical;
    private int contentNavigationVersion;

    private async Task<bool> NavigateMainContentAsync(FrameworkElement target)
    {
        if (closed) return false;
        if (!CloseFullPage()) return false;
        if (!CloseQueuePage()) return false;
        CloseTrackContextMenu();
        if (activePlaylistPage is { BlockNavigation: true } busy)
        { busy.Status.Text = "正在保存，请等待核对完成后切换页面。"; return false; }
        int version = ++contentNavigationVersion;
        await CloseNowPlayingAsync();
        if (closed || version != contentNavigationVersion) return false;
        ClosePlaylistPage();
        foreach (var panel in new FrameworkElement[] { LibraryPanel, SettingsPanel, HomePanel, PlaylistPanel })
            panel.Visibility = ReferenceEquals(panel, target) ? Visibility.Visible : Visibility.Collapsed;
        AnimateViewEntry(target);
        return true;
    }
    private void ClosePlaylistPage()
    {
        var old = activePlaylistPage;
        activePlaylistPage = null;
        old?.Dispose();
        PlaylistPanel.Content = null;
        PlaylistPanel.Visibility = Visibility.Collapsed;
    }
    private async Task<bool> MountPlaylistPageAsync(PlaylistPageView view)
    {
        FrameworkElement? previous = activePlaylistPage is not null ? playlistReturnPanel
            : HomePanel.Visibility == Visibility.Visible ? HomePanel
            : SettingsPanel.Visibility == Visibility.Visible ? SettingsPanel : LibraryPanel;
        var focus = activePlaylistPage is not null ? playlistReturnFocus : FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var list = activePlaylistPage is not null ? playlistReturnList
            : LibraryPanel.Visibility == Visibility.Visible ? CollectionList.Visibility == Visibility.Visible ? CollectionList : TrackList : null;
        var scroll = activePlaylistPage is not null ? playlistReturnScroll
            : list is not null ? FindPresentationScroll(list) : previous is ScrollViewer viewer ? viewer : null;
        double horizontal = activePlaylistPage is not null ? playlistReturnHorizontal : scroll?.HorizontalOffset ?? 0;
        double vertical = activePlaylistPage is not null ? playlistReturnVertical : scroll?.VerticalOffset ?? 0;
        var items = activePlaylistPage is not null ? playlistReturnItems : list?.ItemsSource;
        if (!await NavigateMainContentAsync(PlaylistPanel)) { view.Dispose(); return false; }
        playlistReturnPanel = previous; playlistReturnFocus = focus; playlistReturnList = list;
        playlistReturnScroll = scroll; playlistReturnItems = items;
        playlistReturnHorizontal = horizontal; playlistReturnVertical = vertical;
        activePlaylistPage = view;
        view.BackButton.Click += async (_, _) => await ReturnFromPlaylistPageAsync();
        PlaylistPanel.Content = view.Layout;
        view.Adapt(Root.ActualHeight - PlayerBar.ActualHeight);
        Root.UpdateLayout(); view.BackButton.Focus(FocusState.Programmatic);
        return true;
    }
    private async Task ReturnFromPlaylistPageAsync()
    {
        var previous = playlistReturnPanel ?? HomePanel;
        var focus = playlistReturnFocus;
        var scroll = playlistReturnScroll; var list = playlistReturnList; var items = playlistReturnItems;
        double horizontal = playlistReturnHorizontal, vertical = playlistReturnVertical;
        if (!await NavigateMainContentAsync(previous)) return;
        Root.UpdateLayout();
        if (scroll is not null && (list is null || ReferenceEquals(items, list.ItemsSource))) scroll.ChangeView(horizontal, vertical, null, true);
        if (focus is { IsLoaded: true, IsEnabled: true }) focus.Focus(FocusState.Programmatic);
        else NowCoverButton.Focus(FocusState.Programmatic);
    }
    private async void Home_Click(object sender, RoutedEventArgs e) => await ShowHomeAsync();
    private async Task ShowHomeAsync() { await NavigateMainContentAsync(HomePanel); }
}
