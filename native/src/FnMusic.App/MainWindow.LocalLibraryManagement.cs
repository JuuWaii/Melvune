using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private Func<LibraryManagerRow, Task>? libraryCardEdit, libraryCardDelete;
    private async void LibraryCardEdit_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: LibraryManagerRow row } && libraryCardEdit is { } edit) await edit(row); }
    private async void LibraryCardDelete_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: LibraryManagerRow row } && libraryCardDelete is { } delete) await delete(row); }
    private async void ManageLocalLibraries_Click(object sender, RoutedEventArgs e) => await ShowLocalLibraryManagementAsync();
#if DEBUG
    private Action<PlaylistPageView>? localLibraryPageOpened;
#endif
    private async Task ShowLocalLibraryManagementAsync(bool createNew = false)
    {
        if (localImportBusy || closed) return;
        var catalog = LocalLibraryCatalog.Empty;
        bool busy = false, loaded = false, admin = false;
        NasApiClient? api = null;
        IReadOnlyList<string> authorizedPaths = [];
        var rows = new List<LibraryManagerRow>();
        var list = new ListView { ItemTemplate = (DataTemplate)Root.Resources["LocalLibraryCardTemplate"], SelectionMode = ListViewSelectionMode.Single };
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        list.ItemContainerStyle = itemStyle;
        global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "音乐库");
        Button TopAction(string label, string glyph)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 }); content.Children.Add(new TextBlock { Text = label });
            var button = new Button { Content = content, Tag = label };
            global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            return button;
        }
        var add = TopAction("新建音乐库", "\uE710");
        var scan = TopAction("扫描音乐库", "\uE72C");
        ToolTipService.SetToolTip(scan, "扫描全部本地音乐库；飞牛后台扫描可在素材库与设置中操作。");
        var refresh = new Button { Content = "刷新音乐库" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(refresh);
        var view = new PlaylistPageView("音乐库管理", new StackPanel { Visibility = Visibility.Collapsed }, list, actions,
            new TextBlock { Text = "正在读取音乐库…", TextWrapping = TextWrapping.Wrap }, localLifetime.Token);
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        topActions.Children.Add(add); topActions.Children.Add(scan); view.SetHeadingActions(topActions);
        view.PlayButton.Visibility = Visibility.Collapsed;
        var ct = view.Lifetime.Token;
        bool CurrentRemote(bool write = false) => api is not null && IsCurrentSettingsClient(api)
            && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadSettings
                | (write ? MusicSourceCapabilities.WriteSettings : MusicSourceCapabilities.None)) && (!write || admin);
        void UpdateButtons()
        { add.IsEnabled = loaded; scan.IsEnabled = loaded && catalog.Libraries.Count > 0; }
        void Render(string? key)
        { list.ItemsSource = rows.ToArray(); list.SelectedItem = rows.FirstOrDefault(row => row.Key == key); UpdateButtons(); }
        void InvalidateRows()
        {
            loaded = false; rows = rows.Select(row => row with { CanEdit = false, CanDelete = false }).ToList();
            Render((list.SelectedItem as LibraryManagerRow)?.Key);
        }
        async Task Run(Func<Task> operation, bool mutation)
        {
            if (busy || ct.IsCancellationRequested || (mutation && localImportBusy)) return;
            busy = true; view.Editor.IsEnabled = false; view.BlockNavigation = mutation;
            if (mutation) localImportBusy = true;
            try { await operation(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || mutation)
            { if (!ct.IsCancellationRequested) view.Status.Text = "扫描已取消，原音乐库保留。"; }
            catch (LocalLibraryConflictException)
            { view.Status.Text = "音乐库已在其他窗口修改，请刷新核对后重试。原配置未被覆盖。"; InvalidateRows(); }
            catch (RemoteSettingsConflictException)
            { view.Status.Text = "飞牛音乐库已改变，请刷新核对后重试。"; InvalidateRows(); }
            catch (MusicApiException ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    view.Status.Text = ex.Failure == MusicFailure.Unavailable ? "连接失败或请求超时，请刷新核对音乐库状态后重试。" : "飞牛音乐库操作未完成，请检查连接和权限后刷新。";
                    if (ex.Failure == MusicFailure.Unauthorized && api is not null && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
                }
            }
            catch (Exception)
            { if (!ct.IsCancellationRequested) view.Status.Text = "操作未完成，请检查名称、目录或重复路径。原音乐库保留。"; }
            finally
            {
                busy = false; view.BlockNavigation = false;
                if (mutation) localImportBusy = false;
                if (!view.IsDisposed) { view.Editor.IsEnabled = true; view.CancelButton.Visibility = Visibility.Collapsed; UpdateButtons(); }
            }
        }
        async Task Refresh(string? key = null)
        {
            catalog = await localLibraryStore.LoadAsync(ct); ct.ThrowIfCancellationRequested();
            rows = catalog.Libraries.Select(local => new LibraryManagerRow(local, null, true)).ToList();
            api = ViewModel.AuthenticatedClient;
#if DEBUG
            if (IsSyntheticPreview) api ??= settingsTestClient;
#endif
            admin = false; authorizedPaths = [];
            string warning = "";
            if (CurrentRemote())
            {
                try
                {
                    admin = await api!.IsAdministratorAsync(ct);
                    var remote = await api.ListSharedLibrariesAsync(ct); ct.ThrowIfCancellationRequested();
                    if (!CurrentRemote()) throw new MusicApiException(MusicFailure.Unavailable);
                    rows.AddRange(remote.Select(item => new LibraryManagerRow(null, item, CurrentRemote(true))));
                    if (CurrentRemote(true))
                    {
                        try { authorizedPaths = await api.ListAuthorizedDirectoriesAsync(ct); }
                        catch (MusicApiException ex) when (ex.Failure != MusicFailure.Unauthorized) { warning = "授权目录读取失败，可填写已授权的 NAS 内部路径。"; }
                    }
                }
                catch (MusicApiException ex)
                {
                    warning = "飞牛音乐库读取失败，本地音乐库仍可管理。"; admin = false;
                    if (ex.Failure == MusicFailure.Unauthorized && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync();
                }
            }
            ct.ThrowIfCancellationRequested(); loaded = true; Render(key);
            view.Status.Text = rows.Count == 0 ? "暂无音乐库，点击右上方新建音乐库。" : $"{rows.Count} 个音乐库 · 使用卡片右侧的图标编辑或删除。";
            if (warning.Length > 0) view.Status.Text += " " + warning;
        }
        async Task Edit(LibraryManagerRow? selected)
        {
            if (busy || !loaded || ct.IsCancellationRequested || themedDialogOpen || (selected is not null && !rows.Contains(selected))) return;
            var expected = catalog; var expectedApi = api;
            var request = await EditLibraryDialogAsync(selected, api, CurrentRemote(true), authorizedPaths, ct);
            if (request is null || ct.IsCancellationRequested || !ReferenceEquals(fullPageView, view)) return;
            await Run(async () =>
            {
                if (request.Kind == 1)
                {
                    if (expectedApi is null || !ReferenceEquals(api, expectedApi) || !CurrentRemote(true)) throw new MusicApiException(MusicFailure.Rejected);
                    var updatedRemote = selected?.Remote is { } remote
                        ? await expectedApi.UpdateSharedLibraryAsync(remote, request.Lyric, request.Metadata, ct)
                        : await expectedApi.CreateSharedLibraryAsync(request.Path, request.Lyric, request.Metadata, ct);
                    await Refresh("nas:" + updatedRemote.Library.Id); view.Status.Text = "音乐库设置已同步到飞牛。"; return;
                }
                if (request.Kind != 0 || selected?.Remote is not null) return;
                if ((await localLibraryStore.LoadAsync(ct)).Revision != expected.Revision) throw new LocalLibraryConflictException();
                if (!Path.IsPathFullyQualified(request.Path)) throw new ArgumentException();
                string directory = LocalLibraryStore.NormalizePath(request.Path);
                var existing = selected?.Local ?? expected.Libraries.FirstOrDefault(item => string.Equals(LocalLibraryStore.NormalizePath(item.Directory), directory, StringComparison.OrdinalIgnoreCase));
                if (selected is null && existing is not null) throw new ArgumentException();
                var updated = existing is null ? new LocalLibrary(Guid.NewGuid(), directory,
                    request.Name.Length == 0 ? new DirectoryInfo(directory).Name : request.Name, request.Group)
                    : existing with { Name = request.Name, Group = request.Group, Directory = directory };
                if (existing is null && updated.Name.Length == 0) updated = updated with { Name = "本地音乐" };
                LocalMusicSource? rescanned = null;
                if (existing is null || !string.Equals(directory, LocalLibraryStore.NormalizePath(existing.Directory), StringComparison.OrdinalIgnoreCase))
                {
                    view.Status.Text = "正在扫描本地音乐…"; view.CancelButton.Visibility = Visibility.Visible;
                    rescanned = await ScanLocalAsync(updated.Settings, ct
#if DEBUG
                        , localPageMetadataReader
#endif
                    );
                }
                catalog = await localLibraryStore.SaveAsync(expected with { Libraries = existing is null ? expected.Libraries.Append(updated).ToArray()
                    : expected.Libraries.Select(item => item.SourceId == updated.SourceId ? updated : item).ToArray() }, ct);
                ct.ThrowIfCancellationRequested(); ApplyLocalCatalog(catalog);
                if (rescanned is not null) ReplaceLocalSource(rescanned);
                rows = rows.Where(row => row.Local?.SourceId != updated.SourceId).ToList();
                rows.Insert(0, new(updated, null, true)); Render("local:" + updated.SourceId);
                view.Status.Text = PlaybackStatus.Text = rescanned is null ? "音乐库设置已保存。" : $"音乐库已保存，扫描到 {rescanned.Tracks.Count} 首。";
                if (localView || allMusicView) await LoadPageAsync(page);
            }, true);
        }
        async Task Delete(LibraryManagerRow selected)
        {
            if (busy || !loaded || ct.IsCancellationRequested || themedDialogOpen || !rows.Contains(selected) || !selected.CanDelete) return;
            var expected = catalog; var expectedApi = api;
            if (!await ConfirmLibraryDeleteAsync(selected, ct) || ct.IsCancellationRequested || !ReferenceEquals(fullPageView, view)) return;
            await Run(async () =>
            {
                if (selected.Remote is { } remote)
                {
                    if (expectedApi is null || !ReferenceEquals(api, expectedApi) || !CurrentRemote(true)) throw new MusicApiException(MusicFailure.Rejected);
                    await expectedApi.DeleteSharedLibraryAsync(remote, ct); await Refresh();
                    view.Status.Text = "已从飞牛移除音乐库及其索引，原音乐文件保留。"; return;
                }
                catalog = await localLibraryStore.SaveAsync(expected with { Libraries = expected.Libraries.Where(item => item.SourceId != selected.Local!.SourceId).ToArray() }, ct);
                ct.ThrowIfCancellationRequested(); ApplyLocalCatalog(catalog); rows.Remove(selected); Render(null);
                view.Status.Text = PlaybackStatus.Text = "已移除音乐库，音乐文件与歌单引用保留。";
                if (localView || allMusicView) await LoadPageAsync(page);
            }, true);
        }
        add.Click += async (_, _) => await Edit(null);
        refresh.Click += async (_, _) => await Run(() => Refresh((list.SelectedItem as LibraryManagerRow)?.Key), false);
        view.CancelButton.Click += CancelLocalScan_Click;
        scan.Click += async (_, _) =>
        {
            if (!loaded || catalog.Libraries.Count == 0) return;
            await Run(async () =>
            {
                var expected = catalog;
                if ((await localLibraryStore.LoadAsync(ct)).Revision != expected.Revision) throw new LocalLibraryConflictException();
                var scanned = new List<LocalMusicSource>(); view.CancelButton.Visibility = Visibility.Visible;
                foreach (var definition in expected.Libraries)
                    scanned.Add(await ScanLocalAsync(definition.Settings, ct
#if DEBUG
                        , localPageMetadataReader
#endif
                    ));
                ct.ThrowIfCancellationRequested();
                if ((await localLibraryStore.LoadAsync(ct)).Revision != expected.Revision) throw new LocalLibraryConflictException();
                ApplyLocalCatalog(expected); foreach (var source in scanned) ReplaceLocalSource(source);
                view.Status.Text = PlaybackStatus.Text = $"扫描完成，{scanned.Count} 个本地音乐库，共 {scanned.Sum(source => source.Tracks.Count)} 首。";
                if (localView || allMusicView) await LoadPageAsync(page);
            }, true);
        };
        view.Reload = () => Run(() => Refresh((list.SelectedItem as LibraryManagerRow)?.Key), false);
        view.SourceChanged = () =>
        {
            if (!CurrentRemote()) admin = false;
            rows = rows.Select(row => row.Remote is not null ? row with { CanDelete = loaded && CurrentRemote(true), CanEdit = loaded && CurrentRemote() } : row).ToList();
            Render((list.SelectedItem as LibraryManagerRow)?.Key);
        };
        if (!MountFullPage(view)) return;
        libraryCardEdit = row => Edit(row); libraryCardDelete = Delete;
        view.Closing = () => { libraryCardEdit = libraryCardDelete = null; };
        await Run(() => Refresh(selectedLocalLibrary is { } id ? "local:" + id : null), false);
#if DEBUG
        if (!view.IsDisposed) localLibraryPageOpened?.Invoke(view);
#endif
        if (createNew && !view.IsDisposed) await Edit(null);
    }
}
