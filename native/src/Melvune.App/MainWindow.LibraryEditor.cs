using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;
public sealed partial class MainWindow
{
    private sealed record LibraryManagerRow(LocalLibrary? Local, NasSharedLibrary? Remote, bool CanDelete)
    {
        public bool CanEdit { get; init; } = true;
        public NavidromeConnection? Navidrome { get; init; }
        public EmbyConnection? Emby { get; init; }
        public HttpJsonConnection? HttpJson { get; init; }
        public string ConnectionStatus { get; init; } = "";
        public string Key => Local is { } local ? "local:" + local.SourceId : Navidrome is { } nav ? (nav.Provider == "subsonic" ? "subsonic:" : "nav:") + nav.SourceId : Emby is { } emby ? "emby:" + emby.SourceId : HttpJson is { } json ? "json:" + json.SourceId : "nas:" + Remote!.Library.Id;
        public string Name => Local?.Name ?? Navidrome?.Name ?? Emby?.Name ?? HttpJson?.Name ?? (string.IsNullOrWhiteSpace(Remote!.Library.Name) ? Path.GetFileName(Remote.Path.TrimEnd('/')) : Remote.Library.Name);
        public string Directory => Local?.Directory ?? Navidrome?.ServerAddress ?? Emby?.ServerAddress ?? HttpJson?.ServerAddress ?? Remote!.Path;
        public string Group => Local is { } local ? "本地音乐" + (local.Group.Length == 0 ? "" : " · " + local.Group) : Navidrome is not null || Emby is not null || HttpJson is not null ? (HttpJson is not null ? "自定义 HTTP/JSON" : Emby is not null ? "Emby" : Navidrome?.Provider == "subsonic" ? "Subsonic 兼容源" : "Navidrome") + (ConnectionStatus.Length > 0 ? " · " + ConnectionStatus : "") : "飞牛音乐";
    }
    private sealed record LibraryEditRequest(int Kind, string Name, string Group, string Path, string Metadata, bool Lyric)
    { public string Server { get; init; } = ""; public string Username { get; init; } = ""; public string Password { get; init; } = ""; public string JsonDefinition { get; init; } = ""; }
    private sealed class LibraryEditorView
    {
        public ContentDialog Dialog { get; }
        public ComboBox Kind { get; } = new() { Header = "音乐来源", ItemsSource = new[] { "本地音乐", "飞牛音乐", "其他音乐 · Navidrome", "其他音乐 · Emby", "其他音乐 · Subsonic", "其他音乐 · 自定义 HTTP/JSON" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        public ComboBox ServiceType { get; } = new() { Header = "服务类型", ItemsSource = new[] { "Navidrome", "Emby", "Subsonic 兼容源", "自定义 HTTP/JSON" }, SelectedIndex = 0, IsEnabled = false };
        public TextBox NavName { get; } = new() { Header = "库名称", MaxLength = 120 };
        public TextBox Server { get; } = new() { Header = "Navidrome 服务地址", MaxLength = 2048, PlaceholderText = "https://music.example.com/" };
        public TextBox Username { get; } = new() { Header = "账号", MaxLength = 256 };
        public PasswordBox Password { get; } = new() { Header = "密码（编辑时留空保留已保存凭据）", MaxLength = 1024 };
        public TextBox JsonDefinition { get; } = new() { Header = "JSON 字段方案", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxLength = 16384, Text = new HttpJsonDefinition().ToJson(), Visibility = Visibility.Collapsed };
        public TextBox Name { get; } = new() { Header = "库名称", MaxLength = 120, PlaceholderText = "留空使用文件夹名称" };
        public TextBox Group { get; } = new() { Header = "分组（可选）", MaxLength = 120 };
        public TextBox LocalPath { get; } = new() { Header = "文件夹路径", MaxLength = 4096, PlaceholderText = @"例如 D:\Music" };
        public TextBox NasPath { get; } = new() { Header = "NAS 目录路径", MaxLength = 4096, PlaceholderText = "选择授权目录或填写其子目录" };
        public ComboBox Metadata { get; } = new() { Header = "元数据优先策略", ItemsSource = new[] { "优先云端", "优先本地标签", "仅本地标签" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        public CheckBox Lyric { get; } = new() { Content = "自动下载歌词" };
        public StackPanel LocalFields { get; } = new() { Spacing = 10 };
        public StackPanel NasFields { get; } = new() { Spacing = 10 };
        public StackPanel OtherFields { get; } = new() { Spacing = 10 };
        public TextBlock OtherDescription { get; } = new() { TextWrapping = TextWrapping.Wrap };
        public TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap };
        public LibraryEditorView(XamlRoot root, bool editing)
        {
            Dialog = new() { XamlRoot = root, Title = editing ? "编辑音乐库" : "新建音乐库", PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        }
        public LibraryEditRequest Read() => new(Kind.SelectedIndex, Name.Text.Trim(), Group.Text.Trim(),
            (Kind.SelectedIndex == 0 ? LocalPath.Text : NasPath.Text).Trim(),
            Metadata.SelectedIndex switch { 0 => "cloud_preferred", 2 => "local_only", _ => "local_preferred" }, Lyric.IsChecked == true)
        { Name = Kind.SelectedIndex is 2 or 3 or 4 or 5 ? NavName.Text.Trim() : Name.Text.Trim(), Server = Server.Text.Trim(), Username = Kind.SelectedIndex == 5 ? "" : Username.Text.Trim(), Password = Kind.SelectedIndex == 5 ? "" : Password.Password, JsonDefinition = JsonDefinition.Text };
    }
#if DEBUG
    private Action<LibraryEditorView>? libraryEditorOpened;
    private Action<ContentDialog>? libraryDeleteOpened;
#endif
    private async Task<LibraryEditRequest?> EditLibraryDialogAsync(LibraryManagerRow? row, NasApiClient? api,
        bool remoteWritable, IReadOnlyList<string> authorizedPaths, CancellationToken ct)
    {
        var editor = new LibraryEditorView(Root.XamlRoot, row is not null);
        var browse = new Button { Content = "浏览文件夹…", HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var child in new UIElement[] { editor.Name, editor.Group, editor.LocalPath, browse,
            new TextBlock { Text = "修改路径会重新扫描，并保留音乐库与歌单中的来源身份。", TextWrapping = TextWrapping.Wrap } }) editor.LocalFields.Children.Add(child);
        var authorized = new ComboBox { Header = "NAS 已授权目录", ItemsSource = authorizedPaths, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var child in new UIElement[] { new TextBlock { Text = api is null ? "飞牛音乐 · 尚未连接" : "飞牛音乐 · 当前连接的服务器", TextWrapping = TextWrapping.Wrap }, authorized,
            editor.NasPath, editor.Metadata, editor.Lyric,
            new TextBlock { Text = row?.Remote is not null ? "名称由飞牛目录决定。路径重新关联请使用飞牛素材库与设置中的重新关联功能。" : "使用 NAS 内部路径，不能填写本机或 SMB 路径；保存后同步到飞牛网页端。", TextWrapping = TextWrapping.Wrap } }) editor.NasFields.Children.Add(child);
        editor.OtherFields.Children.Add(editor.ServiceType);
        foreach (var child in new UIElement[] { editor.NavName, editor.Server, editor.Username, editor.Password, editor.JsonDefinition, editor.OtherDescription }) editor.OtherFields.Children.Add(child);
        if (row?.Navidrome is { } nav)
        { editor.NavName.Text = nav.Name; editor.Server.Text = nav.ServerAddress; editor.Username.Text = nav.Username; editor.Server.IsReadOnly = editor.Username.IsReadOnly = true; }
        if (row?.Emby is { } emby)
        { editor.NavName.Text = emby.Name; editor.Server.Text = emby.ServerAddress; editor.Username.Text = emby.Username; editor.Server.IsReadOnly = editor.Username.IsReadOnly = true; }
        if (row?.HttpJson is { } jsonConnection)
        { editor.NavName.Text = jsonConnection.Name; editor.Server.Text = jsonConnection.ServerAddress; editor.JsonDefinition.Text = jsonConnection.Definition.ToJson(); editor.Server.IsReadOnly = editor.JsonDefinition.IsReadOnly = true; }
        var panel = new StackPanel { Spacing = 12, MinWidth = 280 };
        foreach (var child in new UIElement[] { editor.Kind, editor.LocalFields, editor.NasFields, editor.OtherFields, editor.Status }) panel.Children.Add(child);
        editor.Dialog.Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(160, Root.ActualHeight - 230), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        editor.Name.Text = row?.Local?.Name ?? ""; editor.Group.Text = row?.Local?.Group ?? ""; editor.LocalPath.Text = row?.Local?.Directory ?? "";
        if (row?.Remote is { } remote)
        {
            editor.NasPath.Text = remote.Path; editor.NasPath.IsReadOnly = true;
            editor.Lyric.IsChecked = remote.AutoDownloadLyric;
            editor.Metadata.SelectedIndex = Array.IndexOf(new[] { "cloud_preferred", "local_preferred", "local_only" }, remote.MetadataPreference);
        }
        editor.Kind.IsEnabled = row is null;
        void Update()
        {
            bool local = editor.Kind.SelectedIndex == 0, nas = editor.Kind.SelectedIndex == 1;
            bool emby = editor.Kind.SelectedIndex == 3, subsonic = editor.Kind.SelectedIndex == 4, json = editor.Kind.SelectedIndex == 5, other = editor.Kind.SelectedIndex is 2 or 3 or 4 or 5;
            editor.LocalFields.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            editor.NasFields.Visibility = nas ? Visibility.Visible : Visibility.Collapsed;
            editor.OtherFields.Visibility = other ? Visibility.Visible : Visibility.Collapsed;
            editor.ServiceType.SelectedIndex = json ? 3 : emby ? 1 : subsonic ? 2 : 0;
            editor.Server.Header = json ? "HTTP/JSON 服务根地址" : emby ? "Emby 服务地址" : subsonic ? "Subsonic 兼容服务地址" : "Navidrome 服务地址";
            editor.Server.PlaceholderText = emby ? "http://emby.example.com:8096/" : "https://music.example.com/";
            editor.Username.Visibility = editor.Password.Visibility = json ? Visibility.Collapsed : Visibility.Visible;
            editor.JsonDefinition.Visibility = json ? Visibility.Visible : Visibility.Collapsed;
            editor.Password.Header = emby ? "密码（编辑时留空保留登录令牌）" : "密码（编辑时留空保留已保存凭据）";
            editor.OtherDescription.Text = json
                ? "匿名 HTTP/JSON 只读音乐源。方案指定列表接口、受控分页与 id、title、url、cover、lyrics 等字段映射；音乐、封面与歌词地址必须与服务同源。支持播放、封面、歌词及加入本地歌单，不执行脚本或发送账号凭据，不支持需要鉴权或跨域 CDN 的接口。修改地址或方案请新建音乐库。"
                : emby
                ? "保存前验证连接并读取歌曲。登录令牌由 Windows 加密保存，不保存原始密码；支持播放、封面及加入本地歌单，歌词当前支持音频内嵌歌词。服务端歌单写入将在后续支持。修改服务或账号请新建音乐库。"
                : subsonic
                    ? "填写兼容 Subsonic API 的服务根地址，包含服务器的部署子路径。保存前验证连接，并按服务端专辑目录读取歌曲；凭据由 Windows 加密保存。支持播放、封面及加入本地歌单，服务歌词取决于接口支持，也可读取音频内嵌歌词。当前不修改服务端歌单。修改来源类型、服务或账号请新建音乐库。"
                    : "保存前验证连接并读取歌曲。凭据由 Windows 加密保存；支持播放、封面、歌词及加入本地歌单。服务端歌单写入将在后续支持。修改来源类型、服务或账号请新建音乐库。";
            bool current = api is not null && IsCurrentSettingsClient(api)
                && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadSettings | MusicSourceCapabilities.WriteSettings);
            bool writable = remoteWritable && current;
            authorized.IsEnabled = writable && row is null;
            editor.NasPath.IsEnabled = editor.Metadata.IsEnabled = editor.Lyric.IsEnabled = writable;
            string jsonError = "";
            if (json)
            {
                try { _ = HttpJsonDefinition.Parse(editor.JsonDefinition.Text); }
                catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or InvalidDataException)
                { jsonError = "JSON 字段方案无效，请检查字段名称、接口路径和分页设置。"; }
            }
            editor.Dialog.IsPrimaryButtonEnabled = !ct.IsCancellationRequested && (local
                ? !string.IsNullOrWhiteSpace(editor.LocalPath.Text) && (row is null || !string.IsNullOrWhiteSpace(editor.Name.Text))
                : nas ? writable && editor.Metadata.SelectedIndex >= 0 && !string.IsNullOrWhiteSpace(editor.NasPath.Text)
                : other && !string.IsNullOrWhiteSpace(editor.NavName.Text) && !string.IsNullOrWhiteSpace(editor.Server.Text)
                    && (json ? jsonError.Length == 0 : !string.IsNullOrWhiteSpace(editor.Username.Text)
                    && (row?.Navidrome is not null || row?.Emby is not null || editor.Password.Password.Length > 0)));
            editor.Status.Text = nas && !writable ? "飞牛音乐库管理需要已连接的管理员账号；当前仅可查看。" : jsonError;
        }
        editor.Kind.SelectionChanged += (_, _) => Update();
        editor.LocalPath.TextChanged += (_, _) => Update(); editor.Name.TextChanged += (_, _) => Update();
        editor.NasPath.TextChanged += (_, _) => Update(); editor.Metadata.SelectionChanged += (_, _) => Update();
        editor.NavName.TextChanged += (_, _) => Update(); editor.Server.TextChanged += (_, _) => Update(); editor.Username.TextChanged += (_, _) => Update(); editor.Password.PasswordChanged += (_, _) => Update();
        editor.JsonDefinition.TextChanged += (_, _) => Update();
        authorized.SelectionChanged += (_, _) => { if (authorized.SelectedItem is string path) editor.NasPath.Text = path; };
        browse.Click += async (_, _) => await PickLocalFolderAsync(editor.LocalPath, browse);
        editor.Kind.SelectedIndex = row?.HttpJson is not null ? 5 : row?.Emby is not null ? 3 : row?.Navidrome is { } connection ? connection.Provider == "subsonic" ? 4 : 2 : row?.Remote is null ? 0 : 1; Update();
        using var canceled = ct.Register(() => DispatcherQueue.TryEnqueue(() => editor.Dialog.Hide()));
#if DEBUG
        editor.Dialog.Opened += (_, _) => libraryEditorOpened?.Invoke(editor);
#endif
        try { return await ShowThemedDialogAsync(editor.Dialog) == ContentDialogResult.Primary && !ct.IsCancellationRequested ? editor.Read() : null; }
        finally { editor.Password.Password = ""; }
    }
    private async Task<bool> ConfirmLibraryDeleteAsync(LibraryManagerRow row, CancellationToken ct)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "删除音乐库", PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = row.Name + "\n" + row.Directory + "\n\n" + (row.Local is not null
                ? "仅取消导入此音乐库，原音乐文件和歌单引用保留。" : row.HttpJson is not null ? "仅移除此客户端的连接方案，服务端音乐与本地歌单引用保留。" : row.Navidrome is not null || row.Emby is not null ? "仅移除此客户端的连接和保存凭据，服务端音乐与本地歌单引用保留。" : "从飞牛服务器移除此素材库及其索引，网页端也会同步删除；原音乐文件保留。"), TextWrapping = TextWrapping.Wrap } };
        using var canceled = ct.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
#if DEBUG
        dialog.Opened += (_, _) => libraryDeleteOpened?.Invoke(dialog);
#endif
        return await ShowThemedDialogAsync(dialog) == ContentDialogResult.Primary && !ct.IsCancellationRequested;
    }
}
