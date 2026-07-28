using System.Diagnostics;

namespace Godswar.Server.Game;

/// <summary>
/// Measures one single-owner periodic loop without allocating per tick.
/// Schedule drift and skipped boundaries use the monotonic Stopwatch clock;
/// gameplay time remains owned by the simulation.
/// </summary>
internal sealed class SimulationLoopObservation : IDisposable
{
    private readonly SimulationLoopKind _loop;
    private readonly long _periodTimestampTicks;
    private long _nextExpectedTimestamp;
    private SimulationLoopStopOutcome _outcome =
        SimulationLoopStopOutcome.Completed;
    private int _disposed;

    public SimulationLoopObservation(
        SimulationLoopKind loop,
        TimeSpan period)
    {
        if (!Enum.IsDefined(loop))
        {
            throw new ArgumentOutOfRangeException(nameof(loop));
        }
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        _loop = loop;
        _periodTimestampTicks = Math.Max(
            1,
            checked((long)Math.Ceiling(
                period.TotalSeconds * Stopwatch.Frequency)));
        _nextExpectedTimestamp = AddSaturated(
            Stopwatch.GetTimestamp(),
            _periodTimestampTicks);
        SimulationLoopMetrics.RecordLoopStarted(loop);
    }

    public SimulationTickObservation BeginTick()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var startedAt = Stopwatch.GetTimestamp();
        var expectedAt = _nextExpectedTimestamp;
        var schedule = CalculateSchedule(
            expectedAt,
            startedAt,
            _periodTimestampTicks);
        _nextExpectedTimestamp = schedule.NextExpectedTimestamp;

        return new SimulationTickObservation(
            this,
            startedAt,
            TimestampDuration(schedule.LateTimestampTicks),
            schedule.MissedDeadlines);
    }

    public void MarkCancelled()
    {
        _outcome = SimulationLoopStopOutcome.Cancelled;
    }

    public void MarkFaulted()
    {
        _outcome = SimulationLoopStopOutcome.Faulted;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        SimulationLoopMetrics.RecordLoopStopped(_loop, _outcome);
    }

    internal void CompleteTick(
        long startedAt,
        TimeSpan scheduleDrift,
        long missedDeadlines)
    {
        var finishedAt = Stopwatch.GetTimestamp();
        var duration = finishedAt >= startedAt
            ? Stopwatch.GetElapsedTime(startedAt, finishedAt)
            : TimeSpan.Zero;
        SimulationLoopMetrics.RecordTick(
            _loop,
            duration,
            scheduleDrift,
            missedDeadlines);
    }

    internal static SimulationScheduleObservation CalculateSchedule(
        long expectedTimestamp,
        long actualTimestamp,
        long periodTimestampTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedTimestamp);
        ArgumentOutOfRangeException.ThrowIfNegative(actualTimestamp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            periodTimestampTicks);

        var lateTimestampTicks = actualTimestamp > expectedTimestamp
            ? actualTimestamp - expectedTimestamp
            : 0;
        var missedDeadlines =
            lateTimestampTicks / periodTimestampTicks;
        var boundaries = missedDeadlines == long.MaxValue
            ? long.MaxValue
            : missedDeadlines + 1;
        var advance = boundaries >
            long.MaxValue / periodTimestampTicks
                ? long.MaxValue
                : boundaries * periodTimestampTicks;
        return new SimulationScheduleObservation(
            lateTimestampTicks,
            missedDeadlines,
            AddSaturated(expectedTimestamp, advance));
    }

    private static TimeSpan TimestampDuration(long timestampTicks)
    {
        if (timestampTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(
            (double)timestampTicks / Stopwatch.Frequency);
    }

    private static long AddSaturated(long value, long delta)
    {
        if (value >= long.MaxValue - delta)
        {
            return long.MaxValue;
        }

        return value + delta;
    }
}

internal readonly record struct SimulationScheduleObservation(
    long LateTimestampTicks,
    long MissedDeadlines,
    long NextExpectedTimestamp);

internal readonly struct SimulationTickObservation
{
    private readonly SimulationLoopObservation _owner;
    private readonly long _startedAt;
    private readonly TimeSpan _scheduleDrift;
    private readonly long _missedDeadlines;

    internal SimulationTickObservation(
        SimulationLoopObservation owner,
        long startedAt,
        TimeSpan scheduleDrift,
        long missedDeadlines)
    {
        _owner = owner;
        _startedAt = startedAt;
        _scheduleDrift = scheduleDrift;
        _missedDeadlines = missedDeadlines;
    }

    public void Complete()
    {
        _owner.CompleteTick(
            _startedAt,
            _scheduleDrift,
            _missedDeadlines);
    }
}
