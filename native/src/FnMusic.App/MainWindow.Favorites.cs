using FnMusic.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private bool favoritesView, favoriteBusy;
    private CancellationTokenSource favoriteWork = new();
    private async void Favorites_Click(object sender, RoutedEventArgs e)
    { await ShowFavoritesAsync(); }
    private async Task ShowFavoritesAsync()
    {
        if (!RequireRemoteCapability(MusicSourceCapabilities.ReadFavorites)) return;
        AnimateViewEntry(LibraryPanel);
        allMusicView = false; localView = false;
        favoritesView = true; parentArtist = null; collectionKind = null; collection = null;
        SearchInput.Text = searchQuery = ""; UpdateCollectionView(); await LoadPageAsync(1);
    }
    private void TrackSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateFavoriteButton();
    private bool TrackSupports(MusicTrack track, MusicSourceCapabilities capability)
    {
#if DEBUG
        if (IsSyntheticPreview && track.SourceInstanceId == Guid.Empty)
            return capability is MusicSourceCapabilities.Playback or MusicSourceCapabilities.Artwork or MusicSourceCapabilities.Lyrics
                or MusicSourceCapabilities.WriteFavorites or MusicSourceCapabilities.ReadFavorites;
#endif
        return ViewModel.PlaybackRouter.Supports(track.SourceInstanceId, capability);
    }
    private bool CanFavoriteTrack(MusicTrack track) => TrackSupports(track, MusicSourceCapabilities.WriteFavorites)
        && (ViewModel.AuthenticatedClient?.SourceInstanceId == track.SourceInstanceId
#if DEBUG
            || (IsSyntheticPreview && track.SourceInstanceId == Guid.Empty)
#endif
        );
    private void UpdateFavoriteButton()
    {
        if (FavoriteToggle is null) return;
        TrackActionsButton.IsEnabled = TrackList.SelectedItem is MusicTrack;
        FavoriteToggle.IsEnabled = !favoriteBusy && TrackList.SelectedItem is MusicTrack selected && CanFavoriteTrack(selected);
        FavoriteToggle.Content = TrackList.SelectedItem is MusicTrack { IsFavorite: true } ? "取消收藏" : "收藏歌曲";
        ToolTipService.SetToolTip(FavoriteToggle, TrackList.SelectedItem is not MusicTrack ? "请先选择歌曲。"
            : FavoriteToggle.IsEnabled || favoriteBusy ? "收藏到歌曲所属的飞牛账号。"
            : localView ? "本地来源不支持远端收藏，可加入本地歌单。" : "当前歌曲来源未连接或不支持收藏。");
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e)
    { await ToggleFavoriteAsync(); }
    private async Task ToggleFavoriteAsync()
    {
        if (TrackList.SelectedItem is MusicTrack track) await ToggleFavoriteTrackAsync(track);
    }
    private async Task ToggleFavoriteTrackAsync(MusicTrack track)
    {
        if (favoriteBusy) return;
        if (!CanFavoriteTrack(track)) { FavoriteStatus.Text = "歌曲来源未连接或不支持收藏，可使用本地歌单。"; return; }
        var api = ViewModel.AuthenticatedClient;
        if (api is null && !IsSyntheticPreview) { FavoriteStatus.Text = "请先登录后使用收藏。"; return; }
        var ct = favoriteWork.Token;
        var viewToken = libraryWork.Token;
        bool desired = !track.IsFavorite;
        favoriteBusy = true; UpdateFavoriteButton(); FavoriteStatus.Text = "正在更新收藏…";
        try
        {
#if DEBUG
            if (IsSyntheticPreview)
            {
                if (syntheticFavoriteFailure is { } failure)
                { syntheticFavoriteFailure = null; throw new MusicApiException(failure); }
                if (desired) syntheticFavorites.Add(track.Id); else syntheticFavorites.Remove(track.Id);
            }
            else
#endif
            await api!.SetFavoriteAsync(track.Reference, desired, ct);
            if (closed || ct.IsCancellationRequested || !ReferenceEquals(api, ViewModel.AuthenticatedClient)) return;
            FavoriteStatus.Text = desired ? "已收藏" : "已取消收藏";
            // 只更新操作开始时的页面；浏览切换不影响正在进行的写入。
            if (!viewToken.IsCancellationRequested && TrackList.ItemsSource is IEnumerable<MusicTrack> tracks)
            {
                if (favoritesView && !desired)
                    await LoadPageAsync(page > 1 && tracks.Count() == 1 ? page - 1 : page);
                else
                {
                    var selectedReference = (TrackList.SelectedItem as MusicTrack)?.Reference;
                    var updated = tracks.Select(t => t.Reference == track.Reference ? t with { IsFavorite = desired } : t).ToArray();
                    TrackList.ItemsSource = updated;
                    TrackList.SelectedItem = updated.FirstOrDefault(t => t.Reference == selectedReference);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (MusicApiException ex) when (ex.Failure == MusicFailure.Unauthorized)
        {
            if (!closed && !ct.IsCancellationRequested && ReferenceEquals(api, ViewModel.AuthenticatedClient))
                await ViewModel.ExpireSessionAsync();
        }
        catch (Exception)
        {
            if (!closed && !ct.IsCancellationRequested)
                FavoriteStatus.Text = "未能确认收藏结果，请刷新列表核对后重试。";
        }
        finally { favoriteBusy = false; if (!closed) UpdateFavoriteButton(); }
    }
}
