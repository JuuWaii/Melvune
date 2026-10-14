#if DEBUG
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private sealed class NavidromeWindowHandler(byte[] wave, byte[] cover, string title = "Nav 受控曲目", Action? requestSeen = null,
        HttpStatusCode? lyricsStatus = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            requestSeen?.Invoke();
            string method = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            if (method == "getLyricsBySongId" && lyricsStatus is { } failure)
                return Task.FromResult(new HttpResponseMessage(failure) { Content = new StringContent("fixture lyrics failure") });
            if (method == "stream")
            {
                var range = request.Headers.Range!.Ranges.Single(); long from = range.From!.Value, to = Math.Min(range.To!.Value, wave.Length - 1);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(wave[(int)from..((int)to + 1)]) };
                response.Content.Headers.ContentType = new("audio/wav"); response.Content.Headers.ContentRange = new(from, to, wave.Length); return Task.FromResult(response);
            }
            if (method == "getCoverArt")
            { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) }; response.Content.Headers.ContentType = new("image/png"); return Task.FromResult(response); }
            string json = method switch
            {
                "ping" => "{\"subsonic-response\":{\"status\":\"ok\"}}",
                "getAlbumList2" => "{\"subsonic-response\":{\"status\":\"ok\",\"albumList2\":{\"album\":[{\"id\":\"album-one\"}]}}}",
                "getAlbum" => JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["subsonic-response"] = new { status = "ok", album = new { id = "album-one",
                        song = new[] { new { id = "same", title, artist = "验证歌手", album = "验证专辑", albumId = "album-one", duration = 60, coverArt = "cover" } } } }
                }),
                "search3" => JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["subsonic-response"] = new { status = "ok", searchResult3 = new
                    { song = new[] { new { id = "same", title, artist = "验证歌手", album = "验证专辑", duration = 60, coverArt = "cover" } } } }
                }),
                "getLyricsBySongId" => "{\"subsonic-response\":{\"status\":\"ok\",\"lyricsList\":{\"structuredLyrics\":[{\"synced\":true,\"line\":[{\"start\":0,\"value\":\"第一行\"},{\"start\":10000,\"value\":\"第二行\"}]}]}}}",
                _ => throw new InvalidOperationException("Unexpected fixture route")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private async Task VerifyNavidromeWindowAsync()
    {
        var checks = new Dictionary<string, bool>(); string? error = null; string stage = "fixture";
        void Check(string name, bool value) { checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        async Task Until(Func<bool> predicate) { for (int i = 0; i < 200 && !predicate(); i++) await Task.Delay(50); if (!predicate()) throw new TimeoutException(stage); }
        try
        {
            music.Player.IsMuted = true;
            using var audio = PlaybackVerification.CreateWave(); using var bytes = new MemoryStream(); await audio.CopyToAsync(bytes);
            byte[] wave = bytes.ToArray(), cover = await ArtworkFixture.EncodeAsync(160, 160);
            navidromeTestFactory = (connection, credentials) => new(connection.SourceId, connection.Name, connection.ServerAddress, credentials, () => new NavidromeWindowHandler(wave, cover));
            await ShowSyntheticPreviewAsync(); navidromeLoaded = true;
            stage = "create-dialog";
            PlaylistPageView? manager = null; localLibraryPageOpened = view => manager = view;
            await ShowLocalLibraryManagementAsync(); var view = manager!;
            var add = FindSongVisual<Button>(FullPageHost, button => Equals(button.Tag, "新建音乐库")) ?? throw new InvalidOperationException("Missing create");
            void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await WithLibraryEditorAsync(view, () => Invoke(add), async editor =>
            {
                editor.Kind.SelectedIndex = 2;
                Check("nav_editor_dynamic_fields", editor.OtherFields.Visibility == Visibility.Visible && editor.LocalFields.Visibility == Visibility.Collapsed && !editor.Dialog.IsPrimaryButtonEnabled);
                editor.NavName.Text = "Nav 验证库"; editor.Server.Text = "https://nav.example.invalid/"; editor.Username.Text = "fixture"; editor.Password.Password = "fixture-only";
                await Task.Delay(100); Check("nav_editor_create_enabled", editor.Dialog.IsPrimaryButtonEnabled);
            }, save: true);
            await Until(() => navidromeConnections.Count == 1 && view.Editor.IsEnabled);
            var connection = navidromeConnections.Single(); var track = navidromeTracks[connection.SourceId].Single();
            Check("nav_created_and_registered", ViewModel.PlaybackRouter.CanPlay(track.Reference) && (await navidromeStore.LoadCredentialsAsync(connection.SourceId)) is not null);
            stage = "edit-dialog";
            var row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.Navidrome is not null);
            await WithLibraryEditorAsync(view, () => InvokeLibraryCard(view, row, "编辑音乐库"), editor =>
            {
                Check("nav_edit_preserves_account_identity", editor.Kind.SelectedIndex == 2 && editor.Server.IsReadOnly && editor.Username.IsReadOnly && editor.Password.Password.Length == 0);
                editor.NavName.Text = "Nav 重命名"; return Task.CompletedTask;
            }, save: true);
            Check("nav_rename_retains_identity", navidromeConnections.Single().SourceId == connection.SourceId && navidromeConnections.Single().Name == "Nav 重命名");
            string localDirectory = Path.Combine(AppDataDirectory.Resolve(), "nav-local-fixture"); Directory.CreateDirectory(localDirectory);
            await File.WriteAllBytesAsync(Path.Combine(localDirectory, "local.wav"), wave);
            var localLibrary = new LocalLibrary(Guid.NewGuid(), localDirectory, "跨库本地验证", "");
            var localCatalogBefore = await localLibraryStore.LoadAsync();
            ApplyLocalCatalog(await localLibraryStore.SaveAsync(localCatalogBefore with { Libraries = [localLibrary] }));
            var localSource = await LocalMusicSource.ScanAsync(localLibrary.Settings, default); ReplaceLocalSource(localSource);
            var localTrack = localSource.Tracks.Single();
            stage = "browse";
            await ShowAllMusicAsync(); BrowseSourceFilter.SelectedIndex = 3; await Until(() => allMusicLoads == 0 && total == 1);
            Check("nav_scope_hides_local_filters", LocalLibraryFilters.Visibility == Visibility.Collapsed && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            RefreshVisibleLocalIndex(); Check("local_background_refresh_respects_nav_scope", total == 1 && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            SearchInput.Text = "验证专辑"; await SearchAsync(); Check("nav_album_search", total == 1);
            SearchInput.Text = "不存在"; await SearchAsync(); Check("nav_empty_search", total == 0);
            SearchInput.Text = ""; await SearchAsync();
            stage = "play";
            queue.Replace([track], track.Reference); await PlayTrackAsync(track);
            await Until(() => mediaReady && currentLyrics.IsTimed && NowCover.Source is not null);
            Check("nav_native_play_cover_and_lyrics", music.Player.Source is not null && currentLyrics.Lines[0].Text == "第一行");
            music.Player.Pause(); await Until(() => music.Player.PlaybackSession.PlaybackState == global::Windows.Media.Playback.MediaPlaybackState.Paused);
            var playing = music.Player.Source;
            pendingSeek = 15; CommitSeek(); await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 15) < 1);
            Check("nav_native_seek", music.Player.PlaybackSession.CanSeek);
            ResetLibrary(); Check("fn_reset_preserves_nav_playback_and_queue", ReferenceEquals(playing, music.Player.Source) && queue.Current?.Reference == track.Reference && total == 1);
            var factory = navidromeTestFactory;
            navidromeTestFactory = (_, _) => throw new MusicApiException(MusicFailure.Rejected);
            await RefreshNavidromeAsync(default, force: true);
            Check("non_auth_catalog_rejection_retains_playback_and_queue", ReferenceEquals(playing, music.Player.Source) && queue.Current?.Reference == track.Reference && ViewModel.PlaybackRouter.CanPlay(track.Reference) && navidromeTracks[connection.SourceId].Count == 1);
            navidromeTestFactory = factory;
            stage = "playlist";
            var catalog = await playlistStore.LoadAsync(); var playlist = new LocalPlaylist(Guid.NewGuid(), "跨源验证", [LocalPlaylistEntry.FromTrack(track), LocalPlaylistEntry.FromTrack(localTrack)]);
            await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Append(playlist).ToArray() });
            var entries = (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries;
            var entry = entries.Single(item => item.Track == track.Reference);
            Check("nav_local_playlist_persisted_and_available", entry.Track == track.Reference && PlaylistTrackAvailable(entry));
            Check("mixed_local_nav_playlist_preserves_order", entries.Count == 2 && entries[0].Track == track.Reference && entries[1].Track == localTrack.Reference && entries.All(PlaylistTrackAvailable));
            stage = "restart";
            RemoveNavidromeSource(connection.SourceId); navidromeLoaded = false; await RefreshNavidromeAsync(default);
            Check("nav_restore_same_identity", ViewModel.PlaybackRouter.CanPlay(entry.Track) && navidromeTracks[connection.SourceId].Single().Reference == track.Reference);
            stage = "delete";
            await ShowLocalLibraryManagementAsync(); view = manager!;
            row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.Navidrome is not null);
            await WithLibraryDeleteAsync(view, row, accept: false, Check);
            Check("nav_delete_cancel_keeps_connection", (await navidromeStore.LoadAsync()).Count == 1 && PlaylistTrackAvailable(entry));
            await WithLibraryDeleteAsync(view, row, accept: true, Check);
            Check("nav_delete_preserves_offline_playlist", (await navidromeStore.LoadAsync()).Count == 0 && (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries[0].Track == entry.Track && !PlaylistTrackAvailable(entry));
            Check("nav_delete_keeps_local_playlist_entry_available", PlaylistTrackAvailable(entries[1]));
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name + ":" + ex.Message; }
        finally { navidromeTestFactory = null; localLibraryPageOpened = null; }
        await File.WriteAllTextAsync(Path.Combine(AppDataDirectory.Resolve(), "navidrome-window-verification.json"), JsonSerializer.Serialize(new { stage, error, checks }));
    }
}
#endif
