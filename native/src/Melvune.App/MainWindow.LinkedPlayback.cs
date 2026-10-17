using Melvune.Core;
using Melvune.App.Services;
using Windows.Media.Playback;

namespace Melvune.App;

public sealed partial class MainWindow
{
    private readonly PlaybackSnapshotHub playbackSnapshots = new();
    private MusicTrack? snapshotTrack;
    private Guid? trackGeneration;
    private TrackReference? pendingResumeSnapshotReference;
    private bool snapshotLoading;
    private PlaybackLyricsKind snapshotLyricsKind = PlaybackLyricsKind.Empty;

    private void StopLinkedPlayback()
    {
        ClearPresentation();
        music.Stop(); snapshotTrack = null; trackGeneration = null; snapshotLoading = false;
        snapshotLyricsKind = PlaybackLyricsKind.Empty;
        playbackSnapshots.SetLyrics(null, snapshotLyricsKind, Lyrics.Empty);
        PublishPlaybackSnapshot();
    }
    private void BeginLinkedPlayback(MusicTrack track)
    {
        snapshotTrack = track; trackGeneration = Guid.NewGuid(); snapshotLoading = true;
        snapshotLyricsKind = PlaybackLyricsKind.Loading;
        playbackSnapshots.SetLyrics(trackGeneration, snapshotLyricsKind, Lyrics.Empty);
        PublishPlaybackSnapshot();
        _ = LoadSystemArtworkAsync(track, trackGeneration.Value, work.Token);
    }
    private void PublishPlaybackSnapshot()
    {
        if (closed) return;
        if (!closingIntegration && snapshotTrack is null && !snapshotLoading && pendingPlaybackResume is { } saved)
        {
            // A restored song is paused metadata, not an opened audio stream. Give it
            // a stable command identity so SMTC can invoke the same resume path as
            // the playback button without permitting seeks or pauses before loading.
            if (trackGeneration is null || pendingResumeSnapshotReference != saved.Track.Reference)
            {
                trackGeneration = Guid.NewGuid(); pendingResumeSnapshotReference = saved.Track.Reference;
                playbackSnapshots.SetLyrics(trackGeneration, PlaybackLyricsKind.Empty, Lyrics.Empty);
            }
            var pendingTrack = saved.Track;
            playbackSnapshots.Publish(new(1, playbackSnapshots.InstanceId, 0, trackGeneration, DateTimeOffset.UtcNow,
                Melvune.Core.PlaybackStatus.Paused, saved.PositionSeconds, pendingTrack.DurationSeconds,
                pendingTrack.Title, pendingTrack.Artist, pendingTrack.Album, PlaybackLyricsKind.Empty, -1, "",
                ResolveResumeTrack(pendingTrack) is not null, false, queue.CanPrevious, queue.CanNext, false));
            return;
        }
        pendingResumeSnapshotReference = null;
        if (snapshotTrack is null && !snapshotLoading) trackGeneration = null;
        var session = music.Player.PlaybackSession;
        bool hasSource = snapshotTrack is not null && music.Player.Source is not null;
        var state = snapshotLoading ? Melvune.Core.PlaybackStatus.Loading : !hasSource ? Melvune.Core.PlaybackStatus.Stopped
            : session.PlaybackState switch
            {
                MediaPlaybackState.Playing => Melvune.Core.PlaybackStatus.Playing,
                MediaPlaybackState.Paused => Melvune.Core.PlaybackStatus.Paused,
                MediaPlaybackState.Buffering => Melvune.Core.PlaybackStatus.Buffering,
                MediaPlaybackState.Opening => Melvune.Core.PlaybackStatus.Loading,
                _ => Melvune.Core.PlaybackStatus.Stopped
            };
        double position = hasSource ? session.Position.TotalSeconds : 0;
        int line = snapshotTrack is not null && snapshotLyricsKind == PlaybackLyricsKind.Timed
            && playbackSnapshots.Lyrics.TrackGeneration == trackGeneration ? currentLyrics.FindLine(position) : -1;
        var track = snapshotTrack;
        playbackSnapshots.Publish(new(1, playbackSnapshots.InstanceId, 0, trackGeneration, DateTimeOffset.UtcNow,
            state, position, hasSource ? session.NaturalDuration.TotalSeconds : 0,
            track?.Title ?? "", track?.Artist ?? "", track?.Album ?? "", snapshotLyricsKind,
            line, line >= 0 ? currentLyrics.Lines[line].Text : "",
            mediaReady || queue.Current is not null || pendingPlaybackResume is not null, mediaReady,
            queue.CanPrevious, queue.CanNext, hasSource && mediaReady && session.CanSeek));
    }
    private async Task ExecutePlaybackCommandAsync(PlaybackCommand command)
    {
        if (closed || command.TrackGeneration != trackGeneration) return;
        switch (command.Kind)
        {
            case PlaybackCommandKind.Play:
                if (mediaReady) music.Player.Play();
                else await PlayCurrentOrResumeAsync();
                break;
            case PlaybackCommandKind.Pause: Pause_Click(this, new()); break;
            case PlaybackCommandKind.Toggle:
                if (mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) music.Player.Pause();
                else if (mediaReady) music.Player.Play();
                else await PlayCurrentOrResumeAsync();
                break;
            case PlaybackCommandKind.Stop: Stop_Click(this, new()); break;
            case PlaybackCommandKind.Previous: if (queue.Previous() is { } previous) await PlayTrackAsync(previous); break;
            case PlaybackCommandKind.Next: if (queue.Next(false) is { } next) await PlayTrackAsync(next); break;
            case PlaybackCommandKind.Seek:
                if (mediaReady && music.Player.PlaybackSession.CanSeek && command.PositionSeconds is double target && double.IsFinite(target))
                { ResetSeek(); pendingSeek = Math.Clamp(target, 0, music.Player.PlaybackSession.NaturalDuration.TotalSeconds); CommitSeek(); }
                break;
        }
        PublishPlaybackSnapshot();
    }
    private async Task LoadSystemArtworkAsync(MusicTrack track, Guid generation, CancellationToken ct)
    {
        try
        {
            byte[]? bytes = null;
            if (localSources.GetValueOrDefault(track.SourceInstanceId) is { } local)
            {
                await foreach (var candidate in local.GetSidecarCoversAsync(track.Reference, ct))
                {
                    if (candidate.Length is > 0 and <= 4 * 1024 * 1024)
                    {
                        try { await ArtworkDecoder.DecodeAsync(candidate, ArtworkDecoder.ThumbnailEdge, ct); bytes = candidate; break; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { }
                    }
                }
                bytes ??= await LocalArtworkReader.ReadThumbnailAsync(local.GetFilePath(track.Reference), ArtworkDecoder.ThumbnailEdge, ct);
            }
            else if (navidromeSources.GetValueOrDefault(track.SourceInstanceId) is { } nav)
            {
                using var artwork = await nav.OpenArtworkAsync(track, ArtworkDecoder.ThumbnailEdge, ct);
                if (artwork is not null) { using var output = new MemoryStream(); await artwork.CopyToAsync(output, ct); bytes = output.ToArray(); }
                if (!CurrentNavidrome(nav)) return;
            }
            else if (embySources.GetValueOrDefault(track.SourceInstanceId) is { } emby)
            {
                bytes = await emby.ReadCoverAsync(track, ct);
                if (!CurrentEmby(emby)) return;
            }
            else if (httpJsonSources.GetValueOrDefault(track.SourceInstanceId) is { } json)
            {
                bytes = await json.ReadCoverAsync(track, ct);
                if (!CurrentHttpJson(json)) return;
            }
            else if (ViewModel.AuthenticatedClient is { } api && api.SourceInstanceId == track.SourceInstanceId && track.CoverId is { } cover)
                bytes = await ReadRemoteCoverAsync(api, cover, Melvune.Infrastructure.NasCoverSize.Medium, ct);
            if (!closed && !ct.IsCancellationRequested && generation == trackGeneration && bytes is { Length: > 0 and <= 4 * 1024 * 1024 } && systemMedia is not null)
                await systemMedia.UpdateArtworkAsync(generation, bytes);
        }
        catch (Exception) { /* Optional cover failures never affect audio or expose source details. */ }
    }
}
