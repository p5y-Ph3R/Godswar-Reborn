using System.Diagnostics.Metrics;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Infrastructure.Redis;

namespace Godswar.Server.ProtocolChecks;

internal static class RedisCoordinationMetricsChecks
{
    public const string CheckName =
        "B17 Redis transport and logical-result metrics";

    public static Task RunAsync()
    {
        var captured = new List<(string Family, string Result)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name ==
                    RedisCoordinationMetrics.MeterName &&
                instrument.Name ==
                    RedisCoordinationMetrics.LogicalResultInstrumentName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, value, tags, _) =>
            {
                Check.Equal(1L, value, "logical-result counter increment");
                string? family = null;
                string? result = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "coordination.family")
                    {
                        family = tag.Value as string;
                    }
                    else if (tag.Key == "coordination.result")
                    {
                        result = tag.Value as string;
                    }
                }
                captured.Add((family ?? string.Empty, result ?? string.Empty));
            });
        listener.Start();

        RedisCoordinationMetrics.RecordLogical(
            RedisCoordinationOperationFamily.Worker,
            CoordinationOperationStatus.Applied);
        RedisCoordinationMetrics.RecordLogical(
            RedisCoordinationOperationFamily.Route,
            CoordinationOperationStatus.NotFound);
        RedisCoordinationMetrics.RecordLogical(
            RedisCoordinationOperationFamily.Player,
            CoordinationOperationStatus.Conflict);
        RedisCoordinationMetrics.RecordLogical(
            RedisCoordinationOperationFamily.Player,
            CoordinationOperationStatus.Unavailable);

        Check.Equal(
            3,
            captured.Count,
            "only bounded logical statuses produce result metrics");
        Check.True(
            captured.Contains(("worker", "applied")) &&
            captured.Contains(("route", "not_found")) &&
            captured.Contains(("player", "conflict")),
            "logical results retain finite family and result dimensions");
        Check.True(
            captured.All(static measurement =>
                measurement.Family is "worker" or "route" or "player" &&
                measurement.Result is
                    "applied" or "current" or "not_found" or "conflict"),
            "logical-result dimensions cannot contain identifiers");

        return Task.CompletedTask;
    }
}
