using System.Diagnostics;
using System.Diagnostics.Metrics;
using Godswar.Server.Application.Coordination;

namespace Godswar.Server.Infrastructure.Redis;

internal enum RedisCoordinationOperationFamily : byte
{
    Health = 1,
    Worker = 2,
    Route = 3,
    Player = 4,
    Ticket = 5,
    Admission = 6
}

internal enum RedisCoordinationOutcome : byte
{
    Success = 1,
    Conflict = 2,
    NotFound = 3,
    Timeout = 4,
    Unavailable = 5,
    Overloaded = 6,
    CircuitOpen = 7,
    Cancelled = 8
}

internal readonly record struct RedisCoordinationExecutorSnapshot(
    bool IsReady,
    int MaximumConcurrency,
    int InFlight,
    long Accepted,
    long Conflicts,
    long Timeouts,
    long Unavailable,
    long OverloadRejections,
    long CircuitOpenRejections,
    DateTimeOffset LastSuccessAtUtc);

internal static class RedisCoordinationMetrics
{
    public const string MeterName =
        "Godswar.Server.Infrastructure.RedisCoordination";
    public const string LogicalResultInstrumentName =
        "godswar.coordination.logical_results";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Operations =
        Meter.CreateCounter<long>(
            "godswar.coordination.operations",
            "{operation}",
            "Bounded Redis coordination operation outcomes.");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(
            "godswar.coordination.duration",
            "ms",
            "Redis coordination operation duration.");
    private static readonly Counter<long> LogicalResults =
        Meter.CreateCounter<long>(
            LogicalResultInstrumentName,
            "{result}",
            "Bounded logical outcomes returned by Redis coordination.");

    public static long Start() => Stopwatch.GetTimestamp();

    public static void Record(
        RedisCoordinationOperationFamily family,
        RedisCoordinationOutcome outcome,
        long startedTimestamp)
    {
        var familyCode = FamilyCode(family);
        var outcomeCode = OutcomeCode(outcome);
        Operations.Add(
            1,
            new KeyValuePair<string, object?>(
                "coordination.family",
                familyCode),
            new KeyValuePair<string, object?>(
                "coordination.outcome",
                outcomeCode));
        Duration.Record(
            Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>(
                "coordination.family",
                familyCode),
            new KeyValuePair<string, object?>(
                "coordination.outcome",
                outcomeCode));
    }

    public static void RecordLogical(
        RedisCoordinationOperationFamily family,
        CoordinationOperationStatus status)
    {
        var resultCode = status switch
        {
            CoordinationOperationStatus.Applied => "applied",
            CoordinationOperationStatus.Current => "current",
            CoordinationOperationStatus.NotFound => "not_found",
            CoordinationOperationStatus.Conflict => "conflict",
            _ => null
        };
        if (resultCode is null)
        {
            return;
        }

        LogicalResults.Add(
            1,
            new KeyValuePair<string, object?>(
                "coordination.family",
                FamilyCode(family)),
            new KeyValuePair<string, object?>(
                "coordination.result",
                resultCode));
    }

    private static string FamilyCode(
        RedisCoordinationOperationFamily family) =>
        family switch
        {
            RedisCoordinationOperationFamily.Health => "health",
            RedisCoordinationOperationFamily.Worker => "worker",
            RedisCoordinationOperationFamily.Route => "route",
            RedisCoordinationOperationFamily.Player => "player",
            RedisCoordinationOperationFamily.Ticket => "ticket",
            RedisCoordinationOperationFamily.Admission => "admission",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    private static string OutcomeCode(
        RedisCoordinationOutcome outcome) =>
        outcome switch
        {
            RedisCoordinationOutcome.Success => "success",
            RedisCoordinationOutcome.Conflict => "conflict",
            RedisCoordinationOutcome.NotFound => "not_found",
            RedisCoordinationOutcome.Timeout => "timeout",
            RedisCoordinationOutcome.Unavailable => "unavailable",
            RedisCoordinationOutcome.Overloaded => "overloaded",
            RedisCoordinationOutcome.CircuitOpen => "circuit_open",
            RedisCoordinationOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}
