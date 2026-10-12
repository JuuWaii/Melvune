namespace FnMusic.Core;

/// <summary>A session-only countdown, measured by elapsed time rather than the wall clock.</summary>
public sealed class SleepTimer(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private long startedAt;
    private TimeSpan? duration;

    /// <summary>Remains active until cancellation or the due expiry is consumed by TryExpire.</summary>
    public bool IsActive
    {
        get { lock (gate) return duration.HasValue; }
    }

    /// <summary>Null when inactive, or zero when due but not yet consumed.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            lock (gate)
            {
                if (duration is not { } current) return null;
                var remaining = current - clock.GetElapsedTime(startedAt, clock.GetTimestamp());
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Replaces the previous countdown. Durations must be between one second and 24 hours.</summary>
    public void Start(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(duration), duration,
                "Sleep timer duration must be between one second and 24 hours.");

        lock (gate)
        {
            startedAt = clock.GetTimestamp();
            this.duration = duration;
        }
    }

    public void Cancel()
    {
        lock (gate) duration = null;
    }

    /// <summary>Consumes a due countdown, returning true only once for that countdown.</summary>
    public bool TryExpire()
    {
        lock (gate)
        {
            if (duration is not { } current || clock.GetElapsedTime(startedAt, clock.GetTimestamp()) < current)
                return false;
            duration = null;
            return true;
        }
    }
}
