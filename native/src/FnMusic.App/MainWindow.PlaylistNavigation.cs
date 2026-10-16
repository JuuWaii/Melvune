using FnMusic.App.Services;
using System.Text.Json;
using FnMusic.Core;
using Microsoft.UI.Xaml.Input;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? playlistNavigationWork;
    private long localNavigationRevision = -1;
    private int nasNavigationVersion;

    private bool separateNasPlaylistsInSidebar;
    private string? sidebarSelectedPlaylistKey;
    private bool updatingPlaylistGroupingControls = true;
    private sealed record SidebarPlaylistRow(object Value)
    {
        public override string ToString() => Name;
        public string Name => Value is LocalPlaylist local ? local.Name : ((NasPlaylist)Value).Name;
        public string Key => Value is LocalPlaylist local ? "local:" + local.Id : "nas:" + ((NasPlaylist)Value).Reference;
        public Visibility NasVisibility => Value is NasPlaylist ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LocalVisibility => Value is LocalPlaylist ? Visibility.Visible : Visibility.Collapsed;
    }
    private sealed record PlaylistNavigationPreferences(bool SeparateNasPlaylists = false, int Version = 2);
    private static PlaylistNavigationPreferences ReadPlaylistNavigationPreferences(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (root.TryGetProperty("Version", out var version) && version.GetInt32() is not 1 and not 2) throw new JsonException();
        // Preserve the old toggle's on/off choice, while both new modes now include FN playlists.
        bool separate = root.TryGetProperty("SeparateNasPlaylists", out var current) ? current.GetBoolean()
            : root.TryGetProperty("ShowNasPlaylists", out var legacy) && legacy.GetBoolean();
        return new(separate);
    }
    private string PlaylistNavigationPreferencesFile => Path.Combine(AppDataDirectory.Resolve(), "playlist-navigation.json");
    private void InitializePlaylistNavigationPreferences()
    {
        try
        {
            if (File.Exists(PlaylistNavigationPreferencesFile) && new FileInfo(PlaylistNavigationPreferencesFile).Length <= 4096)
                separateNasPlaylistsInSidebar = ReadPlaylistNavigationPreferences(File.ReadAllText(PlaylistNavigationPreferencesFile)).SeparateNasPlaylists;
        }
        catch (Exception) { separateNasPlaylistsInSidebar = false; }
        SeparateNasPlaylistsToggle.IsOn = separateNasPlaylistsInSidebar;
        updatingPlaylistGroupingControls = false;
        RenderUnifiedPlaylistNavigation();
    }
    private void PlaylistGrouping_Toggled(object sender, RoutedEventArgs args)
    { if (!updatingPlaylistGroupingControls && !closed) SetNasPlaylistSidebarGrouping(SeparateNasPlaylistsToggle.IsOn); }
    private void SetNasPlaylistSidebarGrouping(bool separate)
    {
        if (closed) return;
        separateNasPlaylistsInSidebar = separate;
        updatingPlaylistGroupingControls = true;
        try { SeparateNasPlaylistsToggle.IsOn = separate; } finally { updatingPlaylistGroupingControls = false; }
        RenderUnifiedPlaylistNavigation();
        string pending = PlaylistNavigationPreferencesFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
            File.WriteAllText(pending, JsonSerializer.Serialize(new PlaylistNavigationPreferences(separate)));
            File.Move(pending, PlaylistNavigationPreferencesFile, true);
        }
        catch (Exception) { UnifiedPlaylistStatus.Text = "歌单显示偏好未能保存，本次运行仍生效。"; }
        finally { try { if (File.Exists(pending)) File.Delete(pending); } catch (Exception) { } }
    }
    private void RenderUnifiedPlaylistNavigation()
    {
        if (closed) return;
        var api = ContextClient;
        bool includeNas = api is not null && IsCurrentPlaylistClient(api)
            && ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists);
        var rows = (LocalPlaylistNavigation.ItemsSource as IEnumerable<LocalPlaylist> ?? []).Select(item => new SidebarPlaylistRow(item))
            .Concat(includeNas ? (NasPlaylistNavigation.ItemsSource as IEnumerable<NasPlaylist> ?? []).Where(item => item.Reference.SourceInstanceId == api!.SourceInstanceId).Select(item => new SidebarPlaylistRow(item)) : [])
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
        void RenderList(ListView list, SidebarPlaylistRow[] items)
        {
            var previous = (list.ItemsSource as IEnumerable<SidebarPlaylistRow> ?? []).ToArray();
            if (!previous.SequenceEqual(items)) list.ItemsSource = items;
            list.SelectedItem = items.FirstOrDefault(item => item.Key == sidebarSelectedPlaylistKey);
        }
        var remote = rows.Where(row => row.Value is NasPlaylist).ToArray();
        var other = rows.Where(row => row.Value is LocalPlaylist).ToArray();
        RenderList(UnifiedPlaylistNavigation, rows);
        RenderList(GroupedNasPlaylistNavigation, remote);
        RenderList(GroupedOtherPlaylistNavigation, other);
        SidebarGroupedPlaylists.Visibility = separateNasPlaylistsInSidebar ? Visibility.Visible : Visibility.Collapsed;
        MergedPlaylistHeader.Visibility = UnifiedPlaylistNavigation.Visibility = UnifiedPlaylistStatus.Visibility = separateNasPlaylistsInSidebar ? Visibility.Collapsed : Visibility.Visible;
        UnifiedPlaylistStatus.Text = rows.Length == 0 ? "暂无歌单，点击 + 新建。" : "";
        GroupedNasPlaylistStatus.Text = remote.Length > 0 ? "" : includeNas ? "暂无飞牛歌单。" : "连接飞牛并登录后显示同步歌单。";
        GroupedOtherPlaylistStatus.Text = other.Length == 0 ? "暂无其他歌单，点击 + 新建。" : "";
    }

    private async void ManageMusic_Click(object sender, RoutedEventArgs args)
    { try { await ShowMusicManagementAsync(); } catch (Exception) { if (!closed) PlaybackStatus.Text = "音乐管理暂不可用，请重试。"; } }
    private async void MusicPlaylists_Click(object sender, RoutedEventArgs args)
    { try { await ShowMusicManagementAsync(playlists: true); } catch (Exception) { if (!closed) PlaybackStatus.Text = "歌单管理暂不可用，请重试。"; } }
    private async void UnifiedPlaylist_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (closed || args.ClickedItem is not SidebarPlaylistRow row) return;
        sidebarSelectedPlaylistKey = row.Key;
        RenderUnifiedPlaylistNavigation();
        try
        {
            if (row.Value is LocalPlaylist local)
            { LocalPlaylistNavigation.SelectedItem = local; await ShowLocalPlaylistsAsync(local.Id); }
            else if (row.Value is NasPlaylist remote && ViewModel.AuthenticatedClient is { } api
                && remote.Reference.SourceInstanceId == api.SourceInstanceId && RemoteSupports(MusicSourceCapabilities.ReadPlaylists))
            { NasPlaylistNavigation.SelectedItem = remote; await ShowNasPlaylistsAsync(api, remote.Reference); }
        }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "歌单无法打开，请到音乐管理刷新。"; }
    }
    private void UnifiedPlaylist_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (closed || sender is not ListView list) return;
        bool pointer = args.TryGetPosition(list, out var point);
        var origin = pointer ? args.OriginalSource as DependencyObject : FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        if (ResolveContextItem(list, origin, !pointer) is not SidebarPlaylistRow row) return;
        sidebarSelectedPlaylistKey = row.Key;
        RenderUnifiedPlaylistNavigation();
        list.SelectedItem = row;
        ShowContextMenu(CreatePlaylistNavigationMenu(row.Value), list, pointer ? point : null);
        args.Handled = true;
    }

    private void RenderLocalPlaylistNavigation(LocalPlaylistCatalog catalog, Guid? selected = null, bool background = false)
    {
        if (closed) return;
        if (background && catalog.Revision < localNavigationRevision) return;
        localNavigationRevision = catalog.Revision;
        if (selected is { } id) sidebarSelectedPlaylistKey = "local:" + id;
        LocalPlaylistNavigation.ItemsSource = catalog.Playlists;
        LocalPlaylistNavigation.SelectedItem = catalog.Playlists.FirstOrDefault(item => item.Id == selected);
        LocalPlaylistNavigationStatus.Text = catalog.Playlists.Count == 0 ? "暂无本地歌单，点击 + 新建。" : "";
        RenderUnifiedPlaylistNavigation();
    }
    private void RenderNasPlaylistNavigation(NasApiClient api, IReadOnlyList<NasPlaylist> playlists, PlaylistReference? selected = null, bool background = false)
    {
        if (closed || !IsCurrentPlaylistClient(api)) return;
        if (!background) ++nasNavigationVersion;
        if (selected is { } reference) sidebarSelectedPlaylistKey = "nas:" + reference;
        NasPlaylistNavigation.ItemsSource = playlists;
        NasPlaylistNavigation.SelectedItem = playlists.FirstOrDefault(item => item.Reference == selected);
        NasPlaylistNavigationStatus.Text = playlists.Count == 0 ? "暂无飞牛歌单。" : "";
        RenderUnifiedPlaylistNavigation();
    }
    private async Task RefreshPlaylistNavigationAsync()
    {
        playlistNavigationWork?.Cancel(); playlistNavigationWork?.Dispose();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(localLifetime.Token);
        playlistNavigationWork = work;
        work.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = work.Token;
        var api = ViewModel.AuthenticatedClient;
        int nasVersion = nasNavigationVersion;
        try
        {
            try
            {
                var catalog = await playlistStore.LoadAsync(ct);
                ct.ThrowIfCancellationRequested();
                RenderLocalPlaylistNavigation(catalog, (LocalPlaylistNavigation.SelectedItem as LocalPlaylist)?.Id, background: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { if (!closed) LocalPlaylistNavigationStatus.Text = "歌单读取失败，请打开本地歌单刷新。"; }
            if (api is null || !ViewModel.PlaybackRouter.Supports(api.SourceInstanceId, MusicSourceCapabilities.ReadPlaylists))
            { if (!closed) ClearNasPlaylistNavigation(); return; }
            try
            {
                var playlists = CurrentMetadata(api)?.Playlists.Select(item => item.Playlist).ToArray() ?? await api.ListPlaylistsAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (nasVersion == nasNavigationVersion) RenderNasPlaylistNavigation(api, playlists, (NasPlaylistNavigation.SelectedItem as NasPlaylist)?.Reference, background: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!closed && IsCurrentPlaylistClient(api)) NasPlaylistNavigationStatus.Text = "读取失败，请打开飞牛歌单刷新。";
            }
        }
        finally { if (ReferenceEquals(playlistNavigationWork, work)) playlistNavigationWork = null; }
    }
    private void ClearNasPlaylistNavigation()
    {
        ++nasNavigationVersion;
        NasPlaylistNavigation.ItemsSource = null;
        NasPlaylistNavigationStatus.Text = "连接飞牛并登录后显示歌单。";
        RenderUnifiedPlaylistNavigation();
    }
    private void ResetPlaylistNavigation()
    {
        if (fullPageView?.RemoteSource is not null) CloseFullPage(force: true, restoreFocus: false);
        CloseTrackContextMenu();
        ++contentNavigationVersion;
        playlistNavigationWork?.Cancel();
        ClearNasPlaylistNavigation();
        if (activePlaylistPage?.RemoteSource is not null)
        {
            ClosePlaylistPage();
            if (LyricsPanel.Visibility == Visibility.Visible)
            {
                lyricsPlaylistVisibility = lyricsSettingsVisibility = lyricsLibraryVisibility = Visibility.Collapsed;
                lyricsHomeVisibility = Visibility.Visible;
            }
            else HomePanel.Visibility = Visibility.Visible;
        }
    }
    private async void LocalPlaylistNavigation_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LocalPlaylist playlist || closed) return;
        try { await ShowLocalPlaylistsAsync(playlist.Id); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "本地歌单无法读取，请刷新后重试。"; }
    }
    private async void NewLocalPlaylist_Click(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        try { await ShowLocalPlaylistsAsync(createNew: true); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "本地歌单无法读取，请刷新后重试。"; }
    }
    private async void NasPlaylistNavigation_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not NasPlaylist playlist || ViewModel.AuthenticatedClient is not { } api
            || playlist.Reference.SourceInstanceId != api.SourceInstanceId || !RequireRemoteCapability(MusicSourceCapabilities.ReadPlaylists)) return;
        try { await ShowNasPlaylistsAsync(api, playlist.Reference); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "飞牛歌单无法读取，请刷新后重试。"; }
    }
    private async void NewNasPlaylist_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.AuthenticatedClient is not { } api
            || !RequireRemoteCapability(MusicSourceCapabilities.ReadPlaylists | MusicSourceCapabilities.WritePlaylists)) return;
        try { await ShowNasPlaylistsAsync(api, createNew: true); }
        catch (Exception) { if (!closed) PlaybackStatus.Text = "飞牛歌单无法读取，请刷新后重试。"; }
    }
}
