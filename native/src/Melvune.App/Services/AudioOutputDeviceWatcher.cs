using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Media.Devices;

namespace Melvune.App.Services;

/// <summary>
/// Observes render-device changes without reading device identifiers or touching playback.
/// Changed runs on the native event thread; subscribers should enqueue their work.
/// </summary>
internal sealed class AudioOutputDeviceWatcher : IDisposable
{
    private readonly object gate = new();
    private Registration? active;
    private bool disposed;

    public event Action? Changed;

    public bool IsRunning
    {
        get
        {
            lock (gate)
                return !disposed && active is { } registration && IsWatching(registration.Watcher);
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (disposed) return;
            if (active is { } previous)
            {
                if (IsWatching(previous.Watcher)) return;
                active = null;
                Detach(previous);
            }

            var registration = new Registration(
                DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector()));
            registration.Added = (_, _) => DeviceChanged(registration);
            registration.Removed = (_, _) => DeviceChanged(registration);
            registration.Updated = (_, _) => DeviceChanged(registration);
            registration.Completed = (_, _) => EnumerationCompleted(registration);
            registration.Stopped = (_, _) => WatcherStopped(registration);
            registration.DefaultChanged = (_, _) => DeviceChanged(registration);
            active = registration;

            try
            {
                registration.Watcher.Added += registration.Added;
                registration.Watcher.Removed += registration.Removed;
                registration.Watcher.Updated += registration.Updated;
                registration.Watcher.EnumerationCompleted += registration.Completed;
                registration.Watcher.Stopped += registration.Stopped;
                MediaDevice.DefaultAudioRenderDeviceChanged += registration.DefaultChanged;
                registration.Watcher.Start();
            }
            catch
            {
                // Retire this generation before cleanup; queued callbacks cannot reach a retry.
                active = null;
                Detach(registration);
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            var registration = active;
            active = null;
            Changed = null;
            if (registration is not null) Detach(registration);
        }
    }

    private void EnumerationCompleted(Registration registration)
    {
        lock (gate)
        {
            if (disposed || active != registration || registration.InitialEnumerationComplete) return;
            registration.InitialEnumerationComplete = true;
            NotifyChanged(registration);
        }
    }

    private void DeviceChanged(Registration registration)
    {
        lock (gate)
        {
            if (disposed || active != registration || !registration.InitialEnumerationComplete) return;
            NotifyChanged(registration);
        }
    }

    private void WatcherStopped(Registration registration)
    {
        lock (gate)
        {
            if (disposed || active != registration) return;
            active = null;
            Detach(registration);
        }
    }

    private void NotifyChanged(Registration registration)
    {
        // Holding the gate prevents a notification from starting after Dispose returns.
        // A subscriber can reentrantly dispose, so recheck before each invocation.
        foreach (var callback in Changed?.GetInvocationList() ?? [])
        {
            if (disposed || active != registration) break;
            try { ((Action)callback)(); }
            catch { /* Subscriber failures must not escape a native event callback. */ }
        }
    }

    private static bool IsWatching(DeviceWatcher watcher)
    {
        try
        {
            return watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted;
        }
        catch { return false; }
    }

    private static void Detach(Registration registration)
    {
        // Stop is asynchronous. Unsubscribe first and reject stale callbacks by identity.
        // Attempt every removal even if a platform operation fails during shutdown.
        TryCleanup(() => registration.Watcher.Added -= registration.Added);
        TryCleanup(() => registration.Watcher.Removed -= registration.Removed);
        TryCleanup(() => registration.Watcher.Updated -= registration.Updated);
        TryCleanup(() => registration.Watcher.EnumerationCompleted -= registration.Completed);
        TryCleanup(() => registration.Watcher.Stopped -= registration.Stopped);
        TryCleanup(() => MediaDevice.DefaultAudioRenderDeviceChanged -= registration.DefaultChanged);
        TryCleanup(() =>
        {
            if (IsWatching(registration.Watcher)) registration.Watcher.Stop();
        });
    }

    private static void TryCleanup(Action cleanup)
    {
        try { cleanup(); }
        catch { /* Continue detaching the other subscriptions. */ }
    }

    private sealed class Registration(DeviceWatcher watcher)
    {
        public DeviceWatcher Watcher { get; } = watcher;
        public bool InitialEnumerationComplete { get; set; }
        public TypedEventHandler<DeviceWatcher, DeviceInformation> Added { get; set; } = null!;
        public TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> Removed { get; set; } = null!;
        public TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> Updated { get; set; } = null!;
        public TypedEventHandler<DeviceWatcher, object> Completed { get; set; } = null!;
        public TypedEventHandler<DeviceWatcher, object> Stopped { get; set; } = null!;
        public TypedEventHandler<object, DefaultAudioRenderDeviceChangedEventArgs> DefaultChanged { get; set; } = null!;
    }
}
