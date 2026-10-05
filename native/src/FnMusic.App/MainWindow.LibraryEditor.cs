using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private sealed record LibraryManagerRow(LocalLibrary? Local, NasSharedLibrary? Remote, bool CanDelete)
    {
        public bool CanEdit { get; init; } = true;
        public string Key => Local is { } local ? "local:" + local.SourceId : "nas:" + Remote!.Library.Id;
        public string Name => Local?.Name ?? (string.IsNullOrWhiteSpace(Remote!.Library.Name) ? Path.GetFileName(Remote.Path.TrimEnd('/')) : Remote.Library.Name);
        public string Directory => Local?.Directory ?? Remote!.Path;
        public string Group => Local is { } local ? "本地音乐" + (local.Group.Length == 0 ? "" : " · " + local.Group) : "飞牛音乐";
    }
    private sealed record LibraryEditRequest(int Kind, string Name, string Group, string Path, string Metadata, bool Lyric);
    private sealed class LibraryEditorView
    {
        public ContentDialog Dialog { get; }
        public ComboBox Kind { get; } = new() { Header = "音乐来源", ItemsSource = new[] { "本地音乐", "飞牛音乐", "其他音乐（后续开发）" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        public TextBox Name { get; } = new() { Header = "库名称", MaxLength = 120, PlaceholderText = "留空使用文件夹名称" };
        public TextBox Group { get; } = new() { Header = "分组（可选）", MaxLength = 120 };
        public TextBox LocalPath { get; } = new() { Header = "文件夹路径", MaxLength = 4096, PlaceholderText = @"例如 D:\Music" };
        public TextBox NasPath { get; } = new() { Header = "NAS 目录路径", MaxLength = 4096, PlaceholderText = "选择授权目录或填写其子目录" };
        public ComboBox Metadata { get; } = new() { Header = "元数据优先策略", ItemsSource = new[] { "优先云端", "优先本地标签", "仅本地标签" }, SelectedIndex = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        public CheckBox Lyric { get; } = new() { Content = "自动下载歌词" };
        public StackPanel LocalFields { get; } = new() { Spacing = 10 };
        public StackPanel NasFields { get; } = new() { Spacing = 10 };
        public StackPanel OtherFields { get; } = new() { Spacing = 10 };
        public TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap };
        public LibraryEditorView(XamlRoot root, bool editing)
        {
            Dialog = new() { XamlRoot = root, Title = editing ? "编辑音乐库" : "新建音乐库", PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        }
        public LibraryEditRequest Read() => new(Kind.SelectedIndex, Name.Text.Trim(), Group.Text.Trim(),
            (Kind.SelectedIndex == 0 ? LocalPath.Text : NasPath.Text).Trim(),
            Metadata.SelectedIndex switch { 0 => "cloud_preferred", 2 => "local_only", _ => "local_preferred" }, Lyric.IsChecked == true);
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
        editor.OtherFields.Children.Add(new TextBlock { Text = "后续将支持 Emby、Navidrome 和自定义音乐源，当前暂不能创建。", TextWrapping = TextWrapping.Wrap });
        editor.OtherFields.Children.Add(new ComboBox { Header = "服务类型", ItemsSource = new[] { "Emby", "Navidrome", "自定义音乐源" }, SelectedIndex = 0, IsEnabled = false });
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
            editor.LocalFields.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            editor.NasFields.Visibility = nas ? Visibility.Visible : Visibility.Collapsed;
            editor.OtherFields.Visibility = !local && !nas ? Visibility.Visible : Visibility.Collapsed;
            bool current = api is not null && IsCurrentSettingsClient(api)
                && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadSettings | MusicSourceCapabilities.WriteSettings);
            bool writable = remoteWritable && current;
            authorized.IsEnabled = writable && row is null;
            editor.NasPath.IsEnabled = editor.Metadata.IsEnabled = editor.Lyric.IsEnabled = writable;
            editor.Dialog.IsPrimaryButtonEnabled = !ct.IsCancellationRequested && (local
                ? !string.IsNullOrWhiteSpace(editor.LocalPath.Text) && (row is null || !string.IsNullOrWhiteSpace(editor.Name.Text))
                : nas && writable && editor.Metadata.SelectedIndex >= 0 && !string.IsNullOrWhiteSpace(editor.NasPath.Text));
            editor.Status.Text = nas && !writable ? "飞牛音乐库管理需要已连接的管理员账号；当前仅可查看。" : "";
        }
        editor.Kind.SelectionChanged += (_, _) => Update();
        editor.LocalPath.TextChanged += (_, _) => Update(); editor.Name.TextChanged += (_, _) => Update();
        editor.NasPath.TextChanged += (_, _) => Update(); editor.Metadata.SelectionChanged += (_, _) => Update();
        authorized.SelectionChanged += (_, _) => { if (authorized.SelectedItem is string path) editor.NasPath.Text = path; };
        browse.Click += async (_, _) => await PickLocalFolderAsync(editor.LocalPath, browse);
        editor.Kind.SelectedIndex = row?.Remote is null ? 0 : 1; Update();
        using var canceled = ct.Register(() => DispatcherQueue.TryEnqueue(() => editor.Dialog.Hide()));
#if DEBUG
        editor.Dialog.Opened += (_, _) => libraryEditorOpened?.Invoke(editor);
#endif
        return await ShowThemedDialogAsync(editor.Dialog) == ContentDialogResult.Primary && !ct.IsCancellationRequested ? editor.Read() : null;
    }
    private async Task<bool> ConfirmLibraryDeleteAsync(LibraryManagerRow row, CancellationToken ct)
    {
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "删除音乐库", PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = row.Name + "\n" + row.Directory + "\n\n" + (row.Local is not null
                ? "仅取消导入此音乐库，原音乐文件和歌单引用保留。" : "从飞牛服务器移除此素材库及其索引，网页端也会同步删除；原音乐文件保留。"), TextWrapping = TextWrapping.Wrap } };
        using var canceled = ct.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
#if DEBUG
        dialog.Opened += (_, _) => libraryDeleteOpened?.Invoke(dialog);
#endif
        return await ShowThemedDialogAsync(dialog) == ContentDialogResult.Primary && !ct.IsCancellationRequested;
    }
}
