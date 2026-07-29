using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Godswar.Server.Application.World;

internal static class WorldContentMetrics
{
    public const string MeterName = "Godswar.Server.WorldContent";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> LoadOutcomes =
        Meter.CreateCounter<long>(
            "godswar_world_content_loads_total",
            description:
            "Completed world-content preload attempts by bounded outcome.");
    private static readonly Histogram<double> LoadDuration =
        Meter.CreateHistogram<double>(
            "godswar_world_content_load_duration_ms",
            unit: "ms",
            description: "World-content preload duration.");
    private static readonly Counter<long> Rejections =
        Meter.CreateCounter<long>(
            "godswar_world_content_rejections_total",
            description:
            "Missing, invalid, or revision-mismatched content rejections.");
    private static readonly Counter<long> FallbackAttempts =
        Meter.CreateCounter<long>(
            "godswar_world_content_fallback_attempts_total",
            description:
            "Attempts to use a legacy read-through content fallback.");

    public static void RecordLoad(
        string source,
        string outcome,
        TimeSpan duration)
    {
        var tags = new TagList
        {
            { "source", source },
            { "outcome", outcome }
        };
        LoadOutcomes.Add(1, tags);
        LoadDuration.Record(duration.TotalMilliseconds, tags);
    }

    public static void RecordRejection(
        string family,
        WorldContentFailureReason reason)
    {
        Rejections.Add(
            1,
            new TagList
            {
                { "family", family },
                { "reason", ReasonCode(reason) }
            });
    }

    public static void RecordFallbackAttempt(string source)
    {
        FallbackAttempts.Add(
            1,
            new TagList { { "source", source } });
    }

    private static string ReasonCode(WorldContentFailureReason reason) =>
        reason switch
        {
            WorldContentFailureReason.Missing => "missing",
            WorldContentFailureReason.Invalid => "invalid",
            WorldContentFailureReason.RevisionMismatch =>
                "revision_mismatch",
            _ => "unknown"
        };
}
