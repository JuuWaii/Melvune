using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Melvune.App;

public sealed partial class MainWindow
{
#if DEBUG
    private Action<PlaylistPageView, bool>? musicManagementPageOpened;
#endif
    private Task ShowMusicManagementAsync(bool playlists = false) => playlists
        ? ShowUnifiedPlaylistManagementAsync() : ShowLocalLibraryManagementAsync();

    private void InstallMusicManagementTabs(PlaylistPageView view, bool playlists)
    {
        var libraries = new SelectorBarItem { Text = "音乐库", Tag = "libraries" };
        var playlist = new SelectorBarItem { Text = "歌单", Tag = "playlists" };
        var tabs = new SelectorBar();
        tabs.Items.Add(libraries); tabs.Items.Add(playlist);
        tabs.SelectedItem = playlists ? playlist : libraries;
        global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tabs, "音乐管理分页");
        bool reverting = false;
        tabs.SelectionChanged += async (_, _) =>
        {
            if (reverting || view.IsDisposed || !ReferenceEquals(fullPageView, view)) return;
            bool target = ReferenceEquals(tabs.SelectedItem, playlist);
            if (target == playlists) return;
            if (themedDialogOpen || view.BlockNavigation || !view.Editor.IsEnabled || contextPlaylistWriteBusy)
            {
                reverting = true; tabs.SelectedItem = playlists ? playlist : libraries; reverting = false;
                view.Status.Text = "请等待当前操作完成后切换分页。"; return;
            }
            await ShowMusicManagementAsync(target);
        };
        view.Fields.Children.Insert(0, tabs);
    }

    private async Task ShowUnifiedPlaylistManagementAsync(string? selectedKey = null)
    {
        if (closed) return;
        bool busy = false, commandBusy = false, localLoaded = false, remoteLoaded = false, reloadRequested = false;
        NasApiClient? api = null;
        var rows = new List<PlaylistManagerRow>();
        string? selection = selectedKey;
        string warning = "", notice = "";
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single,
            ItemTemplate = (DataTemplate)Root.Resources["PlaylistManagerCardTemplate"] };
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); list.ItemContainerStyle = itemStyle;
        global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "全部歌单管理列表");
        var search = new TextBox { PlaceholderText = "筛选歌单名称", MaxLength = 120, Header = "查找歌单" };
        var visibility = new ToggleSwitch { Header = "飞牛歌单单独分组", IsOn = separateNasPlaylistsInSidebar };
        ToolTipService.SetToolTip(visibility, "开启后侧栏分为飞牛歌单和其他歌单；关闭时全部合并显示。两种模式均保留飞牛歌单。");
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(visibility);
        fields.Children.Add(new TextBlock { Text = "集中管理本机与当前飞牛账号的歌单。飞牛歌单修改会同步到服务器；删除歌单保留音乐文件。", TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(search);
        Button Command(string label, string tag, string glyph)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 }); content.Children.Add(new TextBlock { Text = label });
            var button = new Button { Content = content, Tag = tag };
            global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            return button;
        }
        var createLocal = Command("新建本地歌单", "create-local", "\uE710");
        var createNas = Command("新建飞牛歌单", "create-nas", "\uE710");
        var refresh = new Button { Content = "刷新歌单" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(refresh);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(createLocal); heading.Children.Add(createNas);
        var view = new PlaylistPageView("音乐管理", fields, list, actions,
            new TextBlock { Text = "正在读取歌单…", TextWrapping = TextWrapping.Wrap }, localLifetime.Token);
        view.PlayButton.Visibility = Visibility.Collapsed; view.SetHeadingActions(heading);
        InstallMusicManagementTabs(view, playlists: true);
        var ct = view.Lifetime.Token;
        bool Current() => !closed && !view.IsDisposed && !ct.IsCancellationRequested && ReferenceEquals(fullPageView, view);
        bool RemoteCurrent() => api is not null && IsCurrentPlaylistClient(api)
            && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists);
        bool RemoteWrite() => RemoteCurrent() && ViewModel.PlaybackRouter.Supports(api!.SourceInstanceId, MusicSourceCapabilities.WritePlaylists);
        void Render()
        {
            if (!Current()) return;
            bool ready = !busy && !commandBusy;
            rows = rows.Select(row => row with {
                CanOpen = ready && (row.Value is LocalPlaylist ? localLoaded : remoteLoaded && RemoteCurrent()),
                CanWrite = ready && (row.Value is LocalPlaylist ? localLoaded : remoteLoaded && RemoteWrite()) }).ToList();
            var filtered = rows.Where(row => row.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
            list.ItemsSource = filtered;
            list.SelectedItem = filtered.FirstOrDefault(row => row.Key == selection);
            createLocal.IsEnabled = ready && localLoaded; createNas.IsEnabled = ready && remoteLoaded && RemoteWrite();
            string summary = filtered.Length == 0 && rows.Count > 0 ? "没有匹配的歌单。"
                : rows.Count == 0 ? "暂无可显示的歌单。" : $"{rows.Count} 个歌单 · 当前显示 {filtered.Length} 个。";
            view.Status.Text = (notice.Length == 0 ? summary : notice) + (warning.Length == 0 ? "" : " " + warning);
        }
        async Task Reload(bool cached = false)
        {
            if (!Current()) return;
            if (busy) { reloadRequested = true; return; }
            busy = true; notice = ""; view.Editor.IsEnabled = false; view.Status.Text = "正在读取歌单…";
            api = ContextClient;
            try
            {
                var next = new List<PlaylistManagerRow>();
                var failures = new List<string>();
                localLoaded = remoteLoaded = false;
                try
                {
                    var catalog = await playlistStore.LoadAsync(ct);
                    if (!Current()) return;
                    localLoaded = true;
                    next.AddRange(catalog.Playlists.Select(item => new PlaylistManagerRow(item, true, true)));
                    RenderLocalPlaylistNavigation(catalog);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { failures.Add("本地歌单读取失败，原文件保留，请刷新重试。"); }
                if (!Current()) return;
                if (RemoteCurrent())
                {
                    var expectedApi = api!;
                    try
                    {
                        var data = cached ? CurrentMetadata(expectedApi)?.Playlists.Select(item => item.Playlist).ToArray()
                            ?? await expectedApi.ListPlaylistsAsync(ct) : await expectedApi.ListPlaylistsAsync(ct);
                        if (!Current()) return;
                        if (ReferenceEquals(expectedApi, api) && RemoteCurrent())
                        {
                            remoteLoaded = true;
                            next.AddRange(data.Select(item => new PlaylistManagerRow(item, true, RemoteWrite())));
                            RenderNasPlaylistNavigation(expectedApi, data);
                        }
                        else failures.Add("飞牛账号已改变，请刷新读取当前账号歌单。");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
                    {
                        if (ReferenceEquals(expectedApi, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
                        failures.Add("飞牛登录已失效，本地歌单仍可管理。");
                    }
                    catch (Exception) { failures.Add("飞牛歌单读取失败，本地歌单仍可管理，请刷新重试。"); }
                }
                else failures.Add("连接飞牛并登录后，可在此管理同步歌单。");
                if (!Current()) return;
                rows = next; warning = string.Join(" ", failures);
            }
            finally
            {
                busy = false;
                if (Current())
                {
                    view.Editor.IsEnabled = true; Render();
                    if (reloadRequested) { reloadRequested = false; await Reload(); }
                }
            }
        }
        async Task<object?> Mutate(PlaylistManagerRow? row, string action, bool remote)
        {
            var expectedApi = api;
            bool Writable() => Current() && (!remote || ReferenceEquals(api, expectedApi) && RemoteWrite());
            if (!Writable()) return null;
            bool create = action is "create-local" or "create-nas";
            string? name = null;
            NasPlaylistSnapshot? remoteSnapshot = null;
            if (create || action == "rename")
            {
                name = await RequestPlaylistNameAsync(remote, create ? null : row!.Name);
                if (name is null || !Writable()) return null;
            }
            else if (action == "delete")
            {
                int count;
                if (row!.Value is LocalPlaylist local) count = local.Entries.Count;
                else
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    remoteSnapshot = await expectedApi!.ReadPlaylistSnapshotAsync(((NasPlaylist)row.Value).Reference, readTimeout.Token);
                    if (!Writable()) return null;
                    if (remoteSnapshot.Playlist != (NasPlaylist)row.Value) throw new RemotePlaylistConflictException();
                    count = remoteSnapshot.Tracks.Count;
                }
                if (!await ConfirmDeletePlaylistAsync(row.Name, count, remote) || !Writable()) return null;
            }
            else return null;
            object? result = null;
            if (remote)
            {
                await WithNasPlaylistWriteAsync(expectedApi!, async token =>
                {
                    using var work = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
                    if (!Writable()) return;
                    if (create) result = (await expectedApi!.CreatePlaylistAsync(name!, work.Token)).Reference;
                    else if (action == "rename") await expectedApi!.RenamePlaylistAsync((NasPlaylist)row!.Value, name!, work.Token);
                    else await expectedApi!.DeletePlaylistAsync(remoteSnapshot!, work.Token);
                    var playlists = await expectedApi!.ListPlaylistsAsync(work.Token);
                    work.Token.ThrowIfCancellationRequested();
                    if (!Writable()) return;
                    RenderNasPlaylistNavigation(expectedApi, playlists);
                    PlaybackStatus.Text = action == "delete" ? "飞牛歌单已删除并回读核对，音乐文件保持。"
                        : create ? "飞牛歌单已创建并回读核对。" : "飞牛歌单已重命名并回读核对。";
                });
            }
            else
            {
                await WithContextPlaylistWriteAsync(async token =>
                {
                    using var work = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
                    var catalog = await playlistStore.LoadAsync(work.Token);
                    if (!Writable()) return;
                    if (!create && !SameLocalPlaylist(catalog.Playlists.FirstOrDefault(item => item.Id == ((LocalPlaylist)row!.Value).Id), (LocalPlaylist)row!.Value))
                        throw new PlaylistConflictException();
                    LocalPlaylist[] next;
                    if (create)
                    {
                        var playlist = new LocalPlaylist(Guid.NewGuid(), name!, []);
                        next = catalog.Playlists.Append(playlist).ToArray(); result = playlist.Id;
                    }
                    else if (action == "rename") next = catalog.Playlists.Select(item => item.Id == ((LocalPlaylist)row!.Value).Id ? item with { Name = name! } : item).ToArray();
                    else next = catalog.Playlists.Where(item => item.Id != ((LocalPlaylist)row!.Value).Id).ToArray();
                    var saved = await playlistStore.SaveAsync(catalog with { Playlists = next }, work.Token);
                    if (!Current()) return;
                    RenderLocalPlaylistNavigation(saved);
                    PlaybackStatus.Text = action == "delete" ? "歌单已删除，音乐文件保持。" : create ? "本地歌单已创建。" : "歌单已重命名。";
                });
            }
            return result;
        }
        async Task Execute(PlaylistManagerRow? row, string action)
        {
            if (!Current() || busy || commandBusy || !CanOpenPlaylistNameDialog() || (row is not null && !rows.Contains(row))) return;
            bool remote = action == "create-nas" || row?.Value is NasPlaylist;
            if (action == "open" ? row is not { CanOpen: true }
                : remote ? !remoteLoaded || !RemoteWrite() : !localLoaded) return;
            if (remote && row?.Value is NasPlaylist remoteRow && remoteRow.Reference.SourceInstanceId != api?.SourceInstanceId) return;
            commandBusy = true; notice = "";
            string previousPlaybackStatus = PlaybackStatus.Text;
            try
            {
                if (action == "open")
                {
                    if (row!.Value is LocalPlaylist local) await ShowLocalPlaylistsAsync(local.Id);
                    else await ShowNasPlaylistsAsync(api!, ((NasPlaylist)row.Value).Reference);
                    if (!closed && view.IsDisposed && activePlaylistPage is { IsDisposed: false } detail)
                        detail.ReturnNavigation = async () =>
                        {
                            // Return through the original browse context before remounting management.
                            // Leaving the detail beneath a disabled overlay would make Back loop to it.
                            detail.ReturnNavigation = null;
                            await ReturnFromPlaylistPageAsync();
                            if (!closed && activePlaylistPage is null) await ShowUnifiedPlaylistManagementAsync(row.Key);
                        };
                    return;
                }
                if (action is "create-local" or "create-nas")
                {
                    object? created = await Mutate(row, action, remote);
                    if (!Current() || created is null) return;
                    selection = created is Guid id ? "local:" + id : "nas:" + ((PlaylistReference)created).SourceInstanceId + ":" + ((PlaylistReference)created).PlaylistId;
                }
                else
                {
                    selection = row!.Key;
                    await Mutate(row, action, remote);
                }
                // The shared write helper refreshes the current management view after commit.
                if (Current()) { notice = PlaybackStatus.Text == previousPlaybackStatus ? "" : PlaybackStatus.Text; Render(); }
            }
            catch (OperationCanceledException) when (!Current()) { }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
            {
                if (Current() && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
                if (Current()) notice = "飞牛登录已失效，本地歌单仍可管理。";
            }
            catch (Exception) { if (Current()) notice = "操作未完成，请刷新核对歌单后重试。"; }
            finally
            {
                commandBusy = false;
                if (Current())
                {
                    Render();
                    if (reloadRequested) { reloadRequested = false; await Reload(); }
                }
            }
        }
        visibility.Toggled += (_, _) => { if (Current()) SetNasPlaylistSidebarGrouping(visibility.IsOn); };
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
        search.TextChanged += (_, _) => { if (!busy) Render(); };
        createLocal.Click += async (_, _) => await Execute(null, "create-local");
        createNas.Click += async (_, _) => await Execute(null, "create-nas");
        refresh.Click += async (_, _) => { if (!commandBusy) await Reload(); };
        view.Reload = () => Reload();
        view.SourceChanged = () =>
        {
            if (!Current()) return;
            bool accountChanged = !ReferenceEquals(api, ContextClient);
            if (!RemoteCurrent())
            {
                remoteLoaded = false; rows.RemoveAll(row => row.Value is NasPlaylist);
                warning = "飞牛连接已改变，本地歌单仍可管理。";
            }
            Render();
            if (accountChanged) { if (busy || commandBusy) reloadRequested = true; else _ = Reload(); }
        };
        if (!MountFullPage(view)) return;
        Func<PlaylistManagerRow?, string, Task> callback = Execute;
        playlistManagerAction = callback;
        view.Closing = () => { if (ReferenceEquals(playlistManagerAction, callback)) playlistManagerAction = null; };
        await Reload(cached: true);
#if DEBUG
        if (Current()) musicManagementPageOpened?.Invoke(view, true);
#endif
    }
}
