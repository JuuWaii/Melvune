using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private async void ManageLocalLibraries_Click(object sender, RoutedEventArgs e) => await ShowLocalLibraryManagementAsync();
#if DEBUG
    private Action<PlaylistPageView>? localLibraryPageOpened;
#endif
    private async Task ShowLocalLibraryManagementAsync(bool createNew = false)
    {
        if (localImportBusy || closed) return;
        var catalog = LocalLibraryCatalog.Empty;
        bool rendering = false, busy = false, loaded = false, editing = createNew;
        var list = new ListView { ItemTemplate = (DataTemplate)Root.Resources["LocalLibraryCardTemplate"], SelectionMode = ListViewSelectionMode.Single };
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        list.ItemContainerStyle = itemStyle;
        global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, "本地音乐库");
        var name = new TextBox { Header = "库名称", MaxLength = 120, PlaceholderText = "新增时留空使用文件夹名称" };
        var group = new TextBox { Header = "分组（可选）", MaxLength = 120 };
        var path = new TextBox { Header = "文件夹路径", MaxLength = 4096, PlaceholderText = @"例如 D:\Music" };
        var confirm = new CheckBox { Content = "确认移除所选音乐库" };
        var fields = new StackPanel { Spacing = 10 };
        foreach (var child in new UIElement[] { name, group, path, confirm }) fields.Children.Add(child);
        fields.Children.Add(new TextBlock { Text = "修改路径会重新扫描并保留来源身份；移除仅取消导入，音乐文件与歌单条目保留。", TextWrapping = TextWrapping.Wrap });
        Button TopAction(string label, string glyph)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 }); content.Children.Add(new TextBlock { Text = label });
            var button = new Button { Content = content, Tag = label };
            global::Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            return button;
        }
        var add = TopAction("添加文件夹", "\uE710");
        var scan = TopAction("扫描音乐库", "\uE72C");
        var browse = new Button { Content = "浏览文件夹…" };
        var save = new Button { Content = "保存" };
        var remove = new Button { Content = "移除音乐库", IsEnabled = false };
        var refresh = new Button { Content = "刷新音乐库" };
        var collapse = new Button { Content = "收起编辑" };
        fields.Children.Insert(3, browse);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { save, remove, collapse, refresh }) actions.Children.Add(button);
        var view = new PlaylistPageView("音乐库管理", fields, list, actions, new TextBlock { Text = "正在读取音乐库…", TextWrapping = TextWrapping.Wrap }, localLifetime.Token) { HeaderHeightLimit = 380 };
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        topActions.Children.Add(add); topActions.Children.Add(scan); view.SetHeadingActions(topActions);
        view.PlayButton.Visibility = Visibility.Collapsed;
        var ct = view.Lifetime.Token;
        void UpdateButtons()
        {
            save.IsEnabled = loaded && !string.IsNullOrWhiteSpace(path.Text);
            confirm.IsEnabled = list.SelectedItem is LocalLibrary;
            remove.IsEnabled = loaded && list.SelectedItem is LocalLibrary && confirm.IsChecked == true;
            scan.IsEnabled = loaded && catalog.Libraries.Count > 0;
            fields.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in new[] { save, remove, collapse }) button.Visibility = fields.Visibility;
        }
        void Select()
        {
            if (rendering) return;
            var item = list.SelectedItem as LocalLibrary;
            name.Text = item?.Name ?? ""; group.Text = item?.Group ?? ""; path.Text = item?.Directory ?? "";
            confirm.IsChecked = false; UpdateButtons();
        }
        void Render(Guid? selectedId)
        {
            rendering = true; list.ItemsSource = catalog.Libraries;
            list.SelectedItem = catalog.Libraries.FirstOrDefault(item => item.SourceId == selectedId);
            rendering = false; Select();
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
            { view.Status.Text = "音乐库已在其他窗口修改，请刷新核对后重试。原配置未被覆盖。"; loaded = false; }
            catch (Exception)
            { if (!ct.IsCancellationRequested) view.Status.Text = "操作未完成，请检查名称、目录或重复路径。原音乐库保留。"; }
            finally
            {
                busy = false; view.BlockNavigation = false;
                if (mutation) localImportBusy = false;
                if (!view.IsDisposed) { view.Editor.IsEnabled = true; view.CancelButton.Visibility = Visibility.Collapsed; UpdateButtons(); }
            }
        }
        async Task Refresh(Guid? selectedId)
        {
            catalog = await localLibraryStore.LoadAsync(ct); ct.ThrowIfCancellationRequested();
            loaded = true; Render(selectedId); view.Status.Text = catalog.Libraries.Count == 0 ? "暂无音乐库，点击右上方添加文件夹。" : $"{catalog.Libraries.Count} 个音乐库 · 选择卡片编辑名称、分组或路径。";
        }
        list.SelectionChanged += (_, _) => { if (rendering) return; editing = list.SelectedItem is LocalLibrary; Select(); };
        confirm.Click += (_, _) => UpdateButtons(); path.TextChanged += (_, _) => UpdateButtons();
        add.Click += (_, _) => { list.SelectedItem = null; editing = true; Select(); view.Status.Text = "填写文件夹路径后保存以导入新音乐库。"; path.Focus(FocusState.Programmatic); };
        collapse.Click += (_, _) => { list.SelectedItem = null; editing = false; Select(); };
        browse.Click += async (_, _) => await PickLocalFolderAsync(path, browse);
        refresh.Click += async (_, _) => await Run(() => Refresh((list.SelectedItem as LocalLibrary)?.SourceId), false);
        view.CancelButton.Click += CancelLocalScan_Click;
        scan.Click += async (_, _) =>
        {
            if (!loaded || catalog.Libraries.Count == 0) return;
            await Run(async () =>
            {
                var expected = catalog;
                if ((await localLibraryStore.LoadAsync(ct)).Revision != expected.Revision) throw new LocalLibraryConflictException();
                var scanned = new List<LocalMusicSource>();
                view.CancelButton.Visibility = Visibility.Visible;
                foreach (var definition in expected.Libraries)
                    scanned.Add(await ScanLocalAsync(definition.Settings, ct
#if DEBUG
                        , localPageMetadataReader
#endif
                    ));
                ct.ThrowIfCancellationRequested();
                if ((await localLibraryStore.LoadAsync(ct)).Revision != expected.Revision) throw new LocalLibraryConflictException();
                // Apply all libraries only after the complete scan and catalog check succeed.
                ApplyLocalCatalog(expected);
                foreach (var source in scanned) ReplaceLocalSource(source);
                view.Status.Text = PlaybackStatus.Text = $"扫描完成，{scanned.Count} 个音乐库，共 {scanned.Sum(source => source.Tracks.Count)} 首。";
                if (localView || allMusicView) await LoadPageAsync(page);
            }, true);
        };
        save.Click += async (_, _) =>
        {
            if (!loaded || busy) return;
            var selected = list.SelectedItem as LocalLibrary;
            string enteredPath = path.Text.Trim(), enteredName = name.Text.Trim(), enteredGroup = group.Text.Trim();
            await Run(async () =>
            {
                if (!Path.IsPathFullyQualified(enteredPath)) throw new ArgumentException();
                string directory = LocalLibraryStore.NormalizePath(enteredPath);
                var existing = selected ?? catalog.Libraries.FirstOrDefault(item => string.Equals(LocalLibraryStore.NormalizePath(item.Directory), directory, StringComparison.OrdinalIgnoreCase));
                var updated = existing is null ? new LocalLibrary(Guid.NewGuid(), directory,
                    enteredName.Length == 0 ? new DirectoryInfo(directory).Name : enteredName, enteredGroup)
                    : existing with { Name = enteredName.Length == 0 && selected is null ? existing.Name : enteredName, Group = enteredGroup, Directory = directory };
                if (existing is null && updated.Name.Length == 0) updated = updated with { Name = "本地音乐" };
                LocalMusicSource? rescanned = null;
                if (selected is null || !string.Equals(directory, LocalLibraryStore.NormalizePath(selected.Directory), StringComparison.OrdinalIgnoreCase))
                {
                    view.Status.Text = "正在扫描本地音乐…"; view.CancelButton.Visibility = Visibility.Visible;
                    rescanned = await ScanLocalAsync(updated.Settings, ct
#if DEBUG
                        , localPageMetadataReader
#endif
                    );
                    view.CancelButton.Visibility = Visibility.Collapsed;
                }
                var next = catalog with { Libraries = existing is null ? catalog.Libraries.Append(updated).ToArray()
                    : catalog.Libraries.Select(item => item.SourceId == updated.SourceId ? updated : item).ToArray() };
                catalog = await localLibraryStore.SaveAsync(next, ct); ct.ThrowIfCancellationRequested();
                ApplyLocalCatalog(catalog);
                if (rescanned is not null) ReplaceLocalSource(rescanned);
                Render(updated.SourceId);
                view.Status.Text = PlaybackStatus.Text = rescanned is null ? "音乐库设置已保存。" : $"音乐库已保存，扫描到 {rescanned.Tracks.Count} 首。";
                if (localView || allMusicView) await LoadPageAsync(page);
            }, true);
        };
        remove.Click += async (_, _) =>
        {
            if (!loaded || confirm.IsChecked != true || list.SelectedItem is not LocalLibrary selected) return;
            await Run(async () =>
            {
                catalog = await localLibraryStore.SaveAsync(catalog with { Libraries = catalog.Libraries.Where(item => item.SourceId != selected.SourceId).ToArray() }, ct);
                ct.ThrowIfCancellationRequested(); ApplyLocalCatalog(catalog); Render(null);
                view.Status.Text = PlaybackStatus.Text = "已移除音乐库，音乐文件与歌单引用保留。";
                if (localView || allMusicView) await LoadPageAsync(page);
            }, true);
        };
        view.Reload = () => Run(() => Refresh((list.SelectedItem as LocalLibrary)?.SourceId), false);
        if (!MountFullPage(view)) return;
        await Run(async () =>
        {
            await Refresh(createNew ? null : selectedLocalLibrary);
            editing = createNew || list.SelectedItem is LocalLibrary; Select();
        }, false);
#if DEBUG
        if (!view.IsDisposed) localLibraryPageOpened?.Invoke(view);
#endif
    }
}
