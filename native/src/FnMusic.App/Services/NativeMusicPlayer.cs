using Windows.Media.Playback;

namespace FnMusic.App.Services;

/// <summary>Owns the current seekable decoder; Windows handles clock, volume and output.</summary>
public sealed class NativeMusicPlayer : IDisposable
{
    public MediaPlayer Player { get; } = new() { AutoPlay = false, Volume = 0.5 };
    private SeekablePlayback? seekable;
    private readonly object cleanupGate = new();
    public Task PendingCleanup { get; private set; } = Task.CompletedTask;

    public async Task OpenAsync(Stream stream, CancellationToken ct = default)
    {
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
            Player.Source = decoded.CreateMediaPlaybackItem();
            Player.Play();
        }
        catch
        {
            if (ReferenceEquals(seekable, opening)) Stop();
            else TrackCleanup(opening.Stop());
            throw;
        }
    }

    private void TrackCleanup(Task task)
    {
        lock (cleanupGate)
            PendingCleanup = PendingCleanup.IsCompletedSuccessfully ? task : Task.WhenAll(PendingCleanup, task);
    }

    public void Stop()
    {
        var previous = seekable; seekable = null;
        Player.Pause();
        Player.Source = null;
        // Detach before invalidating the old input, so its shutdown is not reported
        // as a failure of the next MediaPlaybackItem.
        if (previous is not null) TrackCleanup(previous.Stop());
    }

    public void Dispose() { Stop(); Player.Dispose(); }
}
