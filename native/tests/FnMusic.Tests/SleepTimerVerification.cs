using FnMusic.Core;

internal static class SleepTimerVerification
{
    internal static Task RunAsync()
    {
        static void Check(bool value, string scenario)
        {
            if (!value) throw new InvalidOperationException("Sleep timer regression failed: " + scenario);
        }

        var clock = new FakeTimeProvider();
        var timer = new SleepTimer(clock);
        Check(!timer.IsActive && timer.Remaining is null && !timer.TryExpire(), "initial inactive state");

        timer.Start(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Check(timer.IsActive && timer.Remaining == TimeSpan.FromMilliseconds(1) && !timer.TryExpire(), "before deadline");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Check(timer.Remaining == TimeSpan.Zero && timer.IsActive, "due state before expiry consumption");
        Check(timer.TryExpire() && !timer.IsActive && timer.Remaining is null && !timer.TryExpire(), "exact deadline and single expiry");
        clock.Advance(TimeSpan.FromDays(2));
        Check(!timer.TryExpire(), "later polls do not repeat expiry");

        timer.Start(TimeSpan.FromMinutes(15));
        clock.Advance(TimeSpan.FromMinutes(10));
        timer.Cancel();
        timer.Cancel();
        clock.Advance(TimeSpan.FromMinutes(10));
        Check(!timer.IsActive && timer.Remaining is null && !timer.TryExpire(), "cancellation is idempotent and discards deadline");

        timer.Start(TimeSpan.FromMinutes(15));
        clock.Advance(TimeSpan.FromMinutes(10));
        timer.Start(TimeSpan.FromMinutes(30));
        clock.Advance(TimeSpan.FromMinutes(5));
        Check(!timer.TryExpire() && timer.Remaining == TimeSpan.FromMinutes(25), "replacement discards original deadline");
        clock.Advance(TimeSpan.FromMinutes(25));
        Check(timer.TryExpire() && !timer.TryExpire(), "replacement has its own deadline");

        timer.Start(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        timer.Start(TimeSpan.FromSeconds(10));
        Check(!timer.TryExpire() && timer.Remaining == TimeSpan.FromSeconds(10), "replacement discards unconsumed expired countdown");
        clock.SetUtcNow(clock.GetUtcNow().AddDays(-365));
        Check(timer.Remaining == TimeSpan.FromSeconds(10) && !timer.TryExpire(), "wall clock moving backwards");
        clock.SetUtcNow(clock.GetUtcNow().AddYears(10));
        Check(timer.Remaining == TimeSpan.FromSeconds(10) && !timer.TryExpire(), "wall clock moving forwards");
        clock.Advance(TimeSpan.FromSeconds(11));
        Check(timer.Remaining == TimeSpan.Zero && timer.TryExpire(), "late poll after wall clock changes");

        timer.Start(TimeSpan.FromHours(24));
        Check(timer.Remaining == TimeSpan.FromHours(24), "maximum duration accepted");
        foreach (var invalid in new[] { TimeSpan.MinValue, TimeSpan.FromSeconds(-1), TimeSpan.Zero,
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1), TimeSpan.FromHours(24) + TimeSpan.FromTicks(1), TimeSpan.MaxValue })
        {
            try
            {
                timer.Start(invalid);
                throw new InvalidOperationException("Invalid sleep timer duration was accepted.");
            }
            catch (ArgumentOutOfRangeException ex)
            {
                Check(ex.ParamName == "duration", "invalid duration identifies its parameter");
            }
            Check(timer.IsActive && timer.Remaining == TimeSpan.FromHours(24), "invalid duration preserves current countdown");
        }
        clock.Advance(TimeSpan.FromHours(24));
        Check(timer.TryExpire(), "maximum duration expires exactly");
        timer.Start(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        timer.Cancel();
        Check(!timer.TryExpire(), "cancel discards already due countdown");
        Check(!new SleepTimer(clock).IsActive, "new session does not restore previous countdown");
        return Task.CompletedTask;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

        // An intentionally different frequency catches implementations that assume timestamp ticks are TimeSpan ticks.
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan elapsed)
        {
            timestamp += checked((long)(elapsed.TotalSeconds * TimestampFrequency));
            utcNow += elapsed;
        }

        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }
}
