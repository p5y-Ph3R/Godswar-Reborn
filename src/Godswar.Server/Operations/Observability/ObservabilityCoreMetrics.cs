using System.Diagnostics.Metrics;

namespace Godswar.Server.Operations.Observability;

internal static class ObservabilityCoreMetrics
{
    public const string MeterName =
        "Godswar.Server.Operations.Observability";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> LogEvents =
        Meter.CreateCounter<long>(
            "godswar.server.logs.events",
            "{event}",
            "Bounded structured-log outcomes.");
    private static readonly Counter<long> TraceSpans =
        Meter.CreateCounter<long>(
            "godswar.server.traces.spans",
            "{span}",
            "Bounded in-process trace-buffer outcomes.");

    public static void RecordLog(
        OperationalLogEvent eventId,
        string outcome)
    {
        LogEvents.Add(
            1,
            new KeyValuePair<string, object?>(
                "log.event",
                StructuredLogCodes.Event(eventId)),
            new KeyValuePair<string, object?>(
                "log.outcome",
                SafeTelemetryCode.Require(outcome, nameof(outcome))));
    }

    public static void RecordTrace(string outcome)
    {
        TraceSpans.Add(
            1,
            new KeyValuePair<string, object?>(
                "trace.outcome",
                SafeTelemetryCode.Require(outcome, nameof(outcome))));
    }
}

internal static class StructuredLogCodes
{
    public static string Event(OperationalLogEvent eventId) =>
        eventId switch
        {
            OperationalLogEvent.ServerLifecycle =>
                "server_lifecycle",
            OperationalLogEvent.CriticalTaskState =>
                "critical_task_state",
            OperationalLogEvent.ReadinessChanged =>
                "readiness_changed",
            OperationalLogEvent.ManagementRequest =>
                "management_request",
            OperationalLogEvent.TelemetryExporter =>
                "telemetry_exporter",
            OperationalLogEvent.LegacyDiagnosticSuppressed =>
                "legacy_diagnostic_suppressed",
            _ => throw new ArgumentOutOfRangeException(nameof(eventId))
        };

    public static string Level(OperationalLogLevel level) =>
        level switch
        {
            OperationalLogLevel.Trace => "trace",
            OperationalLogLevel.Debug => "debug",
            OperationalLogLevel.Information => "information",
            OperationalLogLevel.Warning => "warning",
            OperationalLogLevel.Error => "error",
            OperationalLogLevel.Critical => "critical",
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };

    public static string Field(OperationalLogField field) =>
        field switch
        {
            OperationalLogField.Component => "component",
            OperationalLogField.Outcome => "outcome",
            OperationalLogField.Reason => "reason",
            OperationalLogField.State => "state",
            OperationalLogField.Source => "source",
            OperationalLogField.Category => "category",
            OperationalLogField.Count => "count",
            OperationalLogField.DurationMilliseconds => "duration_ms",
            OperationalLogField.Truncated => "truncated",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
}
