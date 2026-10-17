#if DEBUG
using System.Net;
using System.Text.Json;
using Melvune.App.Services;
using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Windows.Media.Playback;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private async Task RunSubsonicWindowVerificationAsync()
    {
        var checks = new Dictionary<string, bool>();
        string stage = "fixture", error = "";
        double? firstPlayingPosition = null;
        int requests = 0, clientsCreated = 0;
        HttpStatusCode? lyricsStatus = null;
        void Check(string name, bool value)
        { checks[name] = value; if (!value) throw new InvalidOperationException(name); }
        async Task Until(Func<bool> predicate)
        {
            for (int i = 0; i < 200 && !predicate(); i++) await Task.Delay(50);
            if (!predicate()) throw new TimeoutException(stage);
        }
        static LibraryManagerRow Row(NavidromeConnection connection) => new(null, null, true) { Navidrome = connection };
        static LibraryEditRequest Request(NavidromeConnection connection, string name, string? server = null, string? username = null) =>
            new(connection.Provider == "subsonic" ? 4 : 2, name, "", "", "", false)
            { Server = server ?? connection.ServerAddress, Username = username ?? connection.Username, Password = "" };

        // Corrupt only this isolated profile after the atomic commit, then restore its committed bytes.
        // A committed operation must succeed without a second index read or a cancellable continuation.
        async Task AfterCommitAsync(Func<CancellationToken, Task> operation, Action verify)
        {
            string index = Path.Combine(ThemeSettingsDirectory(), "navidrome-connections.json");
            byte[]? committed = null;
            using var cancelled = new CancellationTokenSource();
            navidromeAfterCommit = () =>
            {
                committed = File.ReadAllBytes(index);
                File.WriteAllText(index, "{");
                cancelled.Cancel();
            };
            try
            {
                await operation(cancelled.Token);
                Check("commit_hook_exercised_" + checks.Count, committed is not null && cancelled.IsCancellationRequested);
                verify();
            }
            finally
            {
                navidromeAfterCommit = null;
                if (committed is not null) File.WriteAllBytes(index, committed);
            }
        }

        try
        {
            music.Player.IsMuted = true;
            using var audio = PlaybackVerification.CreateWave();
            using var bytes = new MemoryStream(); await audio.CopyToAsync(bytes);
            byte[] wave = bytes.ToArray(), cover = await ArtworkFixture.EncodeAsync(160, 160);
            navidromeTestFactory = (connection, credentials) =>
            {
                clientsCreated++;
                string title = connection.Provider == "subsonic" ? "通用受控曲目" : "Nav 受控曲目";
                return new(connection.SourceId, connection.Name, connection.ServerAddress, credentials,
                    () => new NavidromeWindowHandler(wave, cover, title, () => Interlocked.Increment(ref requests), lyricsStatus), connection.Provider);
            };
            await ShowSyntheticPreviewAsync(); navidromeLoaded = true;
            Check("subsonic_profile_starts_without_connections", (await navidromeStore.LoadAsync()).Count == 0);
            PlaylistPageView? manager = null; localLibraryPageOpened = view => manager = view;
            await ShowLocalLibraryManagementAsync(); var view = manager!;
            var add = FindSongVisual<Button>(FullPageHost, button => Equals(button.Tag, "新建音乐库")) ?? throw new InvalidOperationException();
            void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            stage = "create";
            await WithLibraryEditorAsync(view, () => Invoke(add), async editor =>
            {
                editor.Kind.SelectedIndex = 4;
                Check("subsonic_editor_has_distinct_kind_and_dynamic_fields", editor.Kind.Items.Count == 6
                    && editor.OtherFields.Visibility == Visibility.Visible && editor.LocalFields.Visibility == Visibility.Collapsed
                    && editor.ServiceType.SelectedIndex == 2 && !editor.Dialog.IsPrimaryButtonEnabled);
                editor.NavName.Text = "通用验证库"; editor.Server.Text = "https://subsonic.example.invalid/music/";
                editor.Username.Text = "fixture"; editor.Password.Password = string.Concat("fixture", "-only");
                await Task.Delay(100); Check("subsonic_editor_create_enabled", editor.Dialog.IsPrimaryButtonEnabled);
            }, save: true);
            await Until(() => navidromeConnections.Count == 1 && view.Editor.IsEnabled);
            var generic = navidromeConnections.Single(); var track = navidromeTracks[generic.SourceId].Single();
            Check("subsonic_created_and_registered_with_provider", generic.Provider == "subsonic"
                && navidromeSources[generic.SourceId].Source.Provider == "subsonic" && ViewModel.PlaybackRouter.CanPlay(track.Reference)
                && (await navidromeStore.LoadAsync()).Single().Provider == "subsonic");
            var row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.Navidrome?.SourceId == generic.SourceId);
            Check("subsonic_library_card_has_separate_group", row.Group.Contains("Subsonic", StringComparison.OrdinalIgnoreCase));
            await AfterCommitAsync(_ => WithLibraryEditorAsync(view, () => InvokeLibraryCard(view, row, "编辑音乐库"), editor =>
                {
                    Check("subsonic_edit_preserves_provider_account_and_password", editor.Kind.SelectedIndex == 4
                        && !editor.Kind.IsEnabled && editor.Server.IsReadOnly && editor.Username.IsReadOnly && editor.Password.Password.Length == 0);
                    editor.NavName.Text = "通用重命名"; return Task.CompletedTask;
                }, save: true), () => Check("subsonic_committed_editor_update_keeps_success_and_card_without_index_reread",
                    view.Status.Text.Contains("已连接", StringComparison.Ordinal) && view.Editor.IsEnabled
                        && view.List.Items.Cast<LibraryManagerRow>().Any(item => item.Navidrome?.SourceId == generic.SourceId && item.Name == "通用重命名")));
            generic = navidromeConnections.Single();
            Check("subsonic_rename_keeps_source_identity", generic.SourceId == track.SourceInstanceId && generic.Name == "通用重命名");

            var navRequest = new LibraryEditRequest(2, "Nav 对照库", "", "", "", false)
            { Server = "https://nav.example.invalid/", Username = "fixture", Password = string.Concat("fixture", "-only") };
            await SaveNavidromeAsync(null, navRequest, default);
            var nav = navidromeConnections.Single(item => item.Provider == "navidrome");
            var navTrack = navidromeTracks[nav.SourceId].Single();
            Check("same_song_id_in_generic_and_nav_has_distinct_identity", navTrack.Id == track.Id && navTrack.Reference != track.Reference);

            stage = "commit-boundary";
            foreach (var connection in new[] { generic, nav })
            {
                string prefix = connection.Provider;
                await AfterCommitAsync(ct => SaveNavidromeAsync(Row(connection), Request(connection, connection.Name + " 已提交"), ct), () =>
                    Check(prefix + "_save_commit_ignores_cancel_and_failed_reread", navidromeConnections.Single(item => item.SourceId == connection.SourceId).Name.EndsWith("已提交", StringComparison.Ordinal)
                        && navidromeSources.ContainsKey(connection.SourceId) && navidromeTracks.ContainsKey(connection.SourceId)));
                Check(prefix + "_committed_save_matches_disk", (await navidromeStore.LoadAsync()).Single(item => item.SourceId == connection.SourceId)
                    == navidromeConnections.Single(item => item.SourceId == connection.SourceId));
            }
            generic = navidromeConnections.Single(item => item.Provider == "subsonic");
            nav = navidromeConnections.Single(item => item.Provider == "navidrome");
            stage = "account-guard";
            foreach (var connection in new[] { generic, nav })
            {
                foreach (bool serverChange in new[] { true, false })
                {
                    int clientsBefore = clientsCreated, requestsBefore = Volatile.Read(ref requests);
                    bool rejected = false;
                    try
                    {
                        await SaveNavidromeAsync(Row(connection), Request(connection, connection.Name,
                            serverChange ? "https://different.example.invalid/" : null, serverChange ? null : "different"), default);
                    }
                    catch (InvalidOperationException) { rejected = true; }
                    Check(connection.Provider + (serverChange ? "_server_guard_before_client_or_http" : "_account_guard_before_client_or_http"),
                        rejected && clientsBefore == clientsCreated && requestsBefore == Volatile.Read(ref requests));
                }
            }

            string localDirectory = Path.Combine(AppDataDirectory.Resolve(), "subsonic-local-fixture"); Directory.CreateDirectory(localDirectory);
            await File.WriteAllBytesAsync(Path.Combine(localDirectory, "local.wav"), wave);
            var localLibrary = new LocalLibrary(Guid.NewGuid(), localDirectory, "跨库本地验证", "");
            var localCatalog = await localLibraryStore.LoadAsync();
            ApplyLocalCatalog(await localLibraryStore.SaveAsync(localCatalog with { Libraries = [localLibrary] }));
            var localSource = await LocalMusicSource.ScanAsync(localLibrary.Settings, default); ReplaceLocalSource(localSource);
            var localTrack = localSource.Tracks.Single();
            stage = "browse";
            await ShowAllMusicAsync(); BrowseSourceFilter.SelectedIndex = 5; await Until(() => allMusicLoads == 0 && total == 1);
            Check("subsonic_scope_excludes_nav_and_local", LocalLibraryFilters.Visibility == Visibility.Collapsed
                && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            RefreshVisibleLocalIndex(); Check("local_background_refresh_preserves_subsonic_scope", total == 1
                && (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == track.Reference);
            BrowseSourceFilter.SelectedIndex = 3; await Until(() => allMusicLoads == 0 && total == 1);
            Check("nav_scope_excludes_generic_same_id", (TrackList.ItemsSource as IEnumerable<MusicTrack>)!.Single().Reference == navTrack.Reference);
            BrowseSourceFilter.SelectedIndex = 5; await Until(() => allMusicLoads == 0 && total == 1);
            SearchInput.Text = "验证专辑"; await SearchAsync(); Check("subsonic_album_search", total == 1);
            SearchInput.Text = "不存在"; await SearchAsync(); Check("subsonic_empty_search", total == 0);
            SearchInput.Text = ""; await SearchAsync();

            stage = "play";
            track = navidromeTracks[generic.SourceId].Single();
            queue.Replace([track], track.Reference); await PlayTrackAsync(track);
            await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && currentLyrics.IsTimed && NowCover.Source is not null && !snapshotLoading);
            Check("subsonic_native_play_cover_and_lyrics", music.Player.Source is not null && music.Player.IsMuted && currentLyrics.Lines[0].Text == "第一行");
            music.Player.Pause(); await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            var playing = music.Player.Source; pendingSeek = 15; CommitSeek();
            await Until(() => Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - 15) < .2);
            Check("subsonic_native_seek", music.Player.PlaybackSession.CanSeek);
            stage = "decoder-lyrics-fallback";
            var originalDecoder = decoderLyrics;
            var originalDecoderReference = decoderLyricsReference;
            try
            {
                decoderLyrics = new(TaskCreationOptions.RunContinuationsAsynchronously);
                decoderLyricsReference = track.Reference;
                decoderLyrics.SetResult(Lyrics.Parse("[00:00.00]受控内嵌歌词\n[00:10.00]内嵌第二行"));
                lyricsStatus = HttpStatusCode.ServiceUnavailable;
                await RefreshNavidromeAsync(default, force: true); await LoadLyricsAsync(track);
                Check("subsonic_lyrics_service_unavailable_falls_back_to_decoder", currentLyrics.IsTimed
                    && currentLyrics.Lines[0].Text == "受控内嵌歌词" && !lyricsNeedsRetry && snapshotLyricsKind == PlaybackLyricsKind.Timed);
                lyricsStatus = HttpStatusCode.Unauthorized;
                await RefreshNavidromeAsync(default, force: true); await LoadLyricsAsync(track);
                Check("subsonic_lyrics_unauthorized_is_not_hidden_by_decoder", currentLyrics.Lines.Count == 0
                    && lyricsNeedsRetry && snapshotLyricsKind == PlaybackLyricsKind.Unavailable && LyricsStatus.Text.Contains("加载失败", StringComparison.Ordinal));
                decoderLyrics = new(TaskCreationOptions.RunContinuationsAsynchronously); decoderLyrics.SetResult(Lyrics.Empty);
                lyricsStatus = HttpStatusCode.ServiceUnavailable;
                await RefreshNavidromeAsync(default, force: true); await LoadLyricsAsync(track);
                Check("subsonic_lyrics_failed_service_without_decoder_remains_retryable", currentLyrics.Lines.Count == 0
                    && lyricsNeedsRetry && snapshotLyricsKind == PlaybackLyricsKind.Unavailable);
            }
            finally
            {
                lyricsStatus = null; decoderLyrics = originalDecoder; decoderLyricsReference = originalDecoderReference;
                await RefreshNavidromeAsync(default, force: true); await LoadLyricsAsync(track);
            }
            ResetLibrary(); Check("fn_reset_preserves_generic_playback_and_queue", ReferenceEquals(playing, music.Player.Source) && queue.Current?.Reference == track.Reference);
            var factory = navidromeTestFactory;
            navidromeTestFactory = (connection, credentials) => connection.Provider == "subsonic"
                ? throw new MusicApiException(MusicFailure.Rejected) : factory!(connection, credentials);
            await RefreshNavidromeAsync(default, force: true);
            Check("subsonic_non_auth_failure_keeps_catalog_audio_and_queue", ReferenceEquals(playing, music.Player.Source)
                && queue.Current?.Reference == track.Reference && ViewModel.PlaybackRouter.CanPlay(track.Reference) && navidromeTracks[generic.SourceId].Count == 1);
            navidromeTestFactory = factory;

            stage = "playlist";
            var catalog = await playlistStore.LoadAsync();
            var playlist = new LocalPlaylist(Guid.NewGuid(), "本地通用Nav混合验证",
                [LocalPlaylistEntry.FromTrack(track), LocalPlaylistEntry.FromTrack(navTrack), LocalPlaylistEntry.FromTrack(localTrack)]);
            await playlistStore.SaveAsync(catalog with { Playlists = catalog.Playlists.Append(playlist).ToArray() });
            var entries = (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries;
            Check("mixed_local_generic_nav_playlist_keeps_order_and_identity", entries.Count == 3
                && entries[0].Track == track.Reference && entries[1].Track == navTrack.Reference && entries[2].Track == localTrack.Reference && entries.All(PlaylistTrackAvailable));

            stage = "resume";
            var saved = CapturePlaybackResume() ?? throw new InvalidOperationException();
            await playbackResumeStore.SaveAsync(saved);
            RemoveNavidromeSource(generic.SourceId); pendingPlaybackResume = lastPlaybackResume = null; resumeStartupAbandoned = false;
            await RestorePlaybackResumeAsync();
            Check("subsonic_resume_record_is_silent_and_keeps_position", pendingPlaybackResume?.Track.Reference == track.Reference
                && Math.Abs(pendingPlaybackResume.PositionSeconds - 15) < .2 && music.Player.Source is null && !mediaReady);
            navidromeLoaded = false; await RefreshNavidromeAsync(default);
            Check("subsonic_reconnect_keeps_resume_and_playlist_identity", ResolveResumeTrack(saved.Track)?.Reference == track.Reference && entries.All(PlaylistTrackAvailable));
            double observed = double.NaN;
            void Observe(MediaPlaybackSession sender, object args)
            { if (sender.PlaybackState == MediaPlaybackState.Playing) Interlocked.CompareExchange(ref observed, sender.Position.TotalSeconds, double.NaN); }
            music.Player.PlaybackSession.PlaybackStateChanged += Observe;
            try
            {
                PlayPause_Click(this, new RoutedEventArgs());
                await Until(() => mediaReady && pendingPlaybackResume is null && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing && !double.IsNaN(Volatile.Read(ref observed)));
                firstPlayingPosition = observed;
                Check("subsonic_play_button_resumes_first_playing_event_at_saved_position", observed >= saved.PositionSeconds - .3
                    && observed < saved.PositionSeconds + 2 && snapshotTrack?.Reference == track.Reference && music.Player.IsMuted);
            }
            finally { music.Player.PlaybackSession.PlaybackStateChanged -= Observe; }
            music.Player.Pause(); await Until(() => music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused);
            var beforeRevocation = CapturePlaybackResume() ?? throw new InvalidOperationException();
            stage = "revoke";
            navidromeTestFactory = (connection, credentials) => connection.Provider == "subsonic"
                ? throw new MusicApiException(MusicFailure.Unauthorized) : factory!(connection, credentials);
            await RefreshNavidromeAsync(default, force: true);
            Check("subsonic_auth_revocation_isolated_and_resume_retained", !ViewModel.PlaybackRouter.CanPlay(track.Reference)
                && ViewModel.PlaybackRouter.CanPlay(navTrack.Reference) && PlaylistTrackAvailable(entries[2])
                && pendingPlaybackResume?.Track.Reference == track.Reference
                && Math.Abs(pendingPlaybackResume.PositionSeconds - beforeRevocation.PositionSeconds) < .3 && music.Player.Source is null);
            navidromeTestFactory = factory; await RefreshNavidromeAsync(default, force: true);
            queue.Replace([localTrack], localTrack.Reference); await PlayTrackAsync(localTrack);
            await Until(() => mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
            var localPlaying = music.Player.Source;
            stage = "delete";
            await ShowLocalLibraryManagementAsync(); view = manager!;
            row = view.List.Items.Cast<LibraryManagerRow>().Single(item => item.Navidrome?.SourceId == generic.SourceId);
            await WithLibraryDeleteAsync(view, row, accept: false, Check);
            Check("subsonic_delete_cancel_retains_connection_and_playlist", (await navidromeStore.LoadAsync()).Count == 2 && PlaylistTrackAvailable(entries[0]));
            await AfterCommitAsync(_ => WithLibraryDeleteAsync(view, row, accept: true, Check), () =>
                Check("subsonic_committed_delete_keeps_success_and_removes_card_without_index_reread",
                    view.Status.Text.Contains("已移除", StringComparison.Ordinal) && view.Editor.IsEnabled
                        && !view.List.Items.Cast<LibraryManagerRow>().Any(item => item.Navidrome?.SourceId == generic.SourceId)
                        && !navidromeConnections.Any(item => item.SourceId == generic.SourceId)));
            Check("subsonic_delete_preserves_other_sources_audio_and_offline_entry", !navidromeConnections.Any(item => item.SourceId == generic.SourceId)
                && !PlaylistTrackAvailable(entries[0]) && PlaylistTrackAvailable(entries[1]) && PlaylistTrackAvailable(entries[2])
                && ReferenceEquals(localPlaying, music.Player.Source) && queue.Current?.Reference == localTrack.Reference
                && (await playlistStore.LoadAsync()).Playlists.Single(item => item.Id == playlist.Id).Entries[0].Track == track.Reference);
            await AfterCommitAsync(ct => DeleteNavidromeAsync(nav.SourceId, ct), () =>
                Check("nav_delete_commit_ignores_cancel_and_failed_reread", !navidromeConnections.Any(item => item.SourceId == nav.SourceId)
                    && !ViewModel.PlaybackRouter.CanPlay(navTrack.Reference) && ReferenceEquals(localPlaying, music.Player.Source)));
            Check("committed_delete_matches_disk_and_preserves_local_audio", (await navidromeStore.LoadAsync()).Count == 0
                && PlaylistTrackAvailable(entries[2]) && ReferenceEquals(localPlaying, music.Player.Source));
            stage = "complete";
        }
        catch (Exception ex) { error = ex.GetType().Name + ":" + ex.Message; }
        finally { navidromeAfterCommit = null; navidromeTestFactory = null; localLibraryPageOpened = null; }
        await File.WriteAllTextAsync(Path.Combine(AppDataDirectory.Resolve(), "subsonic-window-verification.json"),
            JsonSerializer.Serialize(new { stage, error, checks, firstPlayingPosition }));
    }
}
#endif
