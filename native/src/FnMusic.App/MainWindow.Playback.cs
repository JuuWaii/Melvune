using FnMusic.App.Services;
using FnMusic.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Playback;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly NativeMusicPlayer music = new();
    private readonly PlaybackQueue queue = new();
    private CancellationTokenSource work = new();
    private CancellationTokenSource libraryWork = new();
    private string searchQuery = "";
    private string queueOrigin = "曲库当前页";
    private DispatcherQueueTimer? progressTimer;
    private DispatcherQueueTimer? seekTimer;
    private bool draggingSeek;
    private double? pendingSeek, submittedSeek;
    private DateTimeOffset seekDeadline;
    private int page = 1, total;
    private bool updatingProgress, closed, mediaReady;

    private void InitializePlayback()
    {
        // Flyout content owns a separate namescope; bind directly to the status objects.
        PlaybackInfoStatus.SetBinding(TextBlock.TextProperty, new global::Microsoft.UI.Xaml.Data.Binding { Source = PlaybackStatus, Path = new PropertyPath("Text") });
        PlaybackInfoQueue.SetBinding(TextBlock.TextProperty, new global::Microsoft.UI.Xaml.Data.Binding { Source = QueueStatus, Path = new PropertyPath("Text") });
        PlaybackStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            if (closed) return;
            string text = PlaybackStatus.Text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlaybackInfoButton, "播放状态：" + text);
            if (text.Contains("失败") || text.Contains("无法") || text.Contains("失效") || text.Contains("超时")) ShowMetadataNotice(text);
        });
        UpdateQueueModeControl();
        UpdateVolumeControl();
        InitializeAudioOutputs();
        // 单行 TextBox 会处理 Enter；仍接收该路由事件以提交搜索。
        SearchInput.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Search_KeyDown), true);
        // Thumb 会处理指针事件，必须接收已处理事件才能可靠识别拖动结束。
        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Seek_Pressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Seek_Released), true);
        SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Seek_Released), true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Seek_Canceled), true);
        seekTimer = DispatcherQueue.CreateTimer();
        seekTimer.Interval = TimeSpan.FromMilliseconds(250);
        seekTimer.IsRepeating = false;
        seekTimer.Tick += (_, _) => CommitSeek();
        music.Player.PlaybackSession.SeekCompleted += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || submittedSeek is not double target || Math.Abs(music.Player.PlaybackSession.Position.TotalSeconds - target) > 2) return;
            submittedSeek = null;
            PlaybackStatus.Text = music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused ? "已暂停" : "正在播放（FFmpeg）";
        });
        music.Player.MediaOpened += (sender, _) =>
        {
            var openedSource = sender.Source;
            DispatcherQueue.TryEnqueue(() =>
            { if (!closed && !playbackResumeOpening && openedSource is not null && ReferenceEquals(music.Player.Source, openedSource)) { mediaReady = true; PlaybackStatus.Text = "正在播放（FFmpeg）"; } });
        };
        music.Player.MediaEnded += (sender, _) =>
        {
            var endedSource = sender.Source;
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (closed || endedSource is null || !ReferenceEquals(music.Player.Source, endedSource)) return;
                var next = queue.Next(automatic: true);
                if (next is not null) await PlayTrackAsync(next);
                else { PlaybackStatus.Text = "队列播放结束"; pendingPlaybackResume = null; lastPlaybackResume = queue.Current is { SourceInstanceId: var id } ended && id != Guid.Empty ? new(ended, 0) : null; mediaReady = false; StopLinkedPlayback(); }
            });
        };
        music.Player.MediaFailed += (sender, args) =>
        {
            var failedSource = sender.Source;
            int code = args.ExtendedErrorCode?.HResult ?? 0;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (closed || failedSource is null || !ReferenceEquals(music.Player.Source, failedSource)) return;
                mediaReady = false;
                PlaybackStatus.Text = $"FFmpeg 播放链路无法读取此音频（{code:X8}）。可尝试其他曲目。";
                StopLinkedPlayback();
            });
        };
        progressTimer = DispatcherQueue.CreateTimer();
        progressTimer.Interval = TimeSpan.FromMilliseconds(500);
        progressTimer.Tick += (_, _) => UpdateProgress();
        progressTimer.Start();
        music.Player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!closed) PublishPlaybackSnapshot(); });
    }
    private async Task ShowLibraryAsync()
    {
        if (!RequireRemoteCapability(MusicSourceCapabilities.Browse)) return;
        if (!await NavigateMainContentAsync(LibraryPanel)) return;
        allMusicView = false; localView = false;
#if DEBUG
        if (IsSyntheticPreview) { SettingsPanel.Visibility = Visibility.Collapsed; LibraryPanel.Visibility = Visibility.Visible; await SwitchCollectionAsync(null); return; }
#endif
        SettingsPanel.Visibility = Visibility.Collapsed;
        LibraryPanel.Visibility = Visibility.Visible;
        await SwitchCollectionAsync(null);
        await LoadDevicesAsync();
    }
    private async void Library_Click(object sender, RoutedEventArgs e) => await ShowLibraryAsync();
    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsPanelAsync();
    private async Task ShowSettingsPanelAsync(SelectorBarItem? category = null)
    { if (await NavigateMainContentAsync(SettingsPanel) && category is not null) SettingsCategories.SelectedItem = category; }
    private async void ConnectionSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsPanelAsync(ConnectionCategory);
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    { RequestMetadataSync(refresh: true); if (allMusicView) await LoadAllMusicPageAsync(1, true); else if (localView) await LoadLocalPageAsync(1, true); else await LoadPageAsync(1); }
    private async void Previous_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(page - 1);
    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(page + 1);
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchFromTopAsync();
    private async void Search_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await SearchFromTopAsync();
    }
    private async Task SearchAsync()
    {
        string query = SearchInput.Text.Trim();
        if (!allMusicView && !localView && !RequireRemoteCapability(query.Length == 0 ? MusicSourceCapabilities.Browse : MusicSourceCapabilities.Search)) return;
        favoritesView = false;
        parentArtist = null; collectionKind = null; collection = null; UpdateCollectionView();
        searchQuery = query;
        await LoadPageAsync(1);
    }
    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
        => await ClearTopSearchAsync();

    private async Task LoadPageAsync(int requestedPage)
    {
        if (closed) return;
        if (allMusicView) { await LoadAllMusicPageAsync(requestedPage); return; }
        if (localView) { await LoadLocalPageAsync(requestedPage); return; }
        if (!RequireRemoteCapability(favoritesView ? MusicSourceCapabilities.ReadFavorites
            : collectionKind is null && searchQuery.Length > 0 ? MusicSourceCapabilities.Search : MusicSourceCapabilities.Browse)) return;
        libraryWork.Cancel();
        var api = ViewModel.AuthenticatedClient;
        if (api is null && !IsSyntheticPreview) { PlaybackStatus.Text = "请先在连接与账号中登录。"; return; }
        if (closed) return;
        libraryWork.Cancel(); libraryWork.Dispose(); libraryWork = new();
        var ct = libraryWork.Token;
        string query = searchQuery;
        var kind = collectionKind;
        var selectedCollection = collection;
        var artist = parentArtist;
        page = 1; total = 0;
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        TrackList.ItemsSource = null;
        CollectionList.ItemsSource = null; CollectionOpen.IsEnabled = false;
        PageStatus.Text = kind is not null ? "正在加载…" : query.Length == 0 ? "正在加载曲库…" : "正在搜索…";
        try
        {
#if DEBUG
            if (IsSyntheticPreview && syntheticLibraryFailure is { } fault)
            {
                syntheticLibraryFailure = null;
                if (fault.DelayMilliseconds > 0) await Task.Delay(fault.DelayMilliseconds);
                throw new MusicApiException(fault.Failure);
            }
#endif
            if (kind is not null && selectedCollection is null)
            {
                CollectionPage collections;
#if DEBUG
                if (IsSyntheticPreview) collections = GetSyntheticCollections(kind.Value, Math.Max(1, requestedPage));
                else
#endif
                collections = artist is null && CurrentMetadata(api) is { } cachedCollections
                    ? new CollectionPage((kind == CollectionKind.Album ? cachedCollections.Albums : cachedCollections.Artists)
                        .Select(item => item.Detail).Skip((Math.Max(1, requestedPage) - 1) * 50).Take(50).ToArray(),
                        (kind == CollectionKind.Album ? cachedCollections.Albums : cachedCollections.Artists).Count)
                    : artist is not null && CurrentMetadata(api)?.Artists.FirstOrDefault(item => item.Detail.Id == artist.Id) is { } cachedArtist
                    ? new CollectionPage(cachedArtist.Albums.Skip((Math.Max(1, requestedPage) - 1) * 50).Take(50).ToArray(), cachedArtist.Albums.Count)
                    : artist is null
                    ? await api!.ListCollectionsAsync(kind.Value, Math.Max(1, requestedPage), 50, ct)
                    : await api!.ListArtistAlbumsAsync(artist.Id, Math.Max(1, requestedPage), 50, ct);
                if (ct.IsCancellationRequested || closed) return;
                page = Math.Max(1, requestedPage); total = collections.Total;
                CollectionList.ItemsSource = collections.Items;
                CollectionList.SelectedIndex = collections.Items.Count > 0 ? 0 : -1;
                CollectionOpen.IsEnabled = collections.Items.Count > 0;
                PageStatus.Text = total == 0 ? $"暂无{CollectionLabel}。" : $"{CollectionLabel} · 第 {page} 页，共 {total} 项";
                return;
            }
            TrackPage result;
            if (kind is not null && selectedCollection is not null)
            {
                MusicCollection detail;
#if DEBUG
                if (IsSyntheticPreview) { detail = selectedCollection; result = await GetSyntheticCollectionTracksAsync(selectedCollection, Math.Max(1, requestedPage)); }
                else
#endif
                {
                    var cached = CurrentMetadata(api);
                    var entry = (kind == CollectionKind.Album ? cached?.Albums : cached?.Artists)?.FirstOrDefault(item => item.Detail.Id == selectedCollection.Id);
                    if (entry is not null)
                    { detail = entry.Detail; result = new(entry.Tracks.Skip((Math.Max(1, requestedPage) - 1) * 50).Take(50).ToArray(), entry.Tracks.Count); }
                    else
                    { detail = await api!.GetCollectionAsync(kind.Value, selectedCollection.Id, ct);
                      result = await api.ListCollectionTracksAsync(kind.Value, selectedCollection.Id, Math.Max(1, requestedPage), 50, ct); }
                }
                if (ct.IsCancellationRequested || closed) return;
                collection = detail; UpdateCollectionView();
            }
            else
            {
#if DEBUG
            if (IsSyntheticPreview) result = await GetSyntheticPageAsync(query, Math.Max(1, requestedPage), favoritesView);
            else
#endif
            result = CurrentMetadata(api) is { } cachedTracks ? CachedTrackPage(cachedTracks, requestedPage, favoritesView, query)
                : favoritesView ? await api!.ListFavoritesAsync(Math.Max(1, requestedPage), 50, ct) : query.Length == 0
                ? await api!.ListTracksAsync(Math.Max(1, requestedPage), 50, ct)
                : await api!.SearchTracksAsync(query, Math.Max(1, requestedPage), 50, ct);
            }
            if (ct.IsCancellationRequested || closed) return;
            page = Math.Max(1, requestedPage); total = result.Total;
            TrackList.ItemsSource = result.Tracks;
            TrackList.SelectedIndex = result.Tracks.Count > 0 ? 0 : -1;
            PageStatus.Text = $"{(favoritesView ? "收藏" : kind is not null ? CollectionLabel : query.Length == 0 ? "曲库" : "搜索结果")} · 第 {page} 页，共 {total} 首";
            if (result.Tracks.Count == 0) PageStatus.Text = kind is not null ? "当前详情暂无曲目。" : query.Length == 0 ? "当前资料库暂无曲目。" : "没有找到匹配歌曲，可修改关键词重试。";
            if (favoritesView && result.Tracks.Count == 0) PageStatus.Text = "暂无收藏歌曲。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (MusicApiException error) when (error.Failure == MusicFailure.Unauthorized)
        {
            if (!closed && !ct.IsCancellationRequested)
            { await ViewModel.ExpireSessionAsync(); PlaybackStatus.Text = "登录已失效，请在连接与账号中重新登录。"; }
        }
        catch (Exception) { if (!closed && !ct.IsCancellationRequested) PageStatus.Text = "加载失败，请检查网络后重新搜索或刷新。"; }
        finally
        {
            if (!closed && !ct.IsCancellationRequested) { PreviousPage.IsEnabled = page > 1; NextPage.IsEnabled = (long)page * 50 < total; }
        }
    }
    private async void PlaySelected_Click(object sender, RoutedEventArgs e)
    {
        if (TrackList.SelectedItem is not MusicTrack track) return;
        await StartQueueAsync(track);
    }
    private async Task StartQueueAsync(MusicTrack track)
    {
        if (track.IsCue) { PlaybackStatus.Text = "CUE 分轨播放将在转码适配阶段接入。"; return; }
        if (TrackList.ItemsSource is not IEnumerable<MusicTrack> tracks || !TryReplacePlaybackQueue(tracks, track.Reference)) return;
        queueOrigin = allMusicView ? "所有音乐当前列表" : localView ? "本地音乐当前页" : favoritesView ? "收藏当前页" : collectionKind is not null ? $"{CollectionLabel}详情当前页" : searchQuery.Length == 0 ? "曲库当前页" : "搜索结果当前页";
        await PlayTrackAsync(track);
    }
    private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
    { if (queue.Previous() is { } track) await PlayTrackAsync(track); }
    private async void NextTrack_Click(object sender, RoutedEventArgs e)
    { if (queue.Next(automatic: false) is { } track) await PlayTrackAsync(track); }
    private void QueueMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string value } && Enum.TryParse<QueueMode>(value, out var mode) && Enum.IsDefined(mode))
        { queueStartupModeOverride = mode; queueSessionExplicitlyCleared = false; queue.Mode = mode; UpdateQueueModeControl(); }
    }
    private void UpdateQueueModeControl()
    {
        var (name, glyph) = queue.Mode switch
        {
            QueueMode.RepeatAll => ("列表循环", "\uE8EE"),
            QueueMode.RepeatOne => ("单曲循环", "\uE8ED"),
            QueueMode.Shuffle => ("随机播放", "\uE8B1"),
            _ => ("顺序播放", "\uE72A")
        };
        QueueModeIcon.Glyph = glyph;
        SettingsModeSummary.Text = name;
        string description = "播放模式：" + name;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QueueModeButton, description);
        ToolTipService.SetToolTip(QueueModeButton, description);
        SequentialModeItem.IsChecked = queue.Mode == QueueMode.Sequential;
        RepeatAllModeItem.IsChecked = queue.Mode == QueueMode.RepeatAll;
        RepeatOneModeItem.IsChecked = queue.Mode == QueueMode.RepeatOne;
        ShuffleModeItem.IsChecked = queue.Mode == QueueMode.Shuffle;
    }
    private async Task PlayTrackAsync(MusicTrack track, double? initialPositionSeconds = null)
    {
        if (closed) return;
        resumeStartupAbandoned = true;
        if (initialPositionSeconds is null) { pendingPlaybackResume = null; lastPlaybackResume = track.SourceInstanceId != Guid.Empty ? new(track, 0) : null; }
        CancelWork(); StopLinkedPlayback(); mediaReady = false;
        playbackResumeOpening = initialPositionSeconds is not null;
        ClearDecoderLyrics();
        UpdateQueueStatus();
        if (!IsSyntheticPreview && !ViewModel.PlaybackRouter.CanPlay(track.Reference))
        {
            playbackResumeOpening = false; ClearPresentation(); NowPlaying.Text = "";
            pendingPlaybackResume = lastPlaybackResume;
            ApplyPendingResumePresentation(); UpdateProgress();
            PlaybackStatus.Text = "歌曲来源未连接或不支持播放；队列与当前歌曲记录保留，恢复来源后点击播放。"; return;
        }
        BeginLinkedPlayback(track);
        var embedded = new TaskCompletionSource<Lyrics>(TaskCreationOptions.RunContinuationsAsynchronously);
        decoderLyrics = embedded; decoderLyricsReference = track.Reference;
        _ = LoadLyricsAsync(track);
        var ct = work.Token;
        PlaybackStatus.Text = "正在加载音频…";
        try
        {
            var stream = await OpenPlaybackStreamAsync(track, ct);
            if (ct.IsCancellationRequested || closed) { stream.Dispose(); return; }
            NowPlaying.Text = track.Title + " · " + track.Artist;
            await music.OpenAsync(stream, ct, initialPositionSeconds);
            if (ct.IsCancellationRequested || closed) return;
            if (initialPositionSeconds is not null)
            { playbackResumeOpening = false; mediaReady = true; pendingPlaybackResume = null; lastPlaybackResume = CapturePlaybackResume(); PlaybackStatus.Text = "正在播放（已恢复进度）"; UpdateProgress(); }
            embedded.TrySetResult(music.EmbeddedLyrics);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (MusicApiException error) when (error.Failure == MusicFailure.Unauthorized)
        {
            if (!closed && !ct.IsCancellationRequested)
            {
                if (ViewModel.AuthenticatedClient?.SourceInstanceId == track.SourceInstanceId)
                    await ViewModel.ExpireSessionAsync();
                else if (navidromeSources.ContainsKey(track.SourceInstanceId))
                { RemoveNavidromeSource(track.SourceInstanceId); navidromeStatus[track.SourceInstanceId] = "认证失效，请编辑后重新输入密码"; }
                else if (embySources.ContainsKey(track.SourceInstanceId))
                { RemoveEmbySource(track.SourceInstanceId); embyStatus[track.SourceInstanceId] = "认证失效，请编辑后重新输入密码"; }
                else if (httpJsonSources.ContainsKey(track.SourceInstanceId))
                { RemoveHttpJsonSource(track.SourceInstanceId); httpJsonStatus[track.SourceInstanceId] = "服务需要认证，当前仅支持匿名接口"; }
                PlaybackStatus.Text = "歌曲来源的登录已失效，请重新连接。";
            }
        }
        catch (FileNotFoundException) { if (!closed && !ct.IsCancellationRequested) PlaybackStatus.Text = "音频文件或 FFmpeg 不可用，请检查文件位置与解码器安装。"; }
        catch (Exception) { if (!closed && !ct.IsCancellationRequested) PlaybackStatus.Text = "音频加载失败，请检查网络、登录状态或选择其他曲目。"; }
        finally { embedded.TrySetResult(Lyrics.Empty); if (!ct.IsCancellationRequested && !closed) { playbackResumeOpening = false; snapshotLoading = false; if (music.Player.Source is null) { mediaReady = false; StopLinkedPlayback(); ApplyPendingResumePresentation(); } else PublishPlaybackSnapshot(); } }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { if (playbackResumeOpening) { playbackResumeOpening = false; CancelWork(); StopLinkedPlayback(); mediaReady = false; } else music.Player.Pause(); if (mediaReady || pendingPlaybackResume is not null) PlaybackStatus.Text = "已暂停"; }
    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (playbackResumeOpening || snapshotLoading) return;
        if (mediaReady)
        {
            if (music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) Pause_Click(sender, e);
            else Resume_Click(sender, e);
        }
        else if (pendingPlaybackResume is not null || queue.Current is not null) await PlayCurrentOrResumeAsync();
        else if (LyricsPanel.Visibility != Visibility.Visible) PlaySelected_Click(sender, e);
    }
    private void UpdateQueueStatus()
    {
        QueueStatus.Text = queue.Count == 0 ? "播放队列为空" :
        queue.Current is null ? $"队列 {queue.Count} 首 · 未选择播放曲目" :
        $"队列 {queue.Index + 1}/{queue.Count} · 来自开始播放时的{queueOrigin}";
        RenderQueuePage();
    }
    private async void ManageQueue_Click(object sender, RoutedEventArgs e)
    { if (queuePage is not null) await CloseQueuePageAsync(); else ShowQueuePage(); }
    private void Resume_Click(object sender, RoutedEventArgs e) { if (mediaReady) { music.Player.Play(); PlaybackStatus.Text = "正在播放"; } }
    private void Stop_Click(object sender, RoutedEventArgs e) { resumeStartupAbandoned = true; playbackResumeOpening = false; pendingPlaybackResume = null; lastPlaybackResume = queue.Current is { SourceInstanceId: var id } current && id != Guid.Empty ? new(current, 0) : null; ClearDecoderLyrics(); ClearPresentation(); CancelWork(); StopLinkedPlayback(); mediaReady = false; PlaybackStatus.Text = "已停止"; NowPlaying.Text = ""; }
    private void Mute_Click(object sender, RoutedEventArgs e)
    { music.Player.IsMuted = MuteToggle.IsChecked == true; UpdateVolumeControl(); }
    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    { if (music is not null) { music.Player.Volume = e.NewValue / 100; if (VolumeIcon is not null && MuteToggle is not null) UpdateVolumeControl(); } }
    private void UpdateVolumeControl()
    {
        bool muted = music.Player.IsMuted;
        MuteToggle.IsChecked = muted;
        VolumeIcon.Glyph = muted || music.Player.Volume == 0 ? "\uE74F" : "\uE767";
        string action = muted ? "取消静音" : "静音";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MuteToggle, action);
        ToolTipService.SetToolTip(MuteToggle, $"{action}（音量 {music.Player.Volume * 100:0}%）");
    }
    private void Seek_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (updatingProgress || !mediaReady || !music.Player.PlaybackSession.CanSeek) return;
        pendingSeek = e.NewValue;
        UpdatePlaybackTimes(TimeSpan.FromSeconds(e.NewValue), music.Player.PlaybackSession.NaturalDuration);
        if (!draggingSeek) { seekTimer?.Stop(); seekTimer?.Start(); }
    }
    private void Seek_Pressed(object sender, PointerRoutedEventArgs e)
    { if (SeekSlider.IsEnabled) { draggingSeek = true; seekTimer?.Stop(); } }
    private void Seek_Released(object sender, PointerRoutedEventArgs e)
    { if (draggingSeek) { draggingSeek = false; CommitSeek(); } }
    private void Seek_Canceled(object sender, PointerRoutedEventArgs e) => ResetSeek();
    private void CommitSeek()
    {
        seekTimer?.Stop();
        if (draggingSeek || pendingSeek is not double target) return;
        pendingSeek = null;
        if (closed || !mediaReady || !music.Player.PlaybackSession.CanSeek) return;
        submittedSeek = target; seekDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
        try
        {
            music.Player.PlaybackSession.Position = TimeSpan.FromSeconds(target);
            PlaybackStatus.Text = "正在跳转…";
        }
        catch (Exception) { submittedSeek = null; PlaybackStatus.Text = "跳转失败，请重试。"; }
    }
    private void ResetSeek()
    { draggingSeek = false; pendingSeek = submittedSeek = null; seekTimer?.Stop(); }
    private void UpdateProgress()
    {
        if (closed) return;
        PublishPlaybackSnapshot();
        bool playing = mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        string playGlyph = playing ? "\uE769" : "\uE768";
        if (PlayPauseIcon.Glyph != playGlyph)
        {
            PlayPauseIcon.Glyph = playGlyph;
            string action = playing ? "暂停" : "播放";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayPauseButton, action);
            ToolTipService.SetToolTip(PlayPauseButton, action);
        }
        if (!mediaReady)
        {
            updatingProgress = true;
            try
            {
                if (SeekSlider.IsEnabled) SeekSlider.IsEnabled = false;
                double maximum = Math.Max(1, pendingPlaybackResume?.Track.DurationSeconds ?? 1);
                if (SeekSlider.Maximum != maximum) SeekSlider.Maximum = maximum;
                double position = Math.Min(pendingPlaybackResume?.PositionSeconds ?? 0, maximum);
                if (SeekSlider.Value != position) SeekSlider.Value = position;
                UpdatePlaybackTimes(TimeSpan.FromSeconds(pendingPlaybackResume?.PositionSeconds ?? 0), TimeSpan.FromSeconds(pendingPlaybackResume?.Track.DurationSeconds ?? 0));
                ApplyPendingResumePresentation();
            }
            finally { updatingProgress = false; }
            return;
        }
        UpdateLyricsPosition();
        var session = music.Player.PlaybackSession;
        double duration = session.NaturalDuration.TotalSeconds;
        if (submittedSeek is not null && DateTimeOffset.UtcNow >= seekDeadline)
        { submittedSeek = null; PlaybackStatus.Text = "跳转等待超时，可重试或重新播放。"; }
        updatingProgress = true;
        try
        {
            double maximum = Math.Max(1, duration);
            if (SeekSlider.Maximum != maximum) SeekSlider.Maximum = maximum;
            bool canSeek = mediaReady && duration > 0 && session.CanSeek;
            if (SeekSlider.IsEnabled != canSeek) SeekSlider.IsEnabled = canSeek;
            if (!draggingSeek && pendingSeek is null && submittedSeek is null)
            {
                double position = Math.Clamp(session.Position.TotalSeconds, 0, maximum);
                if (SeekSlider.Value != position) SeekSlider.Value = position;
                UpdatePlaybackTimes(session.Position, session.NaturalDuration);
            }
        }
        finally { updatingProgress = false; }
    }
    private void UpdatePlaybackTimes(TimeSpan position, TimeSpan duration)
    {
        string time = $"{position:mm\\:ss}", totalTime = $"{duration:mm\\:ss}";
        if (PlaybackTime.Text != time) PlaybackTime.Text = time;
        if (PlaybackDuration.Text != totalTime) PlaybackDuration.Text = totalTime;
    }
    private void CancelWork() { playbackResumeOpening = false; ResetSeek(); work.Cancel(); work.Dispose(); work = new(); }
    private void ResetLibrary()
    {
        if (snapshotTrack is { } previous && !IndependentSource(previous.SourceInstanceId)) RememberPlaybackBeforeSourceRemoval(previous.SourceInstanceId);
        ResetMetadataSync();
        ResetPlaylistNavigation();
        coverCache.Clear();
        favoriteWork.Cancel(); favoriteWork.Dispose(); favoriteWork = new();
        favoritesView = false; FavoriteStatus.Text = "";
        // 只移除旧飞牛来源；混合队列中的本地条目及正在播放的本地流仍可保留。
        bool preserveLocalQueue = queue.Current is { } current && IndependentSource(current.SourceInstanceId)
            && ViewModel.PlaybackRouter.CanPlay(current.Reference);
        Guid? revokedSource = ViewModel.AuthenticatedClient?.SourceInstanceId;
        foreach (var track in queue.Tracks.Where(track =>
            (!restoredQueueReferences.Contains(track.Reference) || track.SourceInstanceId == revokedSource)
            && (!IndependentSource(track.SourceInstanceId) || !ViewModel.PlaybackRouter.CanPlay(track.Reference))).ToArray()) queue.Remove(track.Reference);
        if (!preserveLocalQueue)
        {
            ClearDecoderLyrics(); ClearPresentation(); CancelWork(); StopLinkedPlayback(); mediaReady = false;
            NowPlaying.Text = "";
            PlaybackStatus.Text = queue.Count > 0 ? "旧飞牛播放已停止，本地条目保留在队列中。" : localView ? "本地音乐可直接播放。" : "登录后刷新曲库。";
        }
        UpdateQueueStatus();
        if (allMusicView)
        {
            libraryWork.Cancel();
            var localRows = (TrackList.ItemsSource as IEnumerable<MusicTrack>)?.Where(track => IndependentSource(track.SourceInstanceId)).ToArray() ?? [];
            TrackList.ItemsSource = localRows; TrackList.SelectedIndex = localRows.Length > 0 ? 0 : -1;
            page = 1; total = localRows.Length; PreviousPage.IsEnabled = NextPage.IsEnabled = false;
            PageStatus.Text = "飞牛连接已变化；当前保留其他来源歌曲，点击刷新重新汇总。";
            UpdateFavoriteButton(); return;
        }
        // 浏览来源与播放来源可以不同；保留本地筛选、分页及正在进行的扫描。
        if (localView) { UpdateFavoriteButton(); return; }
        libraryWork.Cancel();
        searchQuery = ""; SearchInput.Text = "";
        parentArtist = null; collectionKind = null; collection = null; collectionListPage = 1;
        CollectionList.ItemsSource = null; CollectionOpen.IsEnabled = false; UpdateCollectionView();
        TrackList.ItemsSource = null; PageStatus.Text = "";
        page = 1; total = 0; PreviousPage.IsEnabled = NextPage.IsEnabled = false;
    }
    private void ClosePlayback()
    { SavePlaybackResumeAtExit(); CloseAudioOutputs(); CloseLinkedServices(); StopLinkedPlayback(); closed = true; CloseQueuePage(force: true, restoreFocus: false); CloseFullPage(force: true, restoreFocus: false); activePlaylistPage?.Dispose(); activePlaylistPage = null; playlistNavigationWork?.Cancel(); StopAllUiMotions(); systemTheme.AnimationsEnabledChanged -= SystemMotionChanged; systemTheme.ColorValuesChanged -= SystemThemeChanged; lyricsWork.Cancel(); lyricsWork.Dispose(); localLifetime.Cancel(); CloseNavidrome(); CloseEmby(); CloseHttpJson(); localLifetime.Dispose(); foreach (var id in localSources.Keys) ViewModel.PlaybackRouter.Unregister(id); ResetSeek(); progressTimer?.Stop(); favoriteWork.Cancel(); favoriteWork.Dispose(); libraryWork.Cancel(); libraryWork.Dispose(); work.Cancel(); work.Dispose(); music.Dispose(); }
}
