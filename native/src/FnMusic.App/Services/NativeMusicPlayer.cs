using Windows.Media.Playback;
using FnMusic.Core;

namespace FnMusic.App.Services;

/// <summary>Owns the current seekable decoder; Windows handles clock, volume and output.</summary>
public sealed class NativeMusicPlayer : IDisposable
{
    public MediaPlayer Player { get; } = new() { AutoPlay = false, Volume = 0.5 };
    private SeekablePlayback? seekable;
    private readonly object cleanupGate = new();
    public Task PendingCleanup { get; private set; } = Task.CompletedTask;
    public Lyrics EmbeddedLyrics { get; private set; } = Lyrics.Empty;

    public async Task OpenAsync(Stream stream, CancellationToken ct = default, double? initialPositionSeconds = null)
    {
        if (initialPositionSeconds is double requested && (!double.IsFinite(requested) || requested < 0))
            throw new ArgumentOutOfRangeException(nameof(initialPositionSeconds));
        Stop();
        var opening = new SeekablePlayback(stream);
        seekable = opening;
        using var cancel = ct.Register(() => _ = opening.Stop());
        try
        {
            var decoded = await opening.Opening;
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(seekable, opening) || opening.IsStopped)
                throw new OperationCanceledException("Playback was replaced.");
            EmbeddedLyrics = opening.EmbeddedLyrics;
            var source = decoded.CreateMediaPlaybackItem();
            if (initialPositionSeconds is > 0)
                await OpenAtPositionAsync(opening, source, initialPositionSeconds.Value, ct);
            else
            {
                Player.Source = source;
                Player.Play();
            }
        }
        catch
        {
            if (ReferenceEquals(seekable, opening)) Stop();
            else TrackCleanup(opening.Stop());
            throw;
        }
    }

    private async Task OpenAtPositionAsync(SeekablePlayback opening, IMediaPlaybackSource source,
        double initialPositionSeconds, CancellationToken ct)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seeked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = Player.PlaybackSession;
        int seekRequested = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        bool IsCurrent() => ReferenceEquals(seekable, opening) && !opening.IsStopped
            && ReferenceEquals(Player.Source, source);
        void OnOpened(MediaPlayer sender, object args)
        { if (IsCurrent()) opened.TrySetResult(true); }
        void OnSeekCompleted(MediaPlaybackSession sender, object args)
        { if (Volatile.Read(ref seekRequested) != 0 && IsCurrent()) seeked.TrySetResult(true); }
        void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        { if (IsCurrent()) failed.TrySetResult(new IOException("Audio could not be opened at the saved position.")); }

        async Task UntilAsync(Func<bool> ready)
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) throw new OperationCanceledException("Playback was replaced.");
                if (failed.Task.IsCompleted) throw await failed.Task;
                if (ready()) return;
                await Task.Delay(25, timeout.Token);
            }
        }

        // Subscribe before assigning Source. Keep playback paused until the decoder's
        // seek completes, so restoring never emits the beginning of the song first.
        Player.MediaOpened += OnOpened;
        Player.MediaFailed += OnFailed;
        session.SeekCompleted += OnSeekCompleted;
        try
        {
            Player.Source = source;
            await UntilAsync(() => opened.Task.IsCompleted && session.CanSeek && session.NaturalDuration > TimeSpan.Zero);
            double target = Math.Clamp(initialPositionSeconds, 0, Math.Max(0, session.NaturalDuration.TotalSeconds - .25));
            if (target > 0)
            {
                Volatile.Write(ref seekRequested, 1);
                session.Position = TimeSpan.FromSeconds(target);
                await UntilAsync(() => seeked.Task.IsCompleted && Math.Abs(session.Position.TotalSeconds - target) <= .25);
            }
            // Recheck after every asynchronous step; an older restore must not start
            // a replacement track or a stream that was already stopped.
            timeout.Token.ThrowIfCancellationRequested();
            if (!IsCurrent()) throw new OperationCanceledException("Playback was replaced.");
            if (failed.Task.IsCompleted) throw await failed.Task;
            Player.Play();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("Opening the saved playback position timed out."); }
        finally
        {
            Player.MediaOpened -= OnOpened;
            Player.MediaFailed -= OnFailed;
            session.SeekCompleted -= OnSeekCompleted;
        }
    }

    private void TrackCleanup(Task task)
    {
        lock (cleanupGate)
            PendingCleanup = PendingCleanup.IsCompletedSuccessfully ? task : Task.WhenAll(PendingCleanup, task);
    }

    public void Stop()
    {
        EmbeddedLyrics = Lyrics.Empty;
        var previous = seekable; seekable = null;
        Player.Pause();
        Player.Source = null;
        // Detach before invalidating the old input, so its shutdown is not reported
        // as a failure of the next MediaPlaybackItem.
        if (previous is not null) TrackCleanup(previous.Stop());
    }

    public void Dispose() { Stop(); Player.Dispose(); }
}
