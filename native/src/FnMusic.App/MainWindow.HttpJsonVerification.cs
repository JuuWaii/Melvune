#if DEBUG
using System.Net;
using System.Text;
using System.Text.Json;
using FnMusic.App.Services;
using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Media.Playback;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private sealed class HttpJsonWindowTraffic
    {
        public int Requests, CoverReads, StreamReads, LyricReads;
    }
    private sealed class HttpJsonWindowHandler(byte[] wave, byte[] cover, HttpJsonWindowTraffic traffic) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            if (uri.Host != "json.example.invalid" || !uri.AbsolutePath.StartsWith("/music/", StringComparison.Ordinal)
                || request.Method != HttpMethod.Get || request.Headers.Authorization is not null
                || request.Headers.Contains("Cookie")) throw new InvalidOperationException("Unexpected anonymous fixture request");
            Interlocked.Increment(ref traffic.Requests);
            string path = uri.AbsolutePath["/music/".Length..];
            if (path == "audio/same.wav")
            {
                Interlocked.Increment(ref traffic.StreamReads);
                var range = request.Headers.Range!.Ranges.Single();
                long from = range.From!.Value, to = Math.Min(range.To!.Value, wave.Length - 1);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(wave[(int)from..((int)to + 1)]) };
                response.Content.Headers.ContentType = new("audio/wav");
                response.Content.Headers.ContentRange = new(from, to, wave.Length); return Task.FromResult(response);
            }
            if (path == "cover/same.png")
            {
                Interlocked.Increment(ref traffic.CoverReads);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cover) };
                response.Content.Headers.ContentType = new("image/png"); return Task.FromResult(response);
            }
            if (path == "lyrics/same.lrc")
            {
                Interlocked.Increment(ref traffic.LyricReads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("[00:00.00]JSON 第一行\n[00:10.00]JSON 第二行", Encoding.UTF8, "text/plain") });
            }
            if (path != "api/tracks") throw new InvalidOperationException("Unexpected fixture route");
            string json = "{\"data\":{\"total\":1,\"items\":[{\"id\":\"same\",\"title\":\"JSON 受控曲目\",\"artist\":\"验证歌手\",\"album\":\"验证专辑\",\"duration\":60,\"url\":\"audio/same.wav\",\"cover\":\"cover/same.png\",\"lyrics\":\"lyrics/same.lrc\"}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private async Task VerifyHttpJsonWindowAsync()
    {
        var checks = new Dictionary<string, bool>(); string stage = "fixture", error = "";
        double? firstPlayingPosition = null;
        void Check(string name, bool value) { checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        async Task Until(Func<bool> predicate)
        { for (int i = 0; i < 240 && !predicate(); i++) await Task.Delay(50); if (!predicate()) throw new TimeoutException(stage); }
        async Task AfterCommitAsync(Func<CancellationToken, Task> operation, Action verify)
        {
            string index = Path.Combine(ThemeSettingsDirectory(), "http-json-connections.json"); byte[]? committed = null;
            using var cancelled = new CancellationTokenSource();
            httpJsonAfterCommit = () =>
            { committed = File.ReadAllBytes(index); File.WriteAllText(index, "{"); cancelled.Cancel(); };
            try
            {
                await operation(cancelled.Token);
                Check("httpjson_commit_hook_exercised_" + checks.Count, committed is not null && cancelled.IsCancellationRequested);
                verify();
            }
            finally
            { httpJsonAfterCommit = null; if (committed is not null) File.WriteAllBytes(index, committed); }
        }
        try
        {
            music.Player.IsMuted = true;
            using var audio = PlaybackVerification.CreateWave(); using var bytes = new MemoryStream(); await audio.CopyToAsync(bytes);
            byte[] wave = bytes.ToArray(), cover = await ArtworkFixture.EncodeAsync(160, 160);
            var traffic = new HttpJsonWindowTraffic(); int clientsCreated = 0;
            httpJsonTestFactory = connection =>
            { clientsCreated++; return new(connection.SourceId, connection.Name, connection.ServerAddress, connection.Definition,
                () => new HttpJsonWindowHandler(wave, cover, traffic)); };
            await ShowSyntheticPreviewAsync(); httpJsonLoaded = true;
            Check("httpjson_profile_starts_without_connections", (await httpJsonStore.LoadAsync()).Count == 0);
            PlaylistPageView? manager = null; localLibraryPageOpened = view => manager = view;
            await ShowLocalLibraryManagementAsync(); var view = manager!;
            var add = FindSongVisual<Button>(FullPageHost, button => Equals(button.Tag, "新建音乐库")) ?? throw new InvalidOperationException();
            void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            stage = "create";
            await WithLibraryEditorAsync(view, () => Invoke(add), async editor =>
            {
                editor.Kind.SelectedIndex = 5;
                Check("httpjson_editor_dynamic_anonymous_fields", editor.Kind.Items.Count == 6
                    && editor.JsonDefinition.Visibility == Visibility.Visible && editor.Username.Visibility == Visibility.Collapsed
                    && editor.Password.Visibility == Visibility.Collapsed && editor.ServiceType.SelectedIndex == 3
                    && !editor.Dialog.IsPrimaryButtonEnabled);
                editor.NavName.Text = "JSON 验证库"; editor.Server.Text = "https://json.example.invalid/music/";
                string valid = editor.JsonDefinition.Text; editor.JsonDefinition.Text = "{";
                await Until(() => !editor.Dialog.IsPrimaryButtonEnabled && editor.Status.Text.Contains("无效"));
                Check("httpjson_editor_rejects_invalid_mapping_locally", !editor.Dialog.IsPrimaryButtonEnabled && editor.Status.Text.Contains("无效"));
                editor.JsonDefinition.Text = valid; await Task.Delay(100);
                Check("httpjson_editor_accepts_valid_anonymous_mapping", editor.Dialog.IsPrimaryButtonEnabled);
            }, save: true);
            await Until(() => httpJsonConnections.Count == 1 && view.Editor.IsEnabled);
            var connection = httpJsonConnections.Single(); var track = httpJsonTracks[connection.SourceId].Single();
            Check("httpjson_saved_registered_and_persistent", ViewModel.PlaybackRouter.CanPlay(track.Reference)
                && httpJsonSources[connection.SourceId].Source.Provider == "http-json"
                && (await httpJsonStore.LoadAsync()).Single().SourceId == connection.SourceId);
            var row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.HttpJson is not null);
            Check("httpjson_card_has_dedicated_identity_and_group", row.Key == "json:" + connection.SourceId && row.Group.StartsWith("自定义 HTTP/JSON", StringComparison.Ordinal));
            stage = "edit";
            await AfterCommitAsync(_ => WithLibraryEditorAsync(view, () => InvokeLibraryCard(view, row, "编辑音乐库"), editor =>
            {
                Check("httpjson_edit_locks_source_and_mapping", editor.Kind.SelectedIndex == 5 && !editor.Kind.IsEnabled
                    && editor.Server.IsReadOnly && editor.JsonDefinition.IsReadOnly && editor.Server.Text == connection.ServerAddress
                    && HttpJsonDefinition.Parse(editor.JsonDefinition.Text) == connection.Definition);
                editor.NavName.Text = "JSON 重命名"; return Task.CompletedTask;
            }, save: true), () => Check("httpjson_committed_edit_succeeds_without_reread", view.Status.Text.Contains("已连接")
                && view.List.Items.Cast<LibraryManagerRow>().Any(item => item.HttpJson?.SourceId == connection.SourceId && item.Name == "JSON 重命名")));
            connection = httpJsonConnections.Single();
            Check("httpjson_rename_keeps_source_identity", connection.SourceId == track.SourceInstanceId && (await httpJsonStore.LoadAsync()).Single().Name == "JSON 重命名");
            stage = "identity-guard";
            int clientsBefore = clientsCreated, requestsBefore = traffic.Requests; bool rejected = false;
            try
            {
                await SaveHttpJsonAsync(new(null, null, true) { HttpJson = connection },
                    new(5, connection.Name, "", "", "", false) { Server = "https://different.example.invalid/", JsonDefinition = connection.Definition.ToJson() }, default);
            }
            catch (InvalidOperationException) { rejected = true; }
            Check("httpjson_server_change_rejected_before_requests", rejected && clientsBefore == clientsCreated && requestsBefore == traffic.Requests);
            rejected = false;
            try
            {
                await SaveHttpJsonAsync(new(null, null, true) { HttpJson = connection },
                    new(5, connection.Name, "", "", "", false) { Server = connection.ServerAddress,
                        JsonDefinition = (connection.Definition with { IdPath = "otherId" }).ToJson() }, default);
            }
            catch (InvalidOperationException) { rejected = true; }
            Check("httpjson_mapping_change_rejected_before_requests", rejected && clientsBefore == clientsCreated && requestsBefore == traffic.Requests);

            string localDirectory = Path.Combine(AppDataDirectory.Resolve(), "httpjson-local-fixture"); Directory.CreateDirectory(localDirectory);
            await File.WriteAllBytesAsync(Path.Combine(localDirectory, "local.wav"), wave);
            var localLibrary = new LocalLibrary(Guid.NewGuid(), localDirectory, "跨库本地验证", "");
            var localCatalog = await localLibraryStore.LoadAsync();
            ApplyLocalCatalog(await localLibraryStore.SaveAsync(localCatalog with { Libraries = [localLibrary] }));
            var localSource = await LocalMusicSource.ScanAsync(localLibrary.Settings, default); ReplaceLocalSource(localSource);
            var localTrack = localSource.Tracks.Single();
            stage = "browse";
            await ShowAllMusicAsync(); BrowseSourceFilter.SelectedIndex = 6; await Until(() => allMusicLoads == 0 && total == 1);
            Check("httpjson_scope_hides_local_and_other_sources", LocalLibraryFilters.Visibility == Visibility.Collapsed
                && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            RefreshVisibleLocalIndex(); Check("httpjson_background_local_refresh_preserves_scope", total == 1
                && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            SearchInput.Text = "验证专辑"; await SearchAsync(); Check("httpjson_album_search", total == 1);
            SearchInput.Text = "不存在"; await SearchAsync(); Check("httpjson_empty_search", total == 0);
            SearchInput.Text = ""; await SearchAsync();
            stage = "play";
            track = httpJsonTracks[connection.SourceId].Single(); queue.Replace([track], track.Reference); await PlayTrackAsync(track);
            await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                && currentLyrics.IsTimed && NowCover.Source is not null && !snapshotLoading);
            Check("httpjson_native_play_cover_and_timed_lyrics", music.Player.Source is not null && music.Player.IsMuted
                && traffic.CoverReads > 0 && traffic.StreamReads > 0 && traffic.LyricReads > 0 && currentLyrics.Lines[0].Text == "JSON 第一行");
            music.Player.Pause(); await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            var playing = music.Player.Source; pendingSeek = 15; CommitSeek();
            await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 15) < .2);
            Check("httpjson_native_seek", music.Player.PlaybackSession.CanSeek);
            ResetLibrary(); Check("fn_reset_preserves_httpjson_audio_and_queue", ReferenceEquals(playing, music.Player.Source) && queue.Current?.Reference == track.Reference);
            var factory = httpJsonTestFactory;
            httpJsonTestFactory = _ => throw new MusicApiException(MusicFailure.Unavailable);
            await RefreshHttpJsonAsync(default, force: true);
            Check("httpjson_transient_failure_retains_catalog_audio_queue", ReferenceEquals(playing, music.Player.Source)
                && queue.Current?.Reference == track.Reference && ViewModel.PlaybackRouter.CanPlay(track.Reference) && httpJsonTracks[connection.SourceId].Count == 1);
            httpJsonTestFactory = factory;
            stage = "playlist";
            var catalog = await playlistStore.LoadAsync(); var playlist = new LocalPlaylist(Guid.NewGuid(), "本地与 JSON 混合验证",
                [LocalPlaylistEntry.FromTrack(track), LocalPlaylistEntry.FromTrack(localTrack)]);
            await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Append(playlist).ToArray() });
            var entries = (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries;
            Check("httpjson_mixed_playlist_keeps_identity_order_and_availability", entries.Count == 2
                && entries[0].Track == track.Reference && entries[1].Track == localTrack.Reference && entries.All(PlaylistTrackAvailable));
            stage = "resume";
            var saved = CapturePlaybackResume() ?? throw new InvalidOperationException(); await playbackResumeStore.SaveAsync(saved);
            RemoveHttpJsonSource(connection.SourceId); pendingPlaybackResume = lastPlaybackResume = null; resumeStartupAbandoned = false;
            await RestorePlaybackResumeAsync();
            Check("httpjson_resume_restores_silently_with_position", pendingPlaybackResume?.Track.Reference == track.Reference
                && Math.Abs(pendingPlaybackResume.PositionSeconds - 15) < .2 && music.Player.Source is null && !mediaReady);
            httpJsonLoaded = false; await RefreshHttpJsonAsync(default);
            Check("httpjson_reconnect_preserves_resume_playlist_identity", ResolveResumeTrack(saved.Track)?.Reference == track.Reference && entries.All(PlaylistTrackAvailable));
            double observed = double.NaN;
            void Observe(MediaPlaybackSession sender, object args)
            { if (sender.PlaybackState == MediaPlaybackState.Playing) Interlocked.CompareExchange(ref observed, sender.Position.TotalSeconds, double.NaN); }
            music.Player.PlaybackSession.PlaybackStateChanged += Observe;
            try
            {
                PlayPause_Click(this, new RoutedEventArgs());
                await Until(() => mediaReady && pendingPlaybackResume is null && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && !double.IsNaN(Volatile.Read(ref observed)));
                firstPlayingPosition = observed;
                Check("httpjson_first_playing_event_resumes_at_saved_position", observed >= saved.PositionSeconds - .3
                    && observed < saved.PositionSeconds + 2 && snapshotTrack?.Reference == track.Reference && music.Player.IsMuted);
            }
            finally { music.Player.PlaybackSession.PlaybackStateChanged -= Observe; }
            music.Player.Pause(); await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            var beforeRevocation = CapturePlaybackResume() ?? throw new InvalidOperationException();
            stage = "revocation";
            httpJsonTestFactory = _ => throw new MusicApiException(MusicFailure.Unauthorized);
            await RefreshHttpJsonAsync(default, force: true);
            Check("httpjson_unauthorized_revokes_catalog_and_preserves_resume", !ViewModel.PlaybackRouter.CanPlay(track.Reference)
                && !httpJsonTracks.ContainsKey(connection.SourceId) && music.Player.Source is null
                && pendingPlaybackResume?.Track.Reference == track.Reference
                && Math.Abs(pendingPlaybackResume.PositionSeconds - beforeRevocation.PositionSeconds) < .3 && PlaylistTrackAvailable(entries[1]));
            httpJsonTestFactory = factory; await RefreshHttpJsonAsync(default, force: true);
            queue.Replace([localTrack], localTrack.Reference); await PlayTrackAsync(localTrack);
            await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
            var localPlaying = music.Player.Source;
            httpJsonTestFactory = _ => throw new MusicApiException(MusicFailure.Unauthorized); await RefreshHttpJsonAsync(default, force: true);
            Check("httpjson_revocation_does_not_stop_local_audio", ReferenceEquals(localPlaying, music.Player.Source)
                && queue.Current?.Reference == localTrack.Reference && mediaReady);
            httpJsonTestFactory = factory; await RefreshHttpJsonAsync(default, force: true);
            stage = "delete";
            await ShowLocalLibraryManagementAsync(); view = manager!;
            row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.HttpJson is not null);
            await WithLibraryDeleteAsync(view, row, accept: false, Check);
            Check("httpjson_delete_cancel_retains_connection", (await httpJsonStore.LoadAsync()).Count == 1 && PlaylistTrackAvailable(entries[0]));
            await AfterCommitAsync(_ => WithLibraryDeleteAsync(view, row, accept: true, Check), () =>
                Check("httpjson_committed_delete_succeeds_without_reread", view.Status.Text.Contains("已移除")
                    && view.List.Items.Cast<LibraryManagerRow>().All(item => item.HttpJson is null) && httpJsonConnections.Count == 0));
            Check("httpjson_delete_preserves_local_audio_and_offline_playlist_entry", (await httpJsonStore.LoadAsync()).Count == 0
                && !PlaylistTrackAvailable(entries[0]) && PlaylistTrackAvailable(entries[1]) && ReferenceEquals(localPlaying, music.Player.Source)
                && queue.Current?.Reference == localTrack.Reference
                && (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries[0].Track == track.Reference);
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name + ":" + ex.Message; }
        finally { httpJsonTestFactory = null; httpJsonAfterCommit = null; localLibraryPageOpened = null; }
        await File.WriteAllTextAsync(Path.Combine(AppDataDirectory.Resolve(), "httpjson-window-verification.json"),
            JsonSerializer.Serialize(new { stage, error, checks, firstPlayingPosition }));
    }
}
#endif
