using FnMusic.Core;
using FnMusic.App.Services;
using Windows.Media.Playback;

namespace FnMusic.App;

public sealed partial class MainWindow
{
    private readonly PlaybackSnapshotHub playbackSnapshots = new();
    private MusicTrack? snapshotTrack;
    private Guid? trackGeneration;
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
        var session = music.Player.PlaybackSession;
        bool hasSource = snapshotTrack is not null && music.Player.Source is not null;
        var state = snapshotLoading ? FnMusic.Core.PlaybackStatus.Loading : !hasSource ? FnMusic.Core.PlaybackStatus.Stopped
            : session.PlaybackState switch
            {
                MediaPlaybackState.Playing => FnMusic.Core.PlaybackStatus.Playing,
                MediaPlaybackState.Paused => FnMusic.Core.PlaybackStatus.Paused,
                MediaPlaybackState.Buffering => FnMusic.Core.PlaybackStatus.Buffering,
                MediaPlaybackState.Opening => FnMusic.Core.PlaybackStatus.Loading,
                _ => FnMusic.Core.PlaybackStatus.Stopped
            };
        double position = hasSource ? session.Position.TotalSeconds : 0;
        int line = snapshotTrack is not null && snapshotLyricsKind == PlaybackLyricsKind.Timed
            && playbackSnapshots.Lyrics.TrackGeneration == trackGeneration ? currentLyrics.FindLine(position) : -1;
        var track = snapshotTrack;
        playbackSnapshots.Publish(new(1, playbackSnapshots.InstanceId, 0, trackGeneration, DateTimeOffset.UtcNow,
            state, position, hasSource ? session.NaturalDuration.TotalSeconds : 0,
            track?.Title ?? "", track?.Artist ?? "", track?.Album ?? "", snapshotLyricsKind,
            line, line >= 0 ? currentLyrics.Lines[line].Text : "",
            mediaReady || queue.Current is not null, mediaReady,
            queue.CanPrevious, queue.CanNext, hasSource && mediaReady && session.CanSeek));
    }
    private async Task ExecutePlaybackCommandAsync(PlaybackCommand command)
    {
        if (closed || command.TrackGeneration != trackGeneration) return;
        switch (command.Kind)
        {
            case PlaybackCommandKind.Play:
                if (mediaReady) music.Player.Play();
                else if (queue.Current is { } track) await PlayTrackAsync(track);
                break;
            case PlaybackCommandKind.Pause: if (mediaReady) music.Player.Pause(); break;
            case PlaybackCommandKind.Toggle:
                if (mediaReady && music.Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) music.Player.Pause();
                else if (mediaReady) music.Player.Play();
                else if (queue.Current is { } current) await PlayTrackAsync(current);
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
            else if (ViewModel.AuthenticatedClient is { } api && api.SourceInstanceId == track.SourceInstanceId && track.CoverId is { } cover)
                bytes = await ReadRemoteCoverAsync(api, cover, FnMusic.Infrastructure.NasCoverSize.Medium, ct);
            if (!closed && !ct.IsCancellationRequested && generation == trackGeneration && bytes is { Length: > 0 and <= 4 * 1024 * 1024 } && systemMedia is not null)
                await systemMedia.UpdateArtworkAsync(generation, bytes);
        }
        catch (Exception) { /* Optional cover failures never affect audio or expose source details. */ }
    }
}
