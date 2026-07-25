namespace Godswar.Server.ProtocolChecks;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int ScheduledTimerCount
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(static timer =>
                    !timer.IsDisposed &&
                    timer.DueTimestamp is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_sync)
        {
            _timestamp = checked(_timestamp + amount.Ticks);
            _utcNow += amount;

            foreach (var timer in _timers.ToArray())
            {
                if (!timer.TryFireLocked(_timestamp, out var callback))
                {
                    continue;
                }

                callbacks.Add(callback);
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    private void Change(
        ManualTimer timer,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));

        lock (_sync)
        {
            if (timer.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(ManualTimer));
            }

            timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : checked(_timestamp + dueTime.Ticks);
            timer.PeriodTicks = period == Timeout.InfiniteTimeSpan
                ? null
                : period.Ticks;
            _timers.Add(timer);
        }
    }

    private void Dispose(ManualTimer timer)
    {
        lock (_sync)
        {
            timer.IsDisposed = true;
            timer.DueTimestamp = null;
            _timers.Remove(timer);
        }
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        public long? DueTimestamp { get; set; }

        public bool IsDisposed { get; set; }

        public long? PeriodTicks { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Change(this, dueTime, period);
            return true;
        }

        public void Dispose()
        {
            owner.Dispose(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool TryFireLocked(
            long now,
            out (TimerCallback Callback, object? State) captured)
        {
            if (IsDisposed
                || DueTimestamp is not { } due
                || due > now)
            {
                captured = default;
                return false;
            }

            captured = (callback, state);
            DueTimestamp = PeriodTicks switch
            {
                null => null,
                0 => now,
                { } period => checked(due + period)
            };
            return true;
        }
    }
}
