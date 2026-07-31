using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Godswar.Server.Application.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static class B19ReconciliationMetricsChecks
{
    internal const string CheckName =
        "B19 reconciliation metric cardinality";

    private static readonly HashSet<string> InstrumentNames =
    [
        "godswar_reconciliation_runs_total",
        "godswar_reconciliation_findings_total",
        "godswar_reconciliation_rows_scanned_total",
        "godswar_reconciliation_run_duration_ms",
        "godswar_reconciliation_repair_attempts_total",
        "godswar_reconciliation_repair_rows_total",
        "godswar_reconciliation_repair_duration_ms"
    ];

    public static Task RunAsync()
    {
        CheckFiniteDimensions();
        CheckUnknownCategoryFailsClosed();
        return Task.CompletedTask;
    }

    private static void CheckFiniteDimensions()
    {
        var meterName = $"B19.Reconciliation.Checks.{Guid.NewGuid():N}";
        using var capture = new MetricCapture(meterName);
        using var meter = new Meter(meterName, "1.0.0");
        var metrics = new ReconciliationMetrics(meter);
        var categories = Enum.GetValues<ReconciliationCategory>();
        var report = new ReconciliationReport(
            1,
            ReconciliationMode.ReportOnly,
            ReconciliationRunStatus.Truncated,
            DateTimeOffset.UtcNow,
            19,
            31,
            17,
            true,
            categories
                .Select(static category =>
                    new ReconciliationCategoryCount(category, 1))
                .ToArray());

        metrics.Record(report);
        metrics.RecordRepairCompleted(3, limitReached: false);
        metrics.RecordRepairCompleted(1, limitReached: true);
        metrics.RecordRepairFailure(cancelled: false);
        metrics.RecordRepairFailure(cancelled: true);
        var captured = capture.Measurements;

        Check.True(
            captured.Select(static item => item.InstrumentName)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(InstrumentNames),
            "reconciliation emits the exact reviewed instrument set");
        Check.Equal(
            categories.Length,
            captured.Count(item =>
                item.InstrumentName ==
                    "godswar_reconciliation_findings_total"),
            "one finding measurement is emitted per finite category");
        Check.Equal(
            2,
            captured.Count(item =>
                item.InstrumentName ==
                    "godswar_reconciliation_rows_scanned_total"),
            "row counts use two fixed scopes");
        Check.Equal(
            4,
            captured.Count(item =>
                item.InstrumentName ==
                    "godswar_reconciliation_repair_attempts_total"),
            "repair attempts use four finite outcomes");
        Check.Equal(
            2,
            captured.Count(item =>
                item.InstrumentName ==
                    "godswar_reconciliation_repair_rows_total"),
            "successful repairs record bounded recovered rows");
        Check.Equal(
            4,
            captured.Count(item =>
                item.InstrumentName ==
                    "godswar_reconciliation_repair_duration_ms"),
            "every repair attempt records one duration");

        var allowedCategories = categories
            .Select(static category => category.ToProtocolValue())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var measurement in captured)
        {
            var tags = measurement.Tags.ToDictionary(
                static tag => tag.Key,
                static tag => tag.Value?.ToString() ?? string.Empty,
                StringComparer.Ordinal);
            switch (measurement.InstrumentName)
            {
                case "godswar_reconciliation_runs_total":
                case "godswar_reconciliation_run_duration_ms":
                    Check.True(
                        tags.Count == 2 &&
                        tags.GetValueOrDefault("mode") == "report_only" &&
                        tags.GetValueOrDefault("outcome") == "truncated",
                        "run metrics use only finite mode and outcome");
                    break;
                case "godswar_reconciliation_rows_scanned_total":
                    Check.True(
                        tags.Count == 1 &&
                        tags.TryGetValue("scope", out var scope) &&
                        scope is "characters" or "outbox",
                        "row metrics use only a finite scan scope");
                    break;
                case "godswar_reconciliation_findings_total":
                    Check.True(
                        tags.Count == 1 &&
                        tags.TryGetValue("category", out var category) &&
                        allowedCategories.Contains(category),
                        "finding metrics use only reviewed category values");
                    break;
                case "godswar_reconciliation_repair_attempts_total":
                case "godswar_reconciliation_repair_duration_ms":
                    Check.True(
                        tags.Count == 1 &&
                        tags.TryGetValue("outcome", out var attempt) &&
                        attempt is "completed" or "limit_reached" or
                            "cancelled" or "failed",
                        "repair attempts use only finite outcomes");
                    break;
                case "godswar_reconciliation_repair_rows_total":
                    Check.True(
                        tags.Count == 1 &&
                        tags.GetValueOrDefault("outcome") == "recovered",
                        "repair rows use one finite outcome");
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unexpected reconciliation metric.");
            }

            Check.True(
                tags.Keys.All(static key =>
                    key is "mode" or "outcome" or "scope" or "category"),
                "metrics contain no identity or attacker-controlled tag");
            Check.True(
                tags.Values.All(static value =>
                    value.Length <= 64 &&
                    !Guid.TryParse(value, out _) &&
                    !value.Contains("127.", StringComparison.Ordinal)),
                "metrics contain only bounded non-identity finite values");
        }
    }

    private static void CheckUnknownCategoryFailsClosed()
    {
        using var meter =
            new Meter($"B19.Reconciliation.Invalid.{Guid.NewGuid():N}");
        var metrics = new ReconciliationMetrics(meter);
        var report = new ReconciliationReport(
            1,
            ReconciliationMode.ReportOnly,
            ReconciliationRunStatus.Completed,
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            false,
            [
                new ReconciliationCategoryCount(
                    (ReconciliationCategory)byte.MaxValue,
                    1)
            ]);
        Check.Throws<ArgumentOutOfRangeException>(
            () => metrics.Record(report),
            "unknown finding categories cannot create metric series");
        Check.Throws<ArgumentOutOfRangeException>(
            () => metrics.RecordRepairCompleted(
                -1,
                limitReached: false),
            "negative repair row counts are rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => metrics.RecordRepairFailure(
                cancelled: false,
                durationMilliseconds: -1),
            "negative repair durations are rejected");
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly ConcurrentQueue<CapturedMeasurement>
            _measurements = [];
        private readonly MeterListener _listener = new();

        public MetricCapture(string meterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                    _measurements.Enqueue(new(
                        instrument.Name,
                        measurement,
                        tags.ToArray())));
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) =>
                    _measurements.Enqueue(new(
                        instrument.Name,
                        measurement,
                        tags.ToArray())));
            _listener.Start();
        }

        public IReadOnlyCollection<CapturedMeasurement> Measurements =>
            _measurements.ToArray();

        public void Dispose() => _listener.Dispose();
    }

    private readonly record struct CapturedMeasurement(
        string InstrumentName,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}
