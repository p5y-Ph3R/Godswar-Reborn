using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Godswar.Server.Game;

internal enum SimulationLoopKind : byte
{
    RealtimeMovement = 1,
    MonsterWorld = 2,
    PlayerRecovery = 3,
    ExperienceBoostReconciliation = 4,
    ZodiacEnergyAccrual = 5
}

internal enum SimulationLoopStopOutcome : byte
{
    Completed = 1,
    Cancelled = 2,
    Faulted = 3
}

internal static class SimulationLoopMetricTags
{
    public static string ToMetricTag(this SimulationLoopKind loop) =>
        loop switch
        {
            SimulationLoopKind.RealtimeMovement => "realtime_movement",
            SimulationLoopKind.MonsterWorld => "monster_world",
            SimulationLoopKind.PlayerRecovery => "player_recovery",
            SimulationLoopKind.ExperienceBoostReconciliation =>
                "experience_boost_reconciliation",
            SimulationLoopKind.ZodiacEnergyAccrual =>
                "zodiac_energy_accrual",
            _ => throw new ArgumentOutOfRangeException(nameof(loop))
        };

    public static string ToMetricTag(
        this SimulationLoopStopOutcome outcome) =>
        outcome switch
        {
            SimulationLoopStopOutcome.Completed => "completed",
            SimulationLoopStopOutcome.Cancelled => "cancelled",
            SimulationLoopStopOutcome.Faulted => "faulted",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}

internal static class SimulationLoopMetrics
{
    public const string MeterName = "Godswar.Server.Simulation";

    private const string LoopTagName = "simulation.loop";
    private const string StopOutcomeTagName =
        "simulation.loop.stop_outcome";
    private const int LoopKindCount = 5;

    private static readonly long[] ActiveLoopCounts =
        new long[LoopKindCount];
    private static readonly long[] LastHeartbeatTimestamps =
        new long[LoopKindCount];
    private static readonly Meter Meter = new(MeterName);

    private static readonly ObservableGauge<long> ActiveLoops =
        Meter.CreateObservableGauge(
            "godswar.server.simulation.loops.active",
            ObserveActiveLoops,
            "{loop}",
            "Currently active fixed-step loops.");

    private static readonly Counter<long> StartedLoops =
        Meter.CreateCounter<long>(
            "godswar.server.simulation.loops.started",
            "{loop}",
            "Fixed-step loops started.");

    private static readonly Counter<long> StoppedLoops =
        Meter.CreateCounter<long>(
            "godswar.server.simulation.loops.stopped",
            "{loop}",
            "Fixed-step loops stopped by finite outcome.");

    private static readonly Counter<long> Ticks =
        Meter.CreateCounter<long>(
            "godswar.server.simulation.ticks",
            "{tick}",
            "Completed fixed-step simulation ticks.");

    private static readonly Histogram<double> TickDuration =
        Meter.CreateHistogram<double>(
            "godswar.server.simulation.tick.duration",
            "ms",
            "Elapsed time spent processing a simulation tick.");

    private static readonly Histogram<double> ScheduleDrift =
        Meter.CreateHistogram<double>(
            "godswar.server.simulation.tick.schedule_drift",
            "ms",
            "Non-negative lateness from the expected tick boundary.");

    private static readonly Counter<long> MissedDeadlines =
        Meter.CreateCounter<long>(
            "godswar.server.simulation.tick.missed_deadlines",
            "{deadline}",
            "Expected tick boundaries missed before a completed tick.");

    private static readonly ObservableGauge<double> HeartbeatAge =
        Meter.CreateObservableGauge(
            "godswar.server.simulation.heartbeat.age",
            ObserveHeartbeatAge,
            "ms",
            "Age of the latest heartbeat for each active loop kind.");

    public static void RecordLoopStarted(SimulationLoopKind loop)
    {
        var index = GetIndex(loop);
        var loopTag = LoopTag(loop);

        Interlocked.Increment(ref ActiveLoopCounts[index]);
        Volatile.Write(
            ref LastHeartbeatTimestamps[index],
            Stopwatch.GetTimestamp());
        StartedLoops.Add(1, loopTag);
    }

    public static void RecordTick(
        SimulationLoopKind loop,
        TimeSpan duration,
        TimeSpan scheduleDrift,
        long missedDeadlineCount = 0)
    {
        var index = GetIndex(loop);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            duration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            scheduleDrift,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(
            missedDeadlineCount);

        var loopTag = LoopTag(loop);
        Ticks.Add(1, loopTag);
        TickDuration.Record(duration.TotalMilliseconds, loopTag);
        ScheduleDrift.Record(scheduleDrift.TotalMilliseconds, loopTag);
        if (missedDeadlineCount > 0)
        {
            MissedDeadlines.Add(missedDeadlineCount, loopTag);
        }

        Volatile.Write(
            ref LastHeartbeatTimestamps[index],
            Stopwatch.GetTimestamp());
    }

    public static void RecordLoopStopped(
        SimulationLoopKind loop,
        SimulationLoopStopOutcome outcome)
    {
        var index = GetIndex(loop);
        var loopTag = LoopTag(loop);
        var outcomeTag = StopOutcomeTag(outcome);

        DecrementActiveLoop(index, loop);
        StoppedLoops.Add(1, loopTag, outcomeTag);
    }

    private static IEnumerable<Measurement<long>> ObserveActiveLoops()
    {
        foreach (var loop in Enum.GetValues<SimulationLoopKind>())
        {
            yield return new Measurement<long>(
                Volatile.Read(ref ActiveLoopCounts[GetIndex(loop)]),
                LoopTag(loop));
        }
    }

    private static IEnumerable<Measurement<double>> ObserveHeartbeatAge()
    {
        foreach (var loop in Enum.GetValues<SimulationLoopKind>())
        {
            var index = GetIndex(loop);
            if (Volatile.Read(ref ActiveLoopCounts[index]) <= 0)
            {
                continue;
            }

            var heartbeat = Volatile.Read(
                ref LastHeartbeatTimestamps[index]);
            if (heartbeat <= 0)
            {
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var age = heartbeat >= now
                ? 0d
                : Stopwatch.GetElapsedTime(
                    heartbeat,
                    now).TotalMilliseconds;
            yield return new Measurement<double>(
                age,
                LoopTag(loop));
        }
    }

    private static void DecrementActiveLoop(
        int index,
        SimulationLoopKind loop)
    {
        while (true)
        {
            var current = Volatile.Read(ref ActiveLoopCounts[index]);
            if (current <= 0)
            {
                throw new InvalidOperationException(
                    $"Simulation loop '{loop.ToMetricTag()}' is not active.");
            }

            if (Interlocked.CompareExchange(
                    ref ActiveLoopCounts[index],
                    current - 1,
                    current) == current)
            {
                return;
            }
        }
    }

    private static int GetIndex(SimulationLoopKind loop) =>
        loop switch
        {
            SimulationLoopKind.RealtimeMovement => 0,
            SimulationLoopKind.MonsterWorld => 1,
            SimulationLoopKind.PlayerRecovery => 2,
            SimulationLoopKind.ExperienceBoostReconciliation => 3,
            SimulationLoopKind.ZodiacEnergyAccrual => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(loop))
        };

    private static KeyValuePair<string, object?> LoopTag(
        SimulationLoopKind loop) =>
        new(LoopTagName, loop.ToMetricTag());

    private static KeyValuePair<string, object?> StopOutcomeTag(
        SimulationLoopStopOutcome outcome) =>
        new(StopOutcomeTagName, outcome.ToMetricTag());
}
