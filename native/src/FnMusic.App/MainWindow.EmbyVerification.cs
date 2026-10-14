#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using FnMusic.Core;
using FnMusic.Infrastructure;
using FnMusic.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Media.Playback;

namespace FnMusic.App;
public sealed partial class MainWindow
{
    private sealed class EmbyWindowTraffic
    {
        public int CoverReads, StreamReads;
    }
    private sealed class EmbyWindowHandler(byte[] wave, byte[] cover, EmbyWindowTraffic traffic) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            if (uri.Host != "emby.example.invalid" || !uri.AbsolutePath.StartsWith("/emby/", StringComparison.Ordinal)
                || !request.Headers.Contains("X-Emby-Token") || request.Method != HttpMethod.Get)
                throw new InvalidOperationException("Unexpected fixture request");
            string path = uri.AbsolutePath["/emby/".Length..];
            if (path == "Audio/same/stream")
            {
                if (!uri.Query.Contains("static=true", StringComparison.Ordinal)) throw new InvalidOperationException("Missing static playback");
                Interlocked.Increment(ref traffic.StreamReads);
                var range = request.Headers.Range!.Ranges.Single(); long from = range.From!.Value, to = Math.Min(range.To!.Value, wave.Length - 1);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(wave[(int)from..((int)to + 1)]) };
                response.Content.Headers.ContentType = new("audio/wav"); response.Content.Headers.ContentRange = new(from, to, wave.Length);
                return Task.FromResult(response);
            }
            if (path == "Items/same/Images/Primary")
            {
                Interlocked.Increment(ref traffic.CoverReads);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) };
                response.Content.Headers.ContentType = new("image/png"); return Task.FromResult(response);
            }
            string json = path switch
            {
                "Users/fixture-user" => "{\"Id\":\"fixture-user\"}",
                "Users/fixture-user/Items" => "{\"TotalRecordCount\":1,\"StartIndex\":0,\"Items\":[{\"Id\":\"same\",\"Type\":\"Audio\",\"Name\":\"Emby 受控曲目\",\"Artists\":[\"验证歌手\"],\"ArtistItems\":[{\"Id\":\"artist\",\"Name\":\"验证歌手\"}],\"Album\":\"验证专辑\",\"AlbumId\":\"album\",\"RunTimeTicks\":600000000,\"ImageTags\":{\"Primary\":\"fixture\"}}]}",
                _ => throw new InvalidOperationException("Unexpected fixture route")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private async Task VerifyEmbyWindowAsync()
    {
        var checks = new Dictionary<string, bool>(); string? error = null; string stage = "fixture";
        double? firstPlayingPosition = null;
        void Check(string name, bool value) { checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        async Task Until(Func<bool> predicate) { for (int i = 0; i < 240 && !predicate(); i++) await Task.Delay(50); if (!predicate()) throw new TimeoutException(); }
        try
        {
            music.Player.IsMuted = true;
            using var audio = PlaybackVerification.CreateWave(); using var bytes = new MemoryStream(); await audio.CopyToAsync(bytes);
            byte[] wave = bytes.ToArray(), cover = await ArtworkFixture.EncodeAsync(160, 160);
            var traffic = new EmbyWindowTraffic(); int authenticationCalls = 0;
            const string fixtureInput = "fixture-only";
            embyTestFactory = (connection, credentials) => new(connection.SourceId, connection.Name, connection.ServerAddress, credentials, () => new EmbyWindowHandler(wave, cover, traffic));
            embyAuthTestFactory = (server, username, suppliedSecret, ct) =>
            {
                ct.ThrowIfCancellationRequested(); authenticationCalls++;
                if (username != "fixture" || suppliedSecret != fixtureInput) throw new InvalidOperationException("Unexpected fixture account");
                return Task.FromResult(new EmbyCredentials("fixture-user", "fixture-token"));
            };
            await ShowSyntheticPreviewAsync(); embyLoaded = true;
            stage = "create-dialog";
            PlaylistPageView? manager = null; localLibraryPageOpened = view => manager = view;
            await ShowLocalLibraryManagementAsync(); var view = manager!;
            var add = FindSongVisual<Button>(FullPageHost, button => Equals(button.Tag, "新建音乐库")) ?? throw new InvalidOperationException();
            void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await WithLibraryEditorAsync(view, () => Invoke(add), async editor =>
            {
                editor.Kind.SelectedIndex = 3;
                Check("emby_editor_dynamic_fields", editor.Kind.Items.Count == 5 && editor.OtherFields.Visibility == Visibility.Visible
                    && editor.LocalFields.Visibility == Visibility.Collapsed && Equals(editor.Server.Header, "Emby 服务地址") && !editor.Dialog.IsPrimaryButtonEnabled);
                Check("emby_editor_explains_embedded_lyrics_and_token_storage", editor.OtherDescription.Text.Contains("音频内嵌") && editor.OtherDescription.Text.Contains("不保存原始密码"));
                editor.NavName.Text = "Emby 验证库"; editor.Server.Text = "https://emby.example.invalid/"; editor.Username.Text = "fixture"; editor.Password.Password = fixtureInput;
                await Task.Delay(100); Check("emby_editor_create_enabled", editor.Dialog.IsPrimaryButtonEnabled);
            }, save: true);
            await Until(() => embyConnections.Count == 1 && view.Editor.IsEnabled);
            var connection = embyConnections.Single(); var track = embyTracks[connection.SourceId].Single();
            Check("emby_created_registered_and_authenticated", ViewModel.PlaybackRouter.CanPlay(track.Reference) && authenticationCalls == 1
                && (await embyStore.LoadCredentialsAsync(connection.SourceId))?.UserId == "fixture-user");
            stage = "edit-dialog";
            var row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.Emby is not null);
            Check("emby_manager_row_identity_and_status", row.Key == "emby:" + connection.SourceId && row.Group.StartsWith("Emby", StringComparison.Ordinal) && row.CanEdit && row.CanDelete);
            await WithLibraryEditorAsync(view, () => InvokeLibraryCard(view, row, "编辑音乐库"), editor =>
            {
                Check("emby_edit_preserves_account_identity", editor.Kind.SelectedIndex == 3 && !editor.Kind.IsEnabled && editor.Server.IsReadOnly && editor.Username.IsReadOnly && editor.Password.Password.Length == 0);
                editor.NavName.Text = "Emby 重命名"; return Task.CompletedTask;
            }, save: true);
            Check("emby_rename_retains_identity_and_existing_token", embyConnections.Single().SourceId == connection.SourceId && embyConnections.Single().Name == "Emby 重命名"
                && authenticationCalls == 1 && (await embyStore.LoadCredentialsAsync(connection.SourceId))?.UserId == "fixture-user");

            string localDirectory = Path.Combine(AppDataDirectory.Resolve(), "emby-local-fixture"); Directory.CreateDirectory(localDirectory);
            await File.WriteAllBytesAsync(Path.Combine(localDirectory, "local.wav"), wave);
            var localLibrary = new LocalLibrary(Guid.NewGuid(), localDirectory, "跨库本地验证", "");
            var localCatalogBefore = await localLibraryStore.LoadAsync();
            ApplyLocalCatalog(await localLibraryStore.SaveAsync(localCatalogBefore with { Libraries = [localLibrary] }));
            var localSource = await LocalMusicSource.ScanAsync(localLibrary.Settings, default); ReplaceLocalSource(localSource);
            var localTrack = localSource.Tracks.Single();
            stage = "browse";
            await ShowAllMusicAsync(); BrowseSourceFilter.SelectedIndex = 4; await Until(() => allMusicLoads == 0 && total == 1);
            Check("emby_scope_hides_local_filters", LocalLibraryFilters.Visibility == Visibility.Collapsed && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            RefreshVisibleLocalIndex(); Check("local_background_refresh_respects_emby_scope", total == 1 && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            SearchInput.Text = "验证专辑"; await SearchAsync(); Check("emby_album_search", total == 1);
            SearchInput.Text = "不存在"; await SearchAsync(); Check("emby_empty_search", total == 0);
            SearchInput.Text = ""; await SearchAsync();
            stage = "play";
            queue.Replace([track], track.Reference); await PlayTrackAsync(track);
            await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && NowCover.Source is not null && !snapshotLoading);
            Check("emby_native_play_cover_and_empty_decoder_lyrics", music.Player.Source is not null && music.Player.IsMuted && traffic.CoverReads > 0 && traffic.StreamReads > 0 && currentLyrics.Lines.Count == 0);
            music.Player.Pause(); await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            var playing = music.Player.Source;
            pendingSeek = 15; CommitSeek(); await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 15) < .2);
            Check("emby_native_seek", music.Player.PlaybackSession.CanSeek);
            ResetLibrary(); Check("fn_reset_preserves_emby_playback_and_queue", ReferenceEquals(playing, music.Player.Source) && queue.Current?.Reference == track.Reference && total == 1);
            var factory = embyTestFactory;
            embyTestFactory = (_, _) => throw new MusicApiException(MusicFailure.Rejected);
            await RefreshEmbyAsync(default, force: true);
            Check("emby_non_auth_failure_retains_playback_catalog_and_queue", ReferenceEquals(playing, music.Player.Source) && queue.Current?.Reference == track.Reference
                && ViewModel.PlaybackRouter.CanPlay(track.Reference) && embyTracks[connection.SourceId].Count == 1 && embyStatus.ContainsKey(connection.SourceId));
            embyTestFactory = factory;
            stage = "playlist";
            var catalog = await playlistStore.LoadAsync(); var playlist = new LocalPlaylist(Guid.NewGuid(), "跨源 Emby 验证", [LocalPlaylistEntry.FromTrack(track), LocalPlaylistEntry.FromTrack(localTrack)]);
            await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Append(playlist).ToArray() });
            var entries = (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries;
            var entry = entries.Single(item => item.Track == track.Reference);
            Check("mixed_local_emby_playlist_preserves_identity_order_and_availability", entries.Count == 2 && entries[0].Track == track.Reference && entries[1].Track == localTrack.Reference && entries.All(PlaylistTrackAvailable));
            stage = "resume";
            var saved = CapturePlaybackResume() ?? throw new InvalidOperationException();
            await playbackResumeStore.SaveAsync(saved);
            RemoveEmbySource(connection.SourceId); pendingPlaybackResume = lastPlaybackResume = null; resumeStartupAbandoned = false;
            await RestorePlaybackResumeAsync();
            Check("emby_resume_load_is_silent_with_saved_source_and_position", pendingPlaybackResume?.Track.Reference == track.Reference && Math.Abs(pendingPlaybackResume.PositionSeconds - 15) < .2 && music.Player.Source is null && !mediaReady);
            embyLoaded = false; await RefreshEmbyAsync(default);
            Check("emby_reconnect_restores_same_source_identity", ViewModel.PlaybackRouter.CanPlay(entry.Track) && ResolveResumeTrack(saved.Track)?.Reference == track.Reference);
            double observed = double.NaN;
            void Observe(MediaPlaybackSession sender, object args)
            { if (sender.PlaybackState == MediaPlaybackState.Playing) Interlocked.CompareExchange(ref observed, sender.Position.TotalSeconds, double.NaN); }
            music.Player.PlaybackSession.PlaybackStateChanged += Observe;
            try
            {
                await PlayCurrentOrResumeAsync();
                await Until(() => mediaReady && pendingPlaybackResume is null && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && !double.IsNaN(Volatile.Read(ref observed)));
                firstPlayingPosition = observed;
                Check("emby_resume_first_playing_event_starts_at_saved_position", observed >= saved.PositionSeconds - .3 && observed < saved.PositionSeconds + 2
                    && snapshotTrack?.Reference == track.Reference && music.Player.IsMuted);
            }
            finally { music.Player.PlaybackSession.PlaybackStateChanged -= Observe; }
            music.Player.Pause(); await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            var beforeRevocation = CapturePlaybackResume() ?? throw new InvalidOperationException();
            stage = "unauthorized";
            embyTestFactory = (_, _) => throw new MusicApiException(MusicFailure.Unauthorized);
            await RefreshEmbyAsync(default, force: true);
            Check("emby_unauthorized_revokes_only_source_and_keeps_resume", !ViewModel.PlaybackRouter.CanPlay(track.Reference) && !embyTracks.ContainsKey(connection.SourceId)
                && pendingPlaybackResume?.Track.Reference == track.Reference && Math.Abs(pendingPlaybackResume.PositionSeconds - beforeRevocation.PositionSeconds) < .3 && music.Player.Source is null && PlaylistTrackAvailable(entries[1]));
            queue.Replace([localTrack], localTrack.Reference); await PlayTrackAsync(localTrack);
            await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
            var localPlaying = music.Player.Source;
            embyTestFactory = factory; await RefreshEmbyAsync(default, force: true);
            embyTestFactory = (_, _) => throw new MusicApiException(MusicFailure.Unauthorized); await RefreshEmbyAsync(default, force: true);
            Check("emby_unauthorized_does_not_stop_local_audio_or_queue", ReferenceEquals(localPlaying, music.Player.Source) && queue.Current?.Reference == localTrack.Reference && mediaReady && !ViewModel.PlaybackRouter.CanPlay(track.Reference));
            embyTestFactory = factory; await RefreshEmbyAsync(default, force: true);
            stage = "delete";
            await ShowLocalLibraryManagementAsync(); view = manager!;
            row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.Emby is not null);
            await WithLibraryDeleteAsync(view, row, accept: false, Check);
            Check("emby_delete_cancel_keeps_connection", (await embyStore.LoadAsync()).Count == 1 && PlaylistTrackAvailable(entry));
            await WithLibraryDeleteAsync(view, row, accept: true, Check);
            Check("emby_delete_preserves_offline_playlist", (await embyStore.LoadAsync()).Count == 0 && (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries[0].Track == entry.Track && !PlaylistTrackAvailable(entry));
            Check("emby_delete_keeps_local_playlist_and_audio_available", PlaylistTrackAvailable(entries[1]) && ReferenceEquals(localPlaying, music.Player.Source) && queue.Current?.Reference == localTrack.Reference);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name; }
        finally { embyTestFactory = null; embyAuthTestFactory = null; localLibraryPageOpened = null; }
        await File.WriteAllTextAsync(Path.Combine(AppDataDirectory.Resolve(), "emby-window-verification.json"), JsonSerializer.Serialize(new { stage, error, checks, firstPlayingPosition }));
    }
}
#endif
