using FnMusic.Core;
using FnMusic.Infrastructure;
using Microsoft.UI.Xaml;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly PlaybackResumeStore playbackResumeStore = new(ThemeSettingsDirectory());
    private PlaybackResumeState? pendingPlaybackResume, lastPlaybackResume;
    private bool resumeStartupAbandoned, playbackResumeOpening;

    private async Task RestorePlaybackResumeAsync()
    {
        if (closed || resumeStartupAbandoned) return;
        try
        {
            var restored = await playbackResumeStore.LoadAsync(localLifetime.Token);
            if (closed || resumeStartupAbandoned || restored is null) return;
            pendingPlaybackResume = lastPlaybackResume = restored;
            queue.Replace([restored.Track], restored.Track.Reference); queueOrigin = "上次播放的歌曲";
            PlaybackStatus.Text = "已恢复上次播放记录，点击播放从 " + TimeSpan.FromSeconds(restored.PositionSeconds).ToString(@"mm\:ss") + " 继续。";
            UpdateQueueStatus(); ApplyPendingResumePresentation(); UpdateProgress();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!closed) ShowMetadataNotice("上次播放记录无法读取，可直接选择歌曲播放。"); }
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
            if (queue.Current?.Reference != track.Reference) queue.Replace([track], track.Reference);
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
        resumeStartupAbandoned = true;
        pendingPlaybackResume = lastPlaybackResume = null;
    }

    private void SavePlaybackResumeAtExit()
    {
        try
        {
            if (CapturePlaybackResume() is { } saved) playbackResumeStore.Save(saved);
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
