using System.Diagnostics.Metrics;

namespace Godswar.Server.Application.Reconciliation;

internal sealed class ReconciliationMetrics
{
    internal const string MeterName = "Godswar.Server.Reconciliation";

    private readonly Counter<long> _runs;
    private readonly Counter<long> _findings;
    private readonly Counter<long> _rows;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _repairAttempts;
    private readonly Counter<long> _repairRows;
    private readonly Histogram<double> _repairDuration;

    public ReconciliationMetrics(Meter? meter = null)
    {
        var effectiveMeter = meter ?? SharedMeter;
        _runs = effectiveMeter.CreateCounter<long>(
            "godswar_reconciliation_runs_total");
        _findings = effectiveMeter.CreateCounter<long>(
            "godswar_reconciliation_findings_total");
        _rows = effectiveMeter.CreateCounter<long>(
            "godswar_reconciliation_rows_scanned_total");
        _duration = effectiveMeter.CreateHistogram<double>(
            "godswar_reconciliation_run_duration_ms",
            unit: "ms");
        _repairAttempts = effectiveMeter.CreateCounter<long>(
            "godswar_reconciliation_repair_attempts_total");
        _repairRows = effectiveMeter.CreateCounter<long>(
            "godswar_reconciliation_repair_rows_total");
        _repairDuration = effectiveMeter.CreateHistogram<double>(
            "godswar_reconciliation_repair_duration_ms",
            unit: "ms");
    }

    private static Meter SharedMeter { get; } =
        new(MeterName, "1.0.0");

    public void Record(
        ReconciliationReport report) =>
        Record(report, report.Findings);

    public void Record(
        ReconciliationReport report,
        IReadOnlyList<ReconciliationCategoryCount> observedFindings)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(observedFindings);
        var mode = report.Mode == ReconciliationMode.ReportOnly
            ? "report_only"
            : "unknown";
        var outcome = report.Status switch
        {
            ReconciliationRunStatus.Completed => "completed",
            ReconciliationRunStatus.Truncated => "truncated",
            ReconciliationRunStatus.TimedOut => "timed_out",
            _ => "unknown"
        };
        _runs.Add(
            1,
            new KeyValuePair<string, object?>("mode", mode),
            new KeyValuePair<string, object?>("outcome", outcome));
        _duration.Record(
            report.DurationMilliseconds,
            new KeyValuePair<string, object?>("mode", mode),
            new KeyValuePair<string, object?>("outcome", outcome));
        _rows.Add(
            report.CharacterRowsScanned,
            new KeyValuePair<string, object?>(
                "scope",
                "characters"));
        _rows.Add(
            report.OutboxRowsScanned,
            new KeyValuePair<string, object?>("scope", "outbox"));

        foreach (var finding in observedFindings)
        {
            _findings.Add(
                finding.Count,
                new KeyValuePair<string, object?>(
                    "category",
                    finding.Category.ToProtocolValue()));
        }
    }

    public void RecordWorkerFailure(string outcome)
    {
        if (outcome is not (
            "database_unavailable" or "database_timeout"))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        _runs.Add(
            1,
            new KeyValuePair<string, object?>(
                "mode",
                "report_only"),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordRepairCompleted(
        int recoveredRows,
        bool limitReached,
        long durationMilliseconds = 0)
    {
        if (recoveredRows < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveredRows));
        }

        if (durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds));
        }

        var outcome = limitReached
            ? "limit_reached"
            : "completed";
        _repairAttempts.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                outcome));
        _repairRows.Add(
            recoveredRows,
            new KeyValuePair<string, object?>(
                "outcome",
                "recovered"));
        _repairDuration.Record(
            durationMilliseconds,
            new KeyValuePair<string, object?>(
                "outcome",
                outcome));
    }

    public void RecordRepairFailure(
        bool cancelled,
        long durationMilliseconds = 0)
    {
        if (durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds));
        }

        var outcome = cancelled ? "cancelled" : "failed";
        _repairAttempts.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                outcome));
        _repairDuration.Record(
            durationMilliseconds,
            new KeyValuePair<string, object?>(
                "outcome",
                outcome));
    }
}
