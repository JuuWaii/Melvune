using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private readonly LocalPlaylistStore playlistStore = new(ThemeSettingsDirectory());
    private sealed record PlaylistRow(LocalPlaylistEntry Entry, string Text)
    {
        public MusicTrack Track { get; } = Entry.ToTrack();
        public bool IsUnavailable { get; init; }
        public string SourceLabel { get; init; } = "";
        public string Title => Track.Title;
        public string BrowseArtist => (IsUnavailable ? "[离线/不可用] " : "")
            + string.Join(" · ", new[] { Track.BrowseArtist, SourceLabel }.Where(value => !string.IsNullOrWhiteSpace(value)));
        public string Album => Track.Album;
        public string DisplayDuration => Track.DisplayDuration;
    }
#if DEBUG
    private Action<PlaylistPageView>? playlistPageOpened;
    private event Action? playlistWriteCompleted;
#endif

    private bool PlaylistTrackAvailable(LocalPlaylistEntry entry)
    {
        if (entry.IsCue || !ViewModel.PlaybackRouter.CanPlay(entry.Track)) return false;
        if (navidromeSources.ContainsKey(entry.Track.SourceInstanceId))
            return navidromeTracks.GetValueOrDefault(entry.Track.SourceInstanceId)?.Any(track => track.Reference == entry.Track) == true;
        if (embySources.ContainsKey(entry.Track.SourceInstanceId))
            return embyTracks.GetValueOrDefault(entry.Track.SourceInstanceId)?.Any(track => track.Reference == entry.Track) == true;
        if (!localSources.TryGetValue(entry.Track.SourceInstanceId, out var source)) return true;
        try { return File.Exists(source.GetFilePath(entry.Track)); }
        catch (Exception) { return false; }
    }

    private string PlaylistSourceLabel(LocalPlaylistEntry entry)
    {
        var library = localCatalog.Libraries.FirstOrDefault(item => item.SourceId == entry.Track.SourceInstanceId);
        return library is not null ? (library.Group.Length > 0 ? library.Group + " / " : "") + library.Name
            : navidromeConnections.FirstOrDefault(item => item.SourceId == entry.Track.SourceInstanceId)?.Name
                ?? embyConnections.FirstOrDefault(item => item.SourceId == entry.Track.SourceInstanceId)?.Name
                ?? (ViewModel.AuthenticatedClient?.SourceInstanceId == entry.Track.SourceInstanceId ? "飞牛音乐" : "离线来源");
    }

    private async void LocalPlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (closed) return;
        try { await ShowPlaylistManagerAsync(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "本地歌单无法读取，原文件已保留。"; }
    }

    private async Task ShowLocalPlaylistsAsync(Guid? selectedPlaylist = null, bool createNew = false, MusicTrack? picked = null)
    {
        var catalog = LocalPlaylistCatalog.Empty;
        if (closed) return;
        if (createNew)
        {
            int version = contentNavigationVersion;
            var created = await CreateLocalPlaylistFromDialogAsync();
            if (created is null || closed || version != contentNavigationVersion) return;
            selectedPlaylist = created;
        }
        var pickedTrack = picked ?? TrackList.SelectedItem as MusicTrack;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 180 };
        ConfigureSongList(list);
        list.ContextRequested += TrackList_ContextRequested;
        list.ContextCanceled += TrackContext_Canceled;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = "保存在本机，可混合已接入来源。离线或缺失条目保留，播放时跳过不可用条目。", TextWrapping = TextWrapping.Wrap });
        var refresh = new Button { Content = "刷新" };
        panel.Children.Add(new TextBlock { Text = pickedTrack is null ? "曲目列表尚未选择歌曲。" : "列表中所选歌曲：" + pickedTrack.Title + " · " + pickedTrack.Artist, TextWrapping = TextWrapping.Wrap });
        var add = new Button { Content = "加入列表中所选歌曲", IsEnabled = pickedTrack is { SourceInstanceId: var sourceId } && sourceId != Guid.Empty };
        var relink = new Button { Content = "将所选条目重新关联到列表歌曲" };
        panel.Children.Add(add);
        panel.Children.Add(new TextBlock { Text = "文件移动或改名后，先在曲目列表选中新歌曲，再重新关联歌单条目；保留条目的顺序。", TextWrapping = TextWrapping.Wrap });
        var entryButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var up = new Button { Content = "上移" }; var down = new Button { Content = "下移" }; var remove = new Button { Content = "移除歌曲" };
        foreach (var button in new[] { up, down, remove, relink, refresh }) entryButtons.Children.Add(button);
        var view = new PlaylistPageView("本地歌单", panel, list, entryButtons, status, localLifetime.Token);
        var editor = view.Editor;
        var ct = view.Lifetime.Token;
        bool busy = false;
        Guid? currentId = selectedPlaylist;
        LocalPlaylist? Selected() => catalog.Playlists.FirstOrDefault(item => item.Id == currentId);
        void UpdateButtons()
        {
            var current = Selected(); var row = list.SelectedItem as PlaylistRow;
            remove.IsEnabled = row is not null;
            up.IsEnabled = row is not null && list.SelectedIndex > 0;
            down.IsEnabled = row is not null && list.SelectedIndex < list.Items.Count - 1;
            view.PlayButton.IsEnabled = !busy && row is not null && PlaylistTrackAvailable(row.Entry);
            add.IsEnabled = current is not null && pickedTrack is not null && pickedTrack.SourceInstanceId != Guid.Empty;
            relink.IsEnabled = row is not null && add.IsEnabled;
        }
        void ShowEntries()
        {
            var current = Selected();
            view.TitleText.Text = current?.Name ?? "本地歌单";
            view.LocalPlaylistId = current?.Id;
            list.ItemsSource = current?.Entries.Select(entry => new PlaylistRow(entry,
                $"{(PlaylistTrackAvailable(entry) ? "" : "[离线/不可用] ")}{entry.Title} · {entry.Artist} · {PlaylistSourceLabel(entry)}")
                { IsUnavailable = !PlaylistTrackAvailable(entry), SourceLabel = PlaylistSourceLabel(entry) }).ToArray();
            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1; UpdateButtons();
        }
        void Render(Guid? playlistId = null, Guid? entryId = null)
        {
            currentId = (catalog.Playlists.FirstOrDefault(item => item.Id == playlistId) ?? catalog.Playlists.FirstOrDefault())?.Id;
            ShowEntries();
            if (entryId is not null) list.SelectedItem = list.Items.Cast<PlaylistRow>().FirstOrDefault(row => row.Entry.Id == entryId);
            UpdateButtons();
            if (ReferenceEquals(activePlaylistPage, view)) RenderLocalPlaylistNavigation(catalog, currentId);
        }
        async Task Save(LocalPlaylistCatalog next, Guid? playlistId, Guid? entryId = null)
        {
            if (busy || ct.IsCancellationRequested) return;
            busy = view.BlockNavigation = true; editor.IsEnabled = false; view.PlayButton.IsEnabled = false;
            try { catalog = await playlistStore.SaveAsync(next, ct); ct.ThrowIfCancellationRequested(); status.Text = "已保存到本机。"; Render(playlistId, entryId); }
            catch (PlaylistConflictException)
            {
                try
                {
                    catalog = await playlistStore.LoadAsync(ct);
                    status.Text = "歌单已在其他窗口修改，已重新读取；请检查后重试。"; Render(playlistId);
                }
                catch (Exception) { status.Text = "歌单已在其他窗口修改，重新读取失败；请刷新后重试。"; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { status.Text = "保存失败，原歌单保留，请检查名称或重试。"; }
            finally
            {
                busy = view.BlockNavigation = false; editor.IsEnabled = true; UpdateButtons();
#if DEBUG
                playlistWriteCompleted?.Invoke();
#endif
            }
        }
        async Task Replace(LocalPlaylist current, IReadOnlyList<LocalPlaylistEntry> entries, Guid? entryId = null) =>
            await Save(catalog with { Playlists = catalog.Playlists.Select(item => item.Id == current.Id ? current with { Entries = entries } : item).ToArray() }, current.Id, entryId);
        list.SelectionChanged += (_, _) => UpdateButtons();
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
        relink.Click += async (_, _) =>
        {
            if (Selected() is not { } current || list.SelectedItem is not PlaylistRow row || pickedTrack is null || pickedTrack.SourceInstanceId == Guid.Empty) return;
            if (current.Entries.Any(entry => entry.Id != row.Entry.Id && entry.Track == pickedTrack.Reference))
            { status.Text = "目标歌曲已在此歌单中，请先处理重复条目。"; return; }
            var replacement = LocalPlaylistEntry.FromTrack(pickedTrack) with { Id = row.Entry.Id };
            await Replace(current, current.Entries.Select(entry => entry.Id == row.Entry.Id ? replacement : entry).ToArray(), replacement.Id);
        };
        async Task Move(int delta)
        {
            if (Selected() is not { } current || list.SelectedItem is not PlaylistRow row) return;
            var entries = current.Entries.ToList(); int from = entries.FindIndex(entry => entry.Id == row.Entry.Id), to = from + delta;
            if (from < 0 || to < 0 || to >= entries.Count) return;
            (entries[from], entries[to]) = (entries[to], entries[from]); await Replace(current, entries, row.Entry.Id);
        }
        up.Click += async (_, _) => await Move(-1); down.Click += async (_, _) => await Move(1);
        async Task Reload()
        {
            if (busy || ct.IsCancellationRequested) return;
            busy = true; editor.IsEnabled = false; view.PlayButton.IsEnabled = false;
            try { var selected = Selected()?.Id; catalog = await playlistStore.LoadAsync(ct); ct.ThrowIfCancellationRequested(); Render(selected); status.Text = "已刷新。"; }
            catch (Exception) { status.Text = "刷新失败，当前列表保留。"; }
            finally { busy = false; editor.IsEnabled = true; UpdateButtons(); }
        }
        refresh.Click += async (_, _) => await Reload();
        view.Reload = Reload;
        view.PlayButton.Click += async (_, _) =>
        {
            if (!ct.IsCancellationRequested && Selected() is { } chosen && list.SelectedItem is PlaylistRow start && PlaylistTrackAvailable(start.Entry))
                await PlayLocalPlaylistAsync(chosen, start.Entry);
        };
        view.SourceChanged = () =>
        {
            var selected = (list.SelectedItem as PlaylistRow)?.Entry.Id;
            ShowEntries();
            list.SelectedItem = list.Items.Cast<PlaylistRow>().FirstOrDefault(row => row.Entry.Id == selected);
        };
        if (!await MountPlaylistPageAsync(view)) return;
        busy = true; editor.IsEnabled = false; status.Text = "正在读取本地歌单和音乐来源…";
        try
        {
            await RestoreLocalSourcesAsync(ct); catalog = await playlistStore.LoadAsync(ct); ct.ThrowIfCancellationRequested();
            Render(selectedPlaylist);
            status.Text = catalog.Playlists.Count == 0 ? "暂无本地歌单，点击左侧 + 新建。" : "本地歌单已加载；右键左侧歌单可重命名或删除。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception) { status.Text = "本地歌单无法读取，原文件已保留；请刷新后重试。"; }
        finally { busy = false; editor.IsEnabled = true; UpdateButtons(); }
#if DEBUG
        if (ReferenceEquals(activePlaylistPage, view)) playlistPageOpened?.Invoke(view);
#endif
    }

    private async Task PlayLocalPlaylistAsync(LocalPlaylist chosen, LocalPlaylistEntry start)
    {
        var playable = chosen.Entries.Where(PlaylistTrackAvailable).Select(entry => entry.ToTrack()).ToArray();
        if (TryReplacePlaybackQueue(playable, start.Track))
        {
            queueOrigin = "本地歌单 · " + chosen.Name;
            await PlayTrackAsync(start.ToTrack());
        }
    }
}
