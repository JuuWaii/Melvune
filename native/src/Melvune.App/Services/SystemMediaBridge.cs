using System.Collections.Concurrent;
using System.Diagnostics;
using Melvune.Core;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Melvune.App.Services;

/// <summary>
/// Manually controls the desktop window's SMTC from public playback snapshots.
/// Construct and dispose on the window dispatcher, before disposing the player.
/// </summary>
public sealed class SystemMediaBridge : IDisposable
{
    public const int MaximumArtworkBytes = 8 * 1024 * 1024;
    private readonly SystemMediaTransportControls controls;
    private readonly DispatcherQueue dispatcher;
    private readonly PlaybackSnapshotHub hub;
    private readonly Func<PlaybackCommand, Task> execute;
    private readonly ConcurrentDictionary<long, InMemoryRandomAccessStream> pendingArtwork = new();
    private InMemoryRandomAccessStream? artwork;
    private PlaybackSnapshot? displayed;
    private long lastTimelineTick;
    private long artworkRequest;
    private int disposed;

    /// <summary>Fixed, non-sensitive diagnostic codes; exceptions and source data are never exported.</summary>
    public string? LastError { get; private set; }
    /// <summary>Fixed constructor step, retained if initialization throws. No window/source data.</summary>
    public static string InitializationStage { get; private set; } = "not-started";

    public SystemMediaBridge(MediaPlayer player, nint windowHandle, DispatcherQueue dispatcher,
        PlaybackSnapshotHub hub, Func<PlaybackCommand, Task> execute)
    {
        this.dispatcher = dispatcher;
        this.hub = hub;
        this.execute = execute;
        if (windowHandle == 0) throw new ArgumentException("A top-level app window is required.", nameof(windowHandle));
        // An unpackaged desktop app has no CoreWindow. The player's UWP SMTC
        // accessor can fail here; desktop interop binds the session to our HWND.
        InitializationStage = "get-for-window";
        controls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
        InitializationStage = "command-manager-get";
        var commandManager = player.CommandManager;
        InitializationStage = "command-manager-disable";
        commandManager.IsEnabled = false;
        InitializationStage = "button-subscribe";
        controls.ButtonPressed += OnButtonPressed;
        InitializationStage = "position-subscribe";
        controls.PlaybackPositionChangeRequested += OnPositionRequested;
        InitializationStage = "snapshot-subscribe";
        hub.Changed += OnSnapshot;
        InitializationStage = "initial-snapshot";
        Apply(hub.Current);
        InitializationStage = "ready";
    }

    private void OnSnapshot(PlaybackSnapshot snapshot)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        // Read the latest snapshot on the dispatcher, avoiding an older queued update
        // repainting a newer generation or position.
        if (dispatcher.HasThreadAccess) Apply(hub.Current);
        else dispatcher.TryEnqueue(() => Apply(hub.Current));
    }

    private void Apply(PlaybackSnapshot snapshot)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            bool active = snapshot.TrackGeneration.HasValue && snapshot.State != PlaybackStatus.Stopped;
            bool generationChanged = displayed?.TrackGeneration != snapshot.TrackGeneration;
            bool metadataChanged = generationChanged || displayed?.Title != snapshot.Title
                || displayed?.Artist != snapshot.Artist || displayed?.Album != snapshot.Album;
            bool stateChanged = displayed?.State != snapshot.State;
            controls.IsEnabled = active;
            controls.IsPlayEnabled = active && snapshot.CanPlay;
            controls.IsPauseEnabled = active && snapshot.CanPause;
            controls.IsPreviousEnabled = active && snapshot.CanPrevious;
            controls.IsNextEnabled = active && snapshot.CanNext;
            controls.IsStopEnabled = active;
            controls.IsFastForwardEnabled = false;
            controls.IsRewindEnabled = false;
            controls.PlaybackStatus = MapStatus(snapshot.State);

            bool clearDisplay = generationChanged || (!active &&
                (displayed is null || displayed.State != PlaybackStatus.Stopped));
            if (clearDisplay)
            {
                controls.DisplayUpdater.ClearAll();
                artwork?.Dispose();
                artwork = null;
            }
            if (active && (metadataChanged || stateChanged))
            {
                var updater = controls.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = snapshot.Title;
                updater.MusicProperties.Artist = snapshot.Artist;
                updater.MusicProperties.AlbumTitle = snapshot.Album;
                updater.Update();
            }
            else if (!active && clearDisplay) controls.DisplayUpdater.Update();

            long tick = Stopwatch.GetTimestamp();
            double elapsed = lastTimelineTick == 0 ? double.PositiveInfinity
                : (double)(tick - lastTimelineTick) / Stopwatch.Frequency;
            double expectedDelta = displayed?.State == PlaybackStatus.Playing && displayed is not null
                ? Math.Max(0, (snapshot.Timestamp - displayed.Timestamp).TotalSeconds) : 0;
            bool positionJump = displayed is not null &&
                Math.Abs(snapshot.PositionSeconds - displayed.PositionSeconds - expectedDelta) > 1.5;
            if (generationChanged || stateChanged || positionJump || elapsed >= 5
                || displayed?.DurationSeconds != snapshot.DurationSeconds || displayed?.CanSeek != snapshot.CanSeek)
            {
                UpdateTimeline(snapshot, active);
                lastTimelineTick = tick;
            }
            displayed = snapshot;
        }
        catch (Exception) { LastError = "system-media-update-failed"; }
    }

    internal static MediaPlaybackStatus MapStatus(PlaybackStatus state) => state switch
    {
        PlaybackStatus.Playing => MediaPlaybackStatus.Playing,
        PlaybackStatus.Paused => MediaPlaybackStatus.Paused,
        PlaybackStatus.Loading or PlaybackStatus.Buffering => MediaPlaybackStatus.Changing,
        _ => MediaPlaybackStatus.Stopped
    };

    private void UpdateTimeline(PlaybackSnapshot snapshot, bool active)
    {
        // The snapshot is the actual playback clock, never the drag preview.
        double duration = active ? SafeSeconds(snapshot.DurationSeconds) : 0;
        double position = active ? Math.Clamp(SafeSeconds(snapshot.PositionSeconds), 0, duration) : 0;
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = TimeSpan.FromSeconds(duration),
            Position = TimeSpan.FromSeconds(position),
            MinSeekTime = snapshot.CanSeek && active ? TimeSpan.Zero : TimeSpan.FromSeconds(position),
            MaxSeekTime = TimeSpan.FromSeconds(snapshot.CanSeek && active ? duration : position)
        });
    }

    private static double SafeSeconds(double value) => double.IsFinite(value)
        ? Math.Clamp(value, 0, TimeSpan.MaxValue.TotalSeconds - 1) : 0;

    private void OnButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        PlaybackCommandKind? kind = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => PlaybackCommandKind.Play,
            SystemMediaTransportControlsButton.Pause => PlaybackCommandKind.Pause,
            SystemMediaTransportControlsButton.Stop => PlaybackCommandKind.Stop,
            SystemMediaTransportControlsButton.Previous => PlaybackCommandKind.Previous,
            SystemMediaTransportControlsButton.Next => PlaybackCommandKind.Next,
            _ => null
        };
        if (kind.HasValue) QueueCommand(new(kind.Value, hub.Current.TrackGeneration));
    }

    private void OnPositionRequested(SystemMediaTransportControls sender,
        PlaybackPositionChangeRequestedEventArgs args) =>
        QueueCommand(new(PlaybackCommandKind.Seek, hub.Current.TrackGeneration,
            args.RequestedPlaybackPosition.TotalSeconds));

    private void QueueCommand(PlaybackCommand command)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        dispatcher.TryEnqueue(async () =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            var snapshot = hub.Current;
            if (!CanExecute(snapshot, command)) return;
            try
            {
                if (command.Kind == PlaybackCommandKind.Seek)
                    command = command with { PositionSeconds = Math.Clamp(command.PositionSeconds!.Value,
                        0, SafeSeconds(snapshot.DurationSeconds)) };
                await execute(command);
                if (Volatile.Read(ref disposed) == 0)
                {
                    // Immediately acknowledge the actual accepted position/state.
                    Apply(hub.Current);
                    UpdateTimeline(hub.Current, hub.Current.State != PlaybackStatus.Stopped);
                    lastTimelineTick = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception) { LastError = "system-media-command-failed"; }
        });
    }

    internal static bool CanExecute(PlaybackSnapshot snapshot, PlaybackCommand command)
    {
        if (!command.TrackGeneration.HasValue || command.TrackGeneration != snapshot.TrackGeneration
            || snapshot.State == PlaybackStatus.Stopped) return false;
        return command.Kind switch
        {
            PlaybackCommandKind.Play => snapshot.CanPlay,
            PlaybackCommandKind.Pause => snapshot.CanPause,
            PlaybackCommandKind.Stop => true,
            PlaybackCommandKind.Previous => snapshot.CanPrevious,
            PlaybackCommandKind.Next => snapshot.CanNext,
            PlaybackCommandKind.Seek => snapshot.CanSeek && command.PositionSeconds.HasValue
                && double.IsFinite(command.PositionSeconds.Value),
            _ => false
        };
    }

    /// <summary>
    /// Supplies bounded image bytes, never a NAS URL. Completion means conversion and
    /// dispatcher submission are complete; newer generations/requests supersede it.
    /// </summary>
    public async Task UpdateArtworkAsync(Guid generation, byte[] bytes)
    {
        if (Volatile.Read(ref disposed) != 0 || bytes.Length is 0 or > MaximumArtworkBytes
            || hub.Current.TrackGeneration != generation || hub.Current.State == PlaybackStatus.Stopped) return;
        long request = Interlocked.Increment(ref artworkRequest);
        var stream = new InMemoryRandomAccessStream();
        pendingArtwork[request] = stream;
        if (Volatile.Read(ref disposed) != 0)
        { ReleasePendingArtwork(request); return; }
        try
        {
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            if (!dispatcher.TryEnqueue(() => ApplyArtwork(request, generation)))
                ReleasePendingArtwork(request);
        }
        catch (Exception)
        {
            ReleasePendingArtwork(request);
            LastError = "system-media-artwork-failed";
        }
    }

    private void ApplyArtwork(long request, Guid generation)
    {
        if (!pendingArtwork.TryRemove(request, out var stream)) return;
        if (Volatile.Read(ref disposed) != 0 || request != Volatile.Read(ref artworkRequest)
            || hub.Current.TrackGeneration != generation || hub.Current.State == PlaybackStatus.Stopped)
        { stream.Dispose(); return; }
        try
        {
            Apply(hub.Current);
            controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            controls.DisplayUpdater.Update();
            var previous = artwork;
            artwork = stream;
            previous?.Dispose();
        }
        catch (Exception)
        {
            stream.Dispose();
            LastError = "system-media-artwork-failed";
        }
    }

    private void ReleasePendingArtwork(long request)
    {
        if (pendingArtwork.TryRemove(request, out var stream)) stream.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        hub.Changed -= OnSnapshot;
        controls.ButtonPressed -= OnButtonPressed;
        controls.PlaybackPositionChangeRequested -= OnPositionRequested;
        foreach (var request in pendingArtwork.Keys) ReleasePendingArtwork(request);
        try
        {
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.IsEnabled = false;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
        }
        catch (Exception) { LastError = "system-media-close-failed"; }
        artwork?.Dispose();
        artwork = null;
    }
}
