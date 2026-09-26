using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private bool playlistDialogOpen;
    private readonly LocalPlaylistStore playlistStore = new(ThemeSettingsDirectory());
    private sealed record PlaylistRow(LocalPlaylistEntry Entry, string Text);
#if DEBUG
    private Action<ContentDialog>? playlistDialogOpened;
#endif

    private bool PlaylistTrackAvailable(LocalPlaylistEntry entry)
    {
        if (entry.IsCue || !ViewModel.PlaybackRouter.CanPlay(entry.Track)) return false;
        if (localSource?.Source.Id != entry.Track.SourceInstanceId) return true;
        try { return File.Exists(localSource.GetFilePath(entry.Track)); }
        catch (Exception) { return false; }
    }

    private async void LocalPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (playlistDialogOpen || closed) return;
        playlistDialogOpen = true;
        try { await ShowLocalPlaylistsAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "本地歌单无法读取，原文件已保留。"; }
        finally { playlistDialogOpen = false; }
    }

    private async Task ShowLocalPlaylistsAsync()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        var catalog = await playlistStore.LoadAsync(lifetime.Token);
        if (closed) return;
        var pickedTrack = TrackList.SelectedItem as MusicTrack;
        var picker = new ComboBox { Header = "选择本地歌单", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "歌单名称", MaxLength = 120, PlaceholderText = "输入名称后新建或改名" };
        var list = new ListView { DisplayMemberPath = "Text", SelectionMode = ListViewSelectionMode.Single, Height = 180 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var confirmDelete = new CheckBox { Content = "确认删除所选歌单（不会删除音乐文件）" };
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = "保存在本机，可混合已接入来源。离线或缺失条目保留，播放时跳过不可用条目。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(picker); panel.Children.Add(name);
        var catalogButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var create = new Button { Content = "新建" };
        var rename = new Button { Content = "改名" };
        var delete = new Button { Content = "删除歌单" };
        var refresh = new Button { Content = "刷新" };
        foreach (var button in new[] { create, rename, delete, refresh }) catalogButtons.Children.Add(button);
        panel.Children.Add(catalogButtons); panel.Children.Add(confirmDelete);
        var add = new Button { Content = "加入列表中所选歌曲", IsEnabled = pickedTrack is { SourceInstanceId: var sourceId } && sourceId != Guid.Empty };
        panel.Children.Add(add); panel.Children.Add(list);
        var entryButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var up = new Button { Content = "上移" }; var down = new Button { Content = "下移" }; var remove = new Button { Content = "移除歌曲" };
        foreach (var button in new[] { up, down, remove }) entryButtons.Children.Add(button);
        panel.Children.Add(entryButtons); panel.Children.Add(status);
        var editor = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "本地歌单", CloseButtonText = "关闭", PrimaryButtonText = "播放所选",
            Content = editor
        };
        bool rendering = false, busy = false;
        dialog.Closing += (_, args) => { if (busy && !closed) args.Cancel = true; };
        LocalPlaylist? Selected() => picker.SelectedItem as LocalPlaylist;
        void UpdateButtons()
        {
            var current = Selected(); var row = list.SelectedItem as PlaylistRow;
            rename.IsEnabled = current is not null; delete.IsEnabled = current is not null && confirmDelete.IsChecked == true;
            remove.IsEnabled = row is not null;
            up.IsEnabled = row is not null && list.SelectedIndex > 0;
            down.IsEnabled = row is not null && list.SelectedIndex < list.Items.Count - 1;
            dialog.IsPrimaryButtonEnabled = !busy && row is not null && PlaylistTrackAvailable(row.Entry);
            add.IsEnabled = current is not null && pickedTrack is not null && pickedTrack.SourceInstanceId != Guid.Empty;
        }
        void ShowEntries()
        {
            var current = Selected(); name.Text = current?.Name ?? ""; confirmDelete.IsChecked = false;
            list.ItemsSource = current?.Entries.Select(entry => new PlaylistRow(entry,
                $"{(PlaylistTrackAvailable(entry) ? "" : "[离线/不可用] ")}{entry.Title} · {entry.Artist}")).ToArray();
            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1; UpdateButtons();
        }
        void Render(Guid? playlistId = null, Guid? entryId = null)
        {
            rendering = true;
            picker.ItemsSource = catalog.Playlists;
            picker.SelectedItem = catalog.Playlists.FirstOrDefault(item => item.Id == playlistId) ?? catalog.Playlists.FirstOrDefault();
            ShowEntries();
            if (entryId is not null) list.SelectedItem = list.Items.Cast<PlaylistRow>().FirstOrDefault(row => row.Entry.Id == entryId);
            rendering = false; UpdateButtons();
        }
        async Task Save(LocalPlaylistCatalog next, Guid? playlistId, Guid? entryId = null)
        {
            if (busy) return;
            busy = true; editor.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false;
            try { catalog = await playlistStore.SaveAsync(next, lifetime.Token); status.Text = "已保存到本机。"; Render(playlistId, entryId); }
            catch (PlaylistConflictException)
            {
                try
                {
                    catalog = await playlistStore.LoadAsync(lifetime.Token);
                    status.Text = "歌单已在其他窗口修改，已重新读取；请检查后重试。"; Render(playlistId);
                }
                catch (Exception) { status.Text = "歌单已在其他窗口修改，重新读取失败；请刷新后重试。"; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { status.Text = "保存失败，原歌单保留，请检查名称或重试。"; }
            finally { busy = false; editor.IsEnabled = true; UpdateButtons(); }
        }
        async Task Replace(LocalPlaylist current, IReadOnlyList<LocalPlaylistEntry> entries, Guid? entryId = null) =>
            await Save(catalog with { Playlists = catalog.Playlists.Select(item => item.Id == current.Id ? current with { Entries = entries } : item).ToArray() }, current.Id, entryId);
        picker.SelectionChanged += (_, _) => { if (!rendering) ShowEntries(); };
        list.SelectionChanged += (_, _) => UpdateButtons();
        confirmDelete.Click += (_, _) => UpdateButtons();
        create.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { status.Text = "请输入歌单名称。"; return; }
            var item = new LocalPlaylist(Guid.NewGuid(), name.Text.Trim(), []);
            await Save(catalog with { Playlists = catalog.Playlists.Append(item).ToArray() }, item.Id);
        };
        rename.Click += async (_, _) =>
        {
            if (Selected() is not { } current || string.IsNullOrWhiteSpace(name.Text)) { status.Text = "请输入歌单名称。"; return; }
            await Save(catalog with { Playlists = catalog.Playlists.Select(item => item.Id == current.Id ? item with { Name = name.Text.Trim() } : item).ToArray() }, current.Id);
        };
        delete.Click += async (_, _) =>
        {
            if (Selected() is { } current && confirmDelete.IsChecked == true)
                await Save(catalog with { Playlists = catalog.Playlists.Where(item => item.Id != current.Id).ToArray() }, null);
        };
        add.Click += async (_, _) =>
        {
            if (Selected() is not { } current || pickedTrack is null || pickedTrack.SourceInstanceId == Guid.Empty) return;
            if (current.Entries.Any(entry => entry.Track == pickedTrack.Reference)) { status.Text = "该歌曲已在歌单中。"; return; }
            var entry = LocalPlaylistEntry.FromTrack(pickedTrack);
            await Replace(current, current.Entries.Append(entry).ToArray(), entry.Id);
        };
        remove.Click += async (_, _) =>
        {
            if (Selected() is { } current && list.SelectedItem is PlaylistRow row)
                await Replace(current, current.Entries.Where(entry => entry.Id != row.Entry.Id).ToArray());
        };
        async Task Move(int delta)
        {
            if (Selected() is not { } current || list.SelectedItem is not PlaylistRow row) return;
            var entries = current.Entries.ToList(); int from = entries.FindIndex(entry => entry.Id == row.Entry.Id), to = from + delta;
            if (from < 0 || to < 0 || to >= entries.Count) return;
            (entries[from], entries[to]) = (entries[to], entries[from]); await Replace(current, entries, row.Entry.Id);
        }
        up.Click += async (_, _) => await Move(-1); down.Click += async (_, _) => await Move(1);
        refresh.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; editor.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false;
            try { var selected = Selected()?.Id; catalog = await playlistStore.LoadAsync(lifetime.Token); Render(selected); status.Text = "已刷新。"; }
            catch (Exception) { status.Text = "刷新失败，当前列表保留。"; }
            finally { busy = false; editor.IsEnabled = true; UpdateButtons(); }
        };
        Render();
#if DEBUG
        dialog.Opened += (_, _) => playlistDialogOpened?.Invoke(dialog);
#endif
        var result = await dialog.ShowAsync();
        lifetime.Cancel();
        if (result != ContentDialogResult.Primary || closed || Selected() is not { } chosen || list.SelectedItem is not PlaylistRow start) return;
        await PlayLocalPlaylistAsync(chosen, start.Entry);
    }

    private async Task PlayLocalPlaylistAsync(LocalPlaylist chosen, LocalPlaylistEntry start)
    {
        var playable = chosen.Entries.Where(PlaylistTrackAvailable).Select(entry => entry.ToTrack()).ToArray();
        if (queue.Replace(playable, start.Track))
        {
            queueOrigin = "本地歌单 · " + chosen.Name;
            await PlayTrackAsync(start.ToTrack());
        }
    }
}
