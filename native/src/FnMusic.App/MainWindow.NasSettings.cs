using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private bool nasSettingsOpen;
#if DEBUG
    private NasApiClient? settingsTestClient;
    private Action<PlaylistPageView>? settingsPageOpened;
#endif
    private bool IsCurrentSettingsClient(NasApiClient api) => ReferenceEquals(api, ViewModel.AuthenticatedClient)
#if DEBUG
        || (IsSyntheticPreview && ReferenceEquals(api, settingsTestClient))
#endif
        ;
    private sealed record SharedLibraryRow(NasSharedLibrary Value, string Name);
    private async void NasSettings_Click(object sender, RoutedEventArgs e)
    {
        if (nasSettingsOpen || closed) return;
        if (!RequireRemoteCapability(MusicSourceCapabilities.ReadSettings)) return;
        var api = ViewModel.AuthenticatedClient;
        if (api is null) { PlaybackStatus.Text = "请先连接飞牛并登录。"; return; }
        nasSettingsOpen = true;
        try { await ShowNasSettingsAsync(api); }
        catch (OperationCanceledException) { }
        catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
        { if (!closed && ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync(); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "飞牛设置读取失败，请检查连接和账号权限。"; }
        finally { nasSettingsOpen = false; }
    }
    private async Task ShowNasSettingsAsync(NasApiClient api)
    {
        bool busy = false, rendering = false, admin = false;
        if (closed || !IsCurrentSettingsClient(api)) return;
        NasServerSettings? server = null;
        NasDefaultLibraryAccess? access = null;
        NasSharedLibrary? selected = null;
        string? selectedLibraryId = null;
        IReadOnlyList<NasSharedLibrary> libraries = [];
        bool librariesReady = false;
        string refreshWarning = "";
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        var tabs = new Pivot();
        var libraryPanel = new StackPanel { Spacing = 10 };
        var picker = new ComboBox { Header = "素材库", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var currentPath = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        string[] preferences = ["cloud_preferred", "local_preferred", "local_only"];
        var preference = new ComboBox { Header = "元数据优先策略", ItemsSource = new[] { "优先云端", "优先本地标签", "仅本地标签" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var autoLyric = new CheckBox { Content = "自动下载歌词" };
        var saveLibrary = new Button { Content = "保存素材库设置" };
        var scan = new Button { Content = "扫描所选素材库" };
        var confirmDelete = new CheckBox { Content = "确认从飞牛移除所选素材库及其索引" };
        var delete = new Button { Content = "移除素材库" };
        var authorized = new ComboBox { Header = "NAS 已授权目录", HorizontalAlignment = HorizontalAlignment.Stretch };
        var createPath = new TextBox { Header = "新素材库 NAS 路径", MaxLength = 4096, PlaceholderText = "选择已授权目录或填写其子目录" };
        var create = new Button { Content = "创建素材库" };
        foreach (var child in new UIElement[] { picker, currentPath, preference, autoLyric, saveLibrary, scan, confirmDelete, delete, authorized, createPath, create }) libraryPanel.Children.Add(child);
        libraryPanel.Children.Add(new TextBlock { Text = "新素材库使用上方元数据与歌词选项。路径属于 NAS，不能填写本机路径。扫描请求提交后由服务器后台处理。", TextWrapping = TextWrapping.Wrap });
        var serverPanel = new StackPanel { Spacing = 10 };
        var serverName = new TextBox { Header = "飞牛音乐服务器名称", MaxLength = 32 };
        var language = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var saveServer = new Button { Content = "保存服务器名称" };
        serverPanel.Children.Add(serverName); serverPanel.Children.Add(language); serverPanel.Children.Add(saveServer);
        var accessPanel = new StackPanel { Spacing = 10 };
        var mode = new ComboBox { Header = "新用户默认访问范围", ItemsSource = new[] { "不允许访问", "仅所选素材库", "所有素材库" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var accessLibraries = new ListView { DisplayMemberPath = "Name", SelectionMode = ListViewSelectionMode.Multiple, Height = 200 };
        var saveAccess = new Button { Content = "保存默认访问范围" };
        accessPanel.Children.Add(mode); accessPanel.Children.Add(accessLibraries); accessPanel.Children.Add(saveAccess);
        accessPanel.Children.Add(new TextBlock { Text = "此项修改飞牛服务器中新用户的默认素材库访问范围；不会修改已有用户的单独授权。", TextWrapping = TextWrapping.Wrap });
        tabs.Items.Add(new PivotItem { Header = "素材库", Content = libraryPanel });
        tabs.Items.Add(new PivotItem { Header = "服务器", Content = serverPanel });
        tabs.Items.Add(new PivotItem { Header = "默认访问", Content = accessPanel });
        var role = new TextBlock { Text = admin ? "管理员设置 · 保存与刷新直接同步飞牛服务器" : "当前账号仅查看素材库；管理设置需要飞牛管理员权限。", TextWrapping = TextWrapping.Wrap };
        var refresh = new Button { Content = "刷新飞牛设置" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(role); panel.Children.Add(tabs);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(refresh);
        var sectionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(sectionActions);
        var view = new PlaylistPageView("飞牛素材库与设置", panel, new ListView(), actions, status, localLifetime.Token) { RemoteSource = api.SourceInstanceId };
        view.UseFormLayout();
        var ct = view.Lifetime.Token;
        var editor = view.Editor;
        Action updateTaskButtons = () => { };
        Action refreshReconnectSelection = () => { };
        Action updateReconnectButtons = () => { };
        bool CanWriteSettings() => !ct.IsCancellationRequested && IsCurrentSettingsClient(api) && admin && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadSettings | MusicSourceCapabilities.WriteSettings);
        var tasks = CreateNasTasksPanel(api, ct, CanWriteSettings, (operation, mutation) => Run(operation, mutation: mutation));
        updateTaskButtons = tasks.UpdateButtons;
        tabs.Items.Add(new PivotItem { Header = "后台任务", Content = tasks.Panel });
        var reconnect = CreateNasReconnectPanel(api, ct, CanWriteSettings, () => selected, (operation, mutation) => Run(operation, mutation: mutation), id => Refresh(id));
        refreshReconnectSelection = reconnect.RefreshSelection;
        updateReconnectButtons = reconnect.UpdateButtons;
        tabs.Items.Add(new PivotItem { Header = "重新关联", Content = reconnect.Panel });
        var sections = new[] { libraryPanel, serverPanel, accessPanel, tasks.Panel, reconnect.Panel };
        var buttonsByTab = sections.Select(section => section.Children.OfType<Button>().ToArray()).ToArray();
        foreach (var section in sections)
            foreach (var button in section.Children.OfType<Button>().ToArray()) section.Children.Remove(button);
        void SelectTabActions()
        {
            sectionActions.Children.Clear();
            if (tabs.SelectedIndex >= 0 && tabs.SelectedIndex < buttonsByTab.Length)
                foreach (var button in buttonsByTab[tabs.SelectedIndex]) sectionActions.Children.Add(button);
        }
        tabs.SelectionChanged += (_, _) => SelectTabActions();
        tabs.SelectedIndex = 0; SelectTabActions();
        void UpdateButtons()
        {
            bool known = preference.SelectedIndex >= 0;
            bool writable = CanWriteSettings();
            confirmDelete.IsEnabled = writable;
            saveLibrary.IsEnabled = writable && selected is not null && known;
            scan.IsEnabled = writable && selected is not null;
            delete.IsEnabled = writable && selected is not null && confirmDelete.IsChecked == true;
            create.IsEnabled = writable && librariesReady && known && !string.IsNullOrWhiteSpace(createPath.Text);
            preference.IsEnabled = autoLyric.IsEnabled = authorized.IsEnabled = createPath.IsEnabled = writable;
            serverName.IsEnabled = saveServer.IsEnabled = writable && server is not null;
            mode.IsEnabled = saveAccess.IsEnabled = writable && librariesReady && access is not null;
            accessLibraries.IsEnabled = writable && librariesReady && access is not null && mode.SelectedIndex == 1;
            updateTaskButtons();
            updateReconnectButtons();
        }
        void RenderSelected()
        {
            selected = (picker.SelectedItem as SharedLibraryRow)?.Value;
            currentPath.Text = selected?.Path ?? (librariesReady
                ? selectedLibraryId is null ? "尚无素材库。" : "所选素材库已不存在，请重新选择。"
                : "素材库读取失败，请刷新重试。");
            preference.SelectedIndex = selected is null ? 1 : Array.IndexOf(preferences, selected.MetadataPreference);
            autoLyric.IsChecked = selected?.AutoDownloadLyric ?? false; confirmDelete.IsChecked = false;
            refreshReconnectSelection();
            UpdateButtons();
        }
        async Task Refresh(string? selectedId = null)
        {
            string? requestedId = selectedId ?? selectedLibraryId;
            admin = await api.IsAdministratorAsync(ct);
            ct.ThrowIfCancellationRequested();
            role.Text = admin ? "管理员设置 · 保存与刷新直接同步飞牛服务器" : "当前账号仅查看素材库；管理设置需要飞牛管理员权限。";
            var failed = new List<string>();
            async Task ReadSection(string label, Func<Task> read)
            {
                try { await read(); }
                catch (OperationCanceledException) { throw; }
                catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized) { throw; }
                catch (Exception) { failed.Add(label); }
            }
            libraries = []; librariesReady = false; server = null; access = null;
            authorized.ItemsSource = null;
            await ReadSection("素材库", async () => { libraries = await api.ListSharedLibrariesAsync(ct); librariesReady = true; });
            if (admin)
            {
                await ReadSection("服务器设置", async () => server = await api.GetServerSettingsAsync(ct));
                await ReadSection("默认访问范围", async () => access = await api.GetDefaultLibraryAccessAsync(ct));
                await ReadSection("授权目录", async () => authorized.ItemsSource = await api.ListAuthorizedDirectoriesAsync(ct));
            }
            ct.ThrowIfCancellationRequested();
            refreshWarning = failed.Count == 0 ? "" : "部分设置读取失败：" + string.Join("、", failed) + "。请刷新重试；其他已读取项仍可使用。";
            rendering = true;
            var rows = libraries.Select(item => new SharedLibraryRow(item, string.IsNullOrWhiteSpace(item.Library.Name) ? item.Path : item.Library.Name)).ToArray();
            picker.ItemsSource = rows;
            picker.SelectedItem = requestedId is null ? rows.FirstOrDefault() : rows.FirstOrDefault(item => item.Value.Library.Id == requestedId);
            selectedLibraryId = (picker.SelectedItem as SharedLibraryRow)?.Value.Library.Id ?? requestedId;
            RenderSelected();
            serverName.Text = server?.Name ?? "";
            language.Text = server is null ? (admin ? "服务器设置读取失败，请刷新重试。" : "需要管理员权限。") : "服务端语言：" + server.Language + "（保留现有值）";
            mode.SelectedIndex = access?.Mode switch { "partial" => 1, "all" => 2, _ => 0 };
            accessLibraries.ItemsSource = rows;
            if (access is not null) foreach (var row in rows.Where(row => access.LibraryIds.Contains(row.Value.Library.Id))) accessLibraries.SelectedItems.Add(row);
            rendering = false; UpdateButtons();
        }
        async Task Run(Func<Task> operation, bool scanRequest = false, bool mutation = true)
        {
            if (busy || ct.IsCancellationRequested || !IsCurrentSettingsClient(api)) return;
            if (!ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadSettings) || (mutation && !CanWriteSettings()))
            { status.Text = "当前来源不支持此设置操作，或账号没有写入权限。"; UpdateButtons(); return; }
            busy = true; view.BlockNavigation = mutation; editor.IsEnabled = false; status.Text = "正在与飞牛同步…";
            try { await operation(); ct.ThrowIfCancellationRequested(); status.Text = (scanRequest ? "扫描请求已提交，请在后台任务页刷新查看状态，完成后刷新曲库。" : "操作已返回；具体结果以当前页面回读状态为准。") + (refreshWarning.Length == 0 ? "" : "\n" + refreshWarning); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (RemoteSettingsConflictException)
            { selected = null; server = null; access = null; status.Text = "服务端设置已变化或回读不一致，请刷新核对后重试。"; }
            catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
            { if (ReferenceEquals(api, ViewModel.AuthenticatedClient)) await ViewModel.ExpireSessionAsync(); }
            catch (Exception)
            { selected = null; server = null; access = null; status.Text = "未能确认结果，请先刷新核对；也请检查目录授权及管理员权限。"; }
            finally { busy = false; view.BlockNavigation = false; if (!view.IsDisposed) { editor.IsEnabled = true; UpdateButtons(); } }
        }
        picker.SelectionChanged += async (_, _) =>
        {
            if (rendering) return;
            var choice = (picker.SelectedItem as SharedLibraryRow)?.Value;
            selectedLibraryId = choice?.Library.Id;
            await Run(async () =>
            {
                if (choice is not null) selected = await api.GetSharedLibraryAsync(choice.Library, ct); else selected = null;
                currentPath.Text = selected?.Path ?? "尚无素材库。";
                preference.SelectedIndex = selected is null ? 1 : Array.IndexOf(preferences, selected.MetadataPreference);
                autoLyric.IsChecked = selected?.AutoDownloadLyric ?? false; confirmDelete.IsChecked = false;
                refreshReconnectSelection();
            }, mutation: false);
        };
        preference.SelectionChanged += (_, _) => UpdateButtons(); mode.SelectionChanged += (_, _) => UpdateButtons(); confirmDelete.Click += (_, _) => UpdateButtons();
        createPath.TextChanged += (_, _) => UpdateButtons();
        authorized.SelectionChanged += (_, _) => { if (authorized.SelectedItem is string path) createPath.Text = path; };
        saveLibrary.Click += async (_, _) =>
        { if (selected is { } current && preference.SelectedIndex >= 0) await Run(async () => { var updated = await api.UpdateSharedLibraryAsync(current, autoLyric.IsChecked == true, preferences[preference.SelectedIndex], ct); await Refresh(updated.Library.Id); }); };
        create.Click += async (_, _) =>
        { if (preference.SelectedIndex >= 0) await Run(async () => { var created = await api.CreateSharedLibraryAsync(createPath.Text.Trim(), autoLyric.IsChecked == true, preferences[preference.SelectedIndex], ct); await Refresh(created.Library.Id); }); };
        delete.Click += async (_, _) =>
        { if (selected is { } current && confirmDelete.IsChecked == true) await Run(async () => { await api.DeleteSharedLibraryAsync(current, ct); await Refresh(); }); };
        scan.Click += async (_, _) => { if (selected is { } current) await Run(() => api.ScanSharedLibraryAsync(current.Library, ct), true); };
        saveServer.Click += async (_, _) =>
        { if (server is { } current) await Run(async () => { server = await api.RenameServerAsync(current, serverName.Text, ct); serverName.Text = server.Name; }); };
        saveAccess.Click += async (_, _) =>
        {
            if (access is not { } current) return;
            string requested = mode.SelectedIndex switch { 1 => "partial", 2 => "all", _ => "none" };
            var chosen = accessLibraries.SelectedItems.Cast<SharedLibraryRow>().Select(item => item.Value.Library).ToArray();
            await Run(async () => { access = await api.UpdateDefaultLibraryAccessAsync(current, requested, chosen, ct); await Refresh(selected?.Library.Id); });
        };
        refresh.Click += async (_, _) => await Run(() => Refresh(selected?.Library.Id), mutation: false);
        view.SourceChanged = UpdateButtons;
        view.Reload = () => Run(() => Refresh(selected?.Library.Id), mutation: false);
        if (!MountFullPage(view)) return;
        await Run(() => Refresh(), mutation: false);
        if (closed || ct.IsCancellationRequested || !IsCurrentSettingsClient(api)) return;
#if DEBUG
        settingsPageOpened?.Invoke(view);
#endif
    }
}
