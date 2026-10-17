using Melvune.Core;
using Melvune.Infrastructure;
using Microsoft.UI.Xaml;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private readonly PlaybackResumeStore playbackResumeStore = new(ThemeSettingsDirectory());
    private PlaybackResumeState? pendingPlaybackResume, lastPlaybackResume;
    private bool resumeStartupAbandoned, playbackResumeOpening;
    private readonly HashSet<TrackReference> restoredQueueReferences = [];
    private long queueSessionEditRevision;
    private bool queueSessionExplicitlyCleared;
    private QueueMode? queueStartupModeOverride;
#if DEBUG
    private Action? queueSessionAfterRead;
#endif
    private void MarkQueueSessionEdited() { resumeStartupAbandoned = true; ++queueSessionEditRevision; }
    private bool TryReplacePlaybackQueue(IEnumerable<MusicTrack> tracks, TrackReference selected)
    {
        if (!queue.Replace(tracks, selected))
        { PlaybackStatus.Text = "无法创建播放队列：最多支持 10000 首，请先筛选来源或选择较小歌单。当前队列保持。"; return false; }
        MarkQueueSessionEdited(); queueSessionExplicitlyCleared = false; restoredQueueReferences.Clear(); return true;
    }
    private bool TryInsertPlaybackNext(MusicTrack track)
    {
        if (!queue.InsertNext(track))
        { PlaybackStatus.Text = queue.Count >= PlaybackQueue.MaximumTracks ? "队列已达 10000 首，请先移除歌曲再添加。当前队列保持。" : "该歌曲无法加入下一首播放。"; return false; }
        MarkQueueSessionEdited(); queueSessionExplicitlyCleared = false;
        if (pendingPlaybackResume is { } saved && queue.Current is { } current && saved.Track.Reference != current.Reference)
        { pendingPlaybackResume = null; lastPlaybackResume = new(current, 0); }
        return true;
    }

    private async Task RestorePlaybackResumeAsync()
    {
        if (closed || resumeStartupAbandoned) return;
        try
        {
            long revision = queueSessionEditRevision;
            var restored = await playbackResumeStore.LoadSessionAsync(localLifetime.Token);
#if DEBUG
            queueSessionAfterRead?.Invoke();
#endif
            if (closed || resumeStartupAbandoned || revision != queueSessionEditRevision || restored is null) return;
            var snapshot = queueStartupModeOverride is { } chosenMode ? restored.Queue with { Mode = chosenMode } : restored.Queue;
            if (!queue.Restore(snapshot)) return;
            restoredQueueReferences.Clear();
            foreach (var item in queue.Tracks) restoredQueueReferences.Add(item.Reference);
            queueOrigin = restored.Origin;
            pendingPlaybackResume = lastPlaybackResume = queue.Current is { } current
                ? new(current, restored.PositionSeconds) : null;
            PlaybackStatus.Text = pendingPlaybackResume is { } saved
                ? "已恢复播放队列（" + queue.Count + " 首），点击播放从 " + TimeSpan.FromSeconds(saved.PositionSeconds).ToString(@"mm\:ss") + " 继续。"
                : queue.Count > 0 ? "已恢复播放队列，双击其中歌曲开始播放。" : "播放队列为空。";
            UpdateQueueModeControl(); UpdateQueueStatus(); ApplyPendingResumePresentation(); UpdateProgress();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) ShowMetadataNotice("上次播放队列无法读取，可直接选择歌曲播放。"); }
    }

    private void ApplyPendingResumePresentation()
    {
        if (pendingPlaybackResume is not { } saved || mediaReady || snapshotLoading || closed) return;
        NowPlaying.Text = saved.Track.Title + " · " + saved.Track.Artist; NowAlbum.Text = saved.Track.Album;
        if (NowCover.DataContext is not MusicTrack current || current.Reference != saved.Track.Reference)
            NowCover.DataContext = FullNowCover.DataContext = saved.Track;
        FullNowTitle.Text = saved.Track.Title; FullNowArtist.Text = string.Join(" · ", new[] { saved.Track.Artist, saved.Track.Album }.Where(value => value.Length > 0));
    }

    private MusicTrack? ResolveResumeTrack(MusicTrack saved)
    {
        if (!ViewModel.PlaybackRouter.CanPlay(saved.Reference)) return null;
        if (localSources.TryGetValue(saved.SourceInstanceId, out var local))
            return local.Tracks.FirstOrDefault(track => track.Reference == saved.Reference);
        if (navidromeSources.ContainsKey(saved.SourceInstanceId))
            return navidromeTracks.GetValueOrDefault(saved.SourceInstanceId)?.FirstOrDefault(track => track.Reference == saved.Reference);
        if (embySources.ContainsKey(saved.SourceInstanceId))
            return embyTracks.GetValueOrDefault(saved.SourceInstanceId)?.FirstOrDefault(track => track.Reference == saved.Reference);
        if (httpJsonSources.ContainsKey(saved.SourceInstanceId))
            return httpJsonTracks.GetValueOrDefault(saved.SourceInstanceId)?.FirstOrDefault(track => track.Reference == saved.Reference);
        return ViewModel.AuthenticatedClient?.SourceInstanceId == saved.SourceInstanceId ? saved : null;
    }

    private async Task PlayCurrentOrResumeAsync()
    {
        if (playbackResumeOpening || snapshotLoading) return;
        if (pendingPlaybackResume is { } saved)
        {
            var track = ResolveResumeTrack(saved.Track);
            if (track is null)
            { PlaybackStatus.Text = "上次歌曲来源未连接或歌曲不可用，请恢复对应音乐库或登录后再次点击播放。保存的进度保留。"; return; }
            if (queue.Current?.Reference != track.Reference)
            {
                if (!queue.Tracks.Any(item => item.Reference == track.Reference)) queue.InsertNext(track);
                queue.Select(track.Reference);
            }
            await PlayTrackAsync(track, saved.PositionSeconds); return;
        }
        if (queue.Current is { } current) await PlayTrackAsync(current);
    }

    private PlaybackResumeState? CapturePlaybackResume()
    {
        if (playbackResumeOpening && pendingPlaybackResume is not null) return pendingPlaybackResume;
        if (snapshotTrack is { SourceInstanceId: var id } track && id != Guid.Empty && mediaReady && music.Player.Source is not null)
        {
            double position = music.Player.PlaybackSession.Position.TotalSeconds;
            double duration = music.Player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (!double.IsFinite(duration) || duration <= 0) duration = track.DurationSeconds;
            if (!double.IsFinite(position) || position < 0) return lastPlaybackResume;
            return new(track with { DurationSeconds = duration }, Math.Clamp(position, 0, duration));
        }
        return pendingPlaybackResume ?? lastPlaybackResume;
    }

    private void RememberPlaybackBeforeSourceRemoval(Guid sourceId)
    {
        if (CapturePlaybackResume() is { } saved && saved.Track.SourceInstanceId == sourceId)
            pendingPlaybackResume = lastPlaybackResume = saved;
    }

    private void ForgetPlaybackResume()
    {
        resumeStartupAbandoned = true; MarkQueueSessionEdited(); queueSessionExplicitlyCleared = true;
        pendingPlaybackResume = lastPlaybackResume = null;
    }

    private PlaybackSessionState CapturePlaybackSession()
    {
        var snapshot = queue.CaptureSnapshot();
        var saved = CapturePlaybackResume();
        if (saved is not null)
        {
            // A source may have been revoked after preserving its position. Keep that
            // unavailable current reference alongside the remaining queue, never replace it.
            var tracks = snapshot.Tracks.Select(item => item.Reference == saved.Track.Reference ? saved.Track : item).ToList();
            if (!tracks.Any(item => item.Reference == saved.Track.Reference)) tracks.Insert(0, saved.Track);
            snapshot = snapshot with
            {
                Tracks = tracks.AsReadOnly(), Current = saved.Track.Reference,
                RequestedNext = Array.AsReadOnly(snapshot.RequestedNext.Where(reference => reference != saved.Track.Reference).ToArray())
            };
        }
        return new(snapshot, saved?.PositionSeconds ?? 0, queueOrigin);
    }

    private void SavePlaybackResumeAtExit()
    {
        try
        {
            var session = CapturePlaybackSession();
            if (session.Queue.Tracks.Count > 0 || (!queueSessionExplicitlyCleared && queue.Mode != QueueMode.Sequential)) playbackResumeStore.SaveSession(session);
            else if (resumeStartupAbandoned) playbackResumeStore.Clear();
        }
        catch (Exception) { /* A failed optional record must not prevent closing the player. */ }
    }

    private void StopUnavailableSource(Guid sourceId)
    {
        RememberPlaybackBeforeSourceRemoval(sourceId);
        var saved = pendingPlaybackResume;
        Stop_Click(this, new RoutedEventArgs());
        if (saved?.Track.SourceInstanceId == sourceId) pendingPlaybackResume = lastPlaybackResume = saved;
    }
}
