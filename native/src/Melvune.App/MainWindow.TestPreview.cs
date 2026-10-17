using Melvune.Core;
using Microsoft.UI.Xaml;

namespace Melvune.App;

public sealed partial class MainWindow
{
#if DEBUG
    private (MusicFailure Failure, int DelayMilliseconds)? syntheticLibraryFailure;
    private static readonly HashSet<string> syntheticFavorites = [];
    private MusicFailure? syntheticFavoriteFailure;
#endif
    private bool IsSyntheticPreview =>
#if DEBUG
        Environment.GetCommandLineArgs().Contains("--preview-synthetic");
#else
        false;
#endif

    private async Task<Stream> OpenPlaybackStreamAsync(MusicTrack track, CancellationToken ct)
    {
#if DEBUG
        if (IsSyntheticPreview && track.SourceInstanceId == Guid.Empty) return Services.PlaybackVerification.CreateWave();
#endif
        return await ViewModel.PlaybackRouter.OpenAsync(track.Reference, ct);
    }
#if DEBUG
    // 显式测试入口隔离真实账户与曲名；仍使用生产 XAML、事件和播放器。
    private async Task ShowSyntheticPreviewAsync()
    {
        Title = Environment.GetCommandLineArgs().Contains("--preview-collections") ? "Melvune · 合成专辑歌手验证" : Environment.GetCommandLineArgs().Contains("--preview-search") ? "Melvune · 合成搜索交互验证" : "Melvune · 合成音频交互验证";
        SettingsPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        if (Environment.GetCommandLineArgs().Contains("--preview-compact")) AppWindow.Resize(new global::Windows.Graphics.SizeInt32(820, 820));
        if (Environment.GetCommandLineArgs().Contains("--preview-light")) Root.RequestedTheme = ElementTheme.Light;
        if (Environment.GetCommandLineArgs().Contains("--preview-dark")) Root.RequestedTheme = ElementTheme.Dark;
        await LoadPageAsync(1);
        PlaybackStatus.Text = "合成音频交互验证，默认静音。";
        music.Player.IsMuted = true; UpdateVolumeControl();
        await LoadDevicesAsync();
        await RefreshPlaylistNavigationAsync();
        if (Environment.GetCommandLineArgs().Contains("--preview-embedded-lyrics"))
        { await LoadEmbeddedLyricsPreviewAsync(); return; }
        if (Environment.GetCommandLineArgs().Contains("--preview-management"))
        { await ShowLocalLibraryManagementAsync(createNew: true); return; }
        if (Environment.GetCommandLineArgs().Contains("--preview-playlists"))
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "playlist-preview-files");
            Directory.CreateDirectory(directory);
            for (int i = 1; i <= 3; i++)
            {
                string filePath = Path.Combine(directory, "preview-" + i + ".wav");
                if (!File.Exists(filePath))
                {
                    using var wave = Services.PlaybackVerification.CreateWave();
                    using var file = File.Create(filePath);
                    await wave.CopyToAsync(file);
                }
                await File.WriteAllTextAsync(Path.ChangeExtension(filePath, ".lrc"), "[00:00]合成歌单歌词\n[00:10]播放与来源验证");
                if (Environment.GetCommandLineArgs().Contains("--preview-artwork"))
                    await File.WriteAllBytesAsync(Path.ChangeExtension(filePath, ".png"), await Services.ArtworkFixture.EncodeAsync(2000, 1000));
            }
            var local = await Melvune.Infrastructure.LocalMusicSource.ScanAsync(new(Guid.NewGuid(), directory), default);
            ReplaceLocalSource(local);
            var tracks = local.Tracks.Select((track, i) => track with { Title = "合成歌曲 " + (i + 1), Artist = "测试音乐库", Album = "合成示例" }).ToArray();
            TrackList.ItemsSource = tracks; TrackList.SelectedIndex = 0; localView = true;
            var saved = await playlistStore.LoadAsync();
            var first = new LocalPlaylist(Guid.NewGuid(), "本地收藏", tracks.Select(track => LocalPlaylistEntry.FromTrack(track)).ToArray());
            var second = new LocalPlaylist(Guid.NewGuid(), "跨库与离线示例", [LocalPlaylistEntry.FromTrack(tracks[0]),
                LocalPlaylistEntry.FromTrack(tracks[0] with { SourceInstanceId = Guid.NewGuid(), Title = "离线歌曲（保留引用）" })]);
            await playlistStore.SaveAsync(saved with { Playlists = [first, second] });
            await ShowLocalPlaylistsAsync(first.Id);
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--preview-now-playing") && TrackList.SelectedItem is MusicTrack selected)
        {
            await StartQueueAsync(selected);
            for (int i = 0; i < 100 && !mediaReady; i++) await Task.Delay(20);
            Pause_Click(this, new RoutedEventArgs());
            UpdateProgress();
            await OpenNowPlayingAsync();
        }
    }
    private static async Task<TrackPage> GetSyntheticPageAsync(string query, int requestedPage, bool onlyFavorites = false)
    {
        var tracks = new List<MusicTrack>
        {
            new MusicTrack("synthetic-a", "合成测试音频 A", "测试夹具", 60, false),
            new MusicTrack("synthetic-b", "合成测试音频 B", "测试夹具", 60, false)
        };
        if (Environment.GetCommandLineArgs().Contains("--preview-search"))
        {
            tracks.AddRange(Enumerable.Range(1, 120).Select(i => new MusicTrack($"synthetic-page-{i}", $"分页测试 {i:000}", "测试夹具", 60, false)));
            // 专用测试请求不响应取消，模拟已到达的旧响应，验证生产写回检查。
            if (query == "slow") { await Task.Delay(20000); query = "分页测试"; }
        }
        var found = tracks.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) && (!onlyFavorites || syntheticFavorites.Contains(t.Id)))
            .Select(t => t with { IsFavorite = syntheticFavorites.Contains(t.Id) }).ToArray();
        return new TrackPage(found.Skip((requestedPage - 1) * 50).Take(50).ToArray(), found.Length);
    }
    private static CollectionPage GetSyntheticCollections(CollectionKind kind, int requestedPage)
    {
        string label = kind == CollectionKind.Album ? "专辑" : "歌手";
        if (!Environment.GetCommandLineArgs().Contains("--preview-collections"))
            return new CollectionPage([new("synthetic-collection", $"合成{label}", 2)], 1);
        var items = Enumerable.Range(1, 53).Select(i => new MusicCollection($"collection-{i}",
            i == 52 ? $"空{label}" : i == 53 ? $"延迟{label}（20 秒）" : $"合成{label} {i:000}", i == 52 ? 0 : 53)).ToArray();
        return new CollectionPage(items.Skip((requestedPage - 1) * 50).Take(50).ToArray(), items.Length);
    }
    private static async Task<TrackPage> GetSyntheticCollectionTracksAsync(MusicCollection item, int requestedPage)
    {
        if (!Environment.GetCommandLineArgs().Contains("--preview-collections"))
            return await GetSyntheticPageAsync("合成测试音频", requestedPage);
        // 故意忽略取消，验证迟到详情不得恢复旧标题、列表或分页按钮。
        if (item.Id == "collection-53") await Task.Delay(20000);
        int count = item.TrackCount ?? 0;
        var tracks = Enumerable.Range(1, count).Select(i => new MusicTrack($"{item.Id}-track-{i}", $"详情歌曲 {i:000}", "测试夹具", 60, false));
        return new TrackPage(tracks.Skip((requestedPage - 1) * 50).Take(50).ToArray(), count);
    }
#endif
}
