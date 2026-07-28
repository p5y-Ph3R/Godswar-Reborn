using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class SimulationLoopMetricsChecks
{
    private static readonly HashSet<string> AllowedTagNames =
    [
        "simulation.loop",
        "simulation.loop.stop_outcome"
    ];

    private static readonly HashSet<string> AllowedLoopValues =
    [
        "realtime_movement",
        "monster_world",
        "player_recovery",
        "experience_boost_reconciliation",
        "zodiac_energy_accrual"
    ];

    private static readonly HashSet<string> AllowedStopOutcomeValues =
    [
        "completed",
        "cancelled",
        "faulted"
    ];

    public static Task RunAsync()
    {
        CheckFiniteDimensionMappings();
        CheckScheduleAccounting();
        CheckAbortedIterationsAreNotCompleted();
        CheckMeasurementsUseOnlyBoundedDimensions();
        return Task.CompletedTask;
    }

    private static void CheckFiniteDimensionMappings()
    {
        var loopValues = Enum.GetValues<SimulationLoopKind>()
            .Select(static loop => loop.ToMetricTag())
            .ToHashSet(StringComparer.Ordinal);
        Check.True(
            loopValues.SetEquals(AllowedLoopValues),
            "simulation loop metric values are an exact finite set");

        var outcomeValues = Enum.GetValues<SimulationLoopStopOutcome>()
            .Select(static outcome => outcome.ToMetricTag())
            .ToHashSet(StringComparer.Ordinal);
        Check.True(
            outcomeValues.SetEquals(AllowedStopOutcomeValues),
            "simulation stop outcomes are an exact finite set");

        Check.Throws<ArgumentOutOfRangeException>(
            () => ((SimulationLoopKind)byte.MaxValue).ToMetricTag(),
            "unknown simulation loop cannot become a metric value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ((SimulationLoopStopOutcome)byte.MaxValue).ToMetricTag(),
            "unknown simulation stop outcome cannot become a metric value");
    }

    private static void CheckScheduleAccounting()
    {
        var early = SimulationLoopObservation.CalculateSchedule(
            expectedTimestamp: 100,
            actualTimestamp: 90,
            periodTimestampTicks: 50);
        Check.True(
            early is
            {
                LateTimestampTicks: 0,
                MissedDeadlines: 0,
                NextExpectedTimestamp: 150
            },
            "an early wake advances exactly one scheduled boundary");

        var late = SimulationLoopObservation.CalculateSchedule(
            expectedTimestamp: 100,
            actualTimestamp: 225,
            periodTimestampTicks: 50);
        Check.True(
            late is
            {
                LateTimestampTicks: 125,
                MissedDeadlines: 2,
                NextExpectedTimestamp: 250
            },
            "schedule accounting skips each fully missed boundary once");

        var saturated = SimulationLoopObservation.CalculateSchedule(
            expectedTimestamp: long.MaxValue - 5,
            actualTimestamp: long.MaxValue,
            periodTimestampTicks: 10);
        Check.Equal(
            long.MaxValue,
            saturated.NextExpectedTimestamp,
            "schedule accounting saturates instead of wrapping");
    }

    private static void CheckMeasurementsUseOnlyBoundedDimensions()
    {
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (instrument.Meter.Name == SimulationLoopMetrics.MeterName)
            {
                candidate.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    new CapturedMeasurement(
                        instrument.Name,
                        measurement,
                        tags.ToArray()));
            });
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    new CapturedMeasurement(
                        instrument.Name,
                        measurement,
                        tags.ToArray()));
            });
        listener.Start();

        SimulationLoopMetrics.RecordLoopStarted(
            SimulationLoopKind.RealtimeMovement);
        SimulationLoopMetrics.RecordTick(
            SimulationLoopKind.RealtimeMovement,
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(2),
            missedDeadlineCount: 1);
        listener.RecordObservableInstruments();
        SimulationLoopMetrics.RecordLoopStopped(
            SimulationLoopKind.RealtimeMovement,
            SimulationLoopStopOutcome.Faulted);
        listener.RecordObservableInstruments();

        var captured = measurements.ToArray();
        var expectedInstruments = new HashSet<string>(
        [
            "godswar.server.simulation.loops.active",
            "godswar.server.simulation.loops.started",
            "godswar.server.simulation.loops.stopped",
            "godswar.server.simulation.ticks",
            "godswar.server.simulation.tick.duration",
            "godswar.server.simulation.tick.schedule_drift",
            "godswar.server.simulation.tick.missed_deadlines",
            "godswar.server.simulation.heartbeat.age"
        ]);
        Check.True(
            expectedInstruments.All(name =>
                captured.Any(value =>
                    value.InstrumentName == name)),
            "simulation metrics emit every fixed-step lifecycle family");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName ==
                    "godswar.server.simulation.loops.active"
                && value.Value == 1
                && HasTag(
                    value,
                    "simulation.loop",
                    "realtime_movement")),
            "active-loop gauge observes the running loop");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName ==
                    "godswar.server.simulation.tick.missed_deadlines"
                && value.Value == 1),
            "missed-deadline counter preserves the supplied count");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName ==
                    "godswar.server.simulation.loops.stopped"
                && HasTag(
                    value,
                    "simulation.loop.stop_outcome",
                    "faulted")),
            "loop stop exposes a finite fault outcome");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName ==
                    "godswar.server.simulation.heartbeat.age"
                && value.Value >= 0),
            "active-loop heartbeat exposes non-negative age");

        foreach (var measurement in captured)
        {
            Check.True(
                measurement.Tags.All(static tag =>
                    AllowedTagNames.Contains(tag.Key)),
                $"{measurement.InstrumentName} uses only approved low-cardinality tag names");
            Check.True(
                measurement.Tags.All(static tag =>
                    tag.Value is string value
                    && IsAllowedTagValue(tag.Key, value)),
                $"{measurement.InstrumentName} uses only finite tag values");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => SimulationLoopMetrics.RecordTick(
                SimulationLoopKind.MonsterWorld,
                TimeSpan.FromMilliseconds(-1),
                TimeSpan.Zero),
            "negative tick duration is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => SimulationLoopMetrics.RecordTick(
                SimulationLoopKind.MonsterWorld,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(-1)),
            "negative schedule drift is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => SimulationLoopMetrics.RecordTick(
                SimulationLoopKind.MonsterWorld,
                TimeSpan.Zero,
                TimeSpan.Zero,
                missedDeadlineCount: -1),
            "negative missed-deadline count is rejected");
        Check.Throws<InvalidOperationException>(
            () => SimulationLoopMetrics.RecordLoopStopped(
                SimulationLoopKind.RealtimeMovement,
                SimulationLoopStopOutcome.Completed),
            "stopping an inactive loop cannot corrupt the active gauge");
    }

    private static void CheckAbortedIterationsAreNotCompleted()
    {
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (instrument.Meter.Name == SimulationLoopMetrics.MeterName)
            {
                candidate.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    new CapturedMeasurement(
                        instrument.Name,
                        measurement,
                        tags.ToArray()));
            });
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    new CapturedMeasurement(
                        instrument.Name,
                        measurement,
                        tags.ToArray()));
            });
        listener.Start();

        using (var completed = new SimulationLoopObservation(
                   SimulationLoopKind.MonsterWorld,
                   TimeSpan.FromMilliseconds(50)))
        {
            var tick = completed.BeginTick();
            tick.Complete();
        }

        using (var cancelled = new SimulationLoopObservation(
                   SimulationLoopKind.PlayerRecovery,
                   TimeSpan.FromMilliseconds(50)))
        {
            _ = cancelled.BeginTick();
            cancelled.MarkCancelled();
        }

        using (var faulted = new SimulationLoopObservation(
                   SimulationLoopKind.ZodiacEnergyAccrual,
                   TimeSpan.FromMilliseconds(50)))
        {
            _ = faulted.BeginTick();
            faulted.MarkFaulted();
        }

        var captured = measurements.ToArray();
        var tickMeasurements = captured
            .Where(static value =>
                value.InstrumentName ==
                    "godswar.server.simulation.ticks")
            .ToArray();
        Check.Equal(
            1,
            tickMeasurements.Length,
            "only a successfully completed iteration records a tick");
        Check.True(
            HasTag(
                tickMeasurements[0],
                "simulation.loop",
                "monster_world"),
            "the completed iteration owns the sole tick measurement");
        Check.True(
            !captured.Any(static value =>
                IsTickMeasurement(value.InstrumentName)
                && HasTag(
                    value,
                    "simulation.loop",
                    "player_recovery")),
            "a cancelled iteration records no tick measurements");
        Check.True(
            !captured.Any(static value =>
                IsTickMeasurement(value.InstrumentName)
                && HasTag(
                    value,
                    "simulation.loop",
                    "zodiac_energy_accrual")),
            "a faulted iteration records no tick measurements");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName ==
                    "godswar.server.simulation.loops.stopped"
                && HasTag(
                    value,
                    "simulation.loop.stop_outcome",
                    "cancelled")),
            "the cancelled loop still records its stop outcome");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName ==
                    "godswar.server.simulation.loops.stopped"
                && HasTag(
                    value,
                    "simulation.loop.stop_outcome",
                    "faulted")),
            "the faulted loop still records its stop outcome");
    }

    private static bool IsTickMeasurement(string instrumentName)
    {
        return instrumentName is
            "godswar.server.simulation.ticks"
            or "godswar.server.simulation.tick.duration"
            or "godswar.server.simulation.tick.schedule_drift"
            or "godswar.server.simulation.tick.missed_deadlines";
    }

    private static bool HasTag(
        CapturedMeasurement measurement,
        string name,
        string value)
    {
        return measurement.Tags.Any(
            tag => tag.Key == name && Equals(tag.Value, value));
    }

    private static bool IsAllowedTagValue(string name, string value)
    {
        return name switch
        {
            "simulation.loop" => AllowedLoopValues.Contains(value),
            "simulation.loop.stop_outcome" =>
                AllowedStopOutcomeValues.Contains(value),
            _ => false
        };
    }

    private readonly record struct CapturedMeasurement(
        string InstrumentName,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}
