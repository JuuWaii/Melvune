using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private sealed record PlaylistManagerRow(object Value, bool CanOpen, bool CanWrite)
    {
        public override string ToString() => Name;
        public Visibility NasVisibility => Value is NasPlaylist ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LocalVisibility => Value is LocalPlaylist ? Visibility.Visible : Visibility.Collapsed;
        public string Key => Value is LocalPlaylist local ? "local:" + local.Id : "nas:" + ((NasPlaylist)Value).Reference.SourceInstanceId + ":" + ((NasPlaylist)Value).Reference.PlaylistId;
        public string Name => Value is LocalPlaylist local ? local.Name : ((NasPlaylist)Value).Name;
        public string Description => Value is LocalPlaylist local ? $"{local.Entries.Count} 首 · 保存在本机 · 可跨音乐库"
            : "同步当前飞牛账号 · 打开查看歌曲";
    }
    private Func<PlaylistManagerRow?, string, Task>? playlistManagerAction;
#if DEBUG
    private Action<PlaylistPageView>? playlistManagerOpened;
#endif
    private async void PlaylistManagerAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlaylistManagerRow row, Tag: string action } && playlistManagerAction is { } run)
            await run(row, action);
    }
    private async Task ShowPlaylistManagerAsync(bool remote, NasApiClient? api = null, string? selectedKey = null)
    {
        bool CanReadRemote() => api is not null && IsCurrentPlaylistClient(api)
            && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists);
        bool CanWrite() => !remote || CanReadRemote() && ViewModel.PlaybackRouter.Supports(api!.SourceInstanceId, MusicSourceCapabilities.WritePlaylists);
        if (closed || (remote && !CanReadRemote())) return;
        bool busy = false, loaded = false;
        var rows = new List<PlaylistManagerRow>();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = (DataTemplate)Root.Resources["PlaylistManagerCardTemplate"] };
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); list.ItemContainerStyle = itemStyle;
        global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, remote ? "飞牛歌单管理列表" : "本地歌单管理列表");
        var search = new TextBox { PlaceholderText = "筛选歌单名称", MaxLength = 120, Header = "查找歌单" };
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(new TextBlock { Text = remote ? "此处管理当前飞牛账号的同步歌单；网页修改后刷新。" : "此处管理保存在本机的跨音乐库歌单；删除歌单保留音乐文件。", TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(search);
        var createContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        createContent.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 14 });
        createContent.Children.Add(new TextBlock { Text = "新建歌单" });
        var create = new Button { Tag = "create", Content = createContent };
        global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(create, "新建歌单");
        var refresh = new Button { Content = "刷新歌单" };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; heading.Children.Add(create);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(refresh);
        var view = new PlaylistPageView(remote ? "飞牛歌单管理" : "本地歌单管理", fields, list, actions,
            new TextBlock { Text = "正在读取歌单…", TextWrapping = TextWrapping.Wrap }, localLifetime.Token)
            { RemoteSource = remote ? api!.SourceInstanceId : null };
        view.PlayButton.Visibility = Visibility.Collapsed; view.SetHeadingActions(heading);
        var ct = view.Lifetime.Token;
        string? selection = selectedKey;
        bool Current() => !closed && !ct.IsCancellationRequested && ReferenceEquals(activePlaylistPage, view);
        void Render()
        {
            rows = rows.Select(row => row with { CanOpen = loaded && (!remote || CanReadRemote()), CanWrite = loaded && CanWrite() }).ToList();
            var filtered = rows.Where(row => row.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
            list.ItemsSource = filtered; list.SelectedItem = filtered.FirstOrDefault(row => row.Key == selection);
            create.IsEnabled = loaded && CanWrite();
            view.Status.Text = !loaded ? "读取失败，请刷新重试；现有歌单保持。" : rows.Count == 0 ? "暂无歌单，点击右上方新建。"
                : filtered.Length == 0 ? "没有匹配的歌单。" : $"{rows.Count} 个歌单 · 当前显示 {filtered.Length} 个。";
        }
        async Task Reload(bool cached = false)
        {
            if (busy || !Current()) return;
            busy = true; view.Editor.IsEnabled = false; view.Status.Text = "正在读取歌单…";
            try
            {
                if (remote)
                {
                    if (!CanReadRemote()) throw new MusicApiException(MusicFailure.Unavailable);
                    var data = cached ? CurrentMetadata(api)?.Playlists.Select(item => item.Playlist).ToArray() ?? await api!.ListPlaylistsAsync(ct) : await api!.ListPlaylistsAsync(ct); ct.ThrowIfCancellationRequested();
                    if (!Current() || !CanReadRemote()) return;
                    rows = data.Select(item => new PlaylistManagerRow(item, true, CanWrite())).ToList();
                    RenderNasPlaylistNavigation(api!, data);
                }
                else
                {
                    var data = await playlistStore.LoadAsync(ct); ct.ThrowIfCancellationRequested(); if (!Current()) return;
                    rows = data.Playlists.Select(item => new PlaylistManagerRow(item, true, true)).ToList(); RenderLocalPlaylistNavigation(data);
                }
                loaded = true; Render();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
            {
                if (ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
                if (Current()) { loaded = false; Render(); }
            }
            catch (Exception) { if (Current()) { loaded = false; Render(); } }
            finally { busy = false; if (!view.IsDisposed) view.Editor.IsEnabled = true; }
        }
        async Task Execute(PlaylistManagerRow? row, string action)
        {
            if (!Current() || busy || !loaded || !CanOpenPlaylistNameDialog() || (row is not null && !rows.Contains(row))) return;
            if ((action == "open" && row is not { CanOpen: true }) || (action != "open" && !CanWrite())) return;
            try
            {
                if (action == "open")
                {
                    selection = row!.Key;
                    if (row.Value is LocalPlaylist local) await ShowLocalPlaylistsAsync(local.Id);
                    else await ShowNasPlaylistsAsync(api!, ((NasPlaylist)row.Value).Reference);
                    if (activePlaylistPage is { } detail && !ReferenceEquals(detail, view) && !detail.IsDisposed)
                        detail.ReturnNavigation = () => ShowPlaylistManagerAsync(remote, api, row.Key);
                    return;
                }
                if (action == "create")
                {
                    object? created = remote ? await CreateNasPlaylistFromDialogAsync(api!) : await CreateLocalPlaylistFromDialogAsync();
                    if (!Current() || created is null) return;
                    selection = created is Guid id ? "local:" + id : "nas:" + ((PlaylistReference)created).SourceInstanceId + ":" + ((PlaylistReference)created).PlaylistId;
                    Render(); return;
                }
                selection = row!.Key;
                var previousPlaybackStatus = PlaybackStatus.Text;
                if (row.Value is LocalPlaylist selected)
                {
                    if (action == "rename") await RenameLocalPlaylistAsync(selected);
                    if (action == "delete") await DeleteLocalPlaylistAsync(selected);
                }
                else if (row.Value is NasPlaylist selectedRemote)
                {
                    if (action == "rename") await RenameNasPlaylistAsync(api!, selectedRemote);
                    if (action == "delete") await DeleteNasPlaylistAsync(api!, selectedRemote);
                }
                if (Current())
                {
                    if (PlaybackStatus.Text == previousPlaybackStatus) Render();
                    else view.Status.Text = PlaybackStatus.Text;
                }
            }
            catch (Exception) { if (Current()) view.Status.Text = "操作未完成，请刷新核对歌单后重试。"; }
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is PlaylistManagerRow row) selection = row.Key; };
        list.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler(async (_, args) =>
        {
            for (var origin = args.OriginalSource as DependencyObject; origin is not null && !ReferenceEquals(origin, list); origin = VisualTreeHelper.GetParent(origin))
                if (origin is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
            if (ResolveContextItem(list, args.OriginalSource as DependencyObject, false) is PlaylistManagerRow row)
            { args.Handled = true; await Execute(row, "open"); }
        }), handledEventsToo: true);
        list.KeyDown += async (_, args) =>
        {
            if (args.Key == global::Windows.System.VirtualKey.Enter && FocusManager.GetFocusedElement(Root.XamlRoot) is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                && list.SelectedItem is PlaylistManagerRow row)
            { args.Handled = true; await Execute(row, "open"); }
        };
        search.TextChanged += (_, _) => { if (loaded && !busy && Current()) Render(); };
        create.Click += async (_, _) => await Execute(null, "create"); refresh.Click += async (_, _) => { if (remote) RequestMetadataSync(refresh: true); await Reload(); };
        view.Reload = () => { if (remote) RequestMetadataSync(refresh: true); return Reload(); };
        view.SourceChanged = () => { if (Current()) { if (remote && !CanReadRemote()) loaded = false; Render(); } };
        if (!await MountPlaylistPageAsync(view)) return;
        playlistManagerAction = (row, action) => Execute(row, action);
        view.Closing = () => playlistManagerAction = null;
        await Reload(cached: true);
#if DEBUG
        if (Current()) playlistManagerOpened?.Invoke(view);
#endif
    }
}
