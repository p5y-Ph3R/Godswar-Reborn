using System.Diagnostics;
using Godswar.Server.Application.Reconciliation;
using Npgsql;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal enum ReconciliationWorkerState : byte
{
    Disabled = 0,
    Starting = 1,
    Running = 2,
    Stopped = 3,
    Faulted = 4
}

internal readonly record struct ReconciliationWorkerSnapshot(
    bool Enabled,
    ReconciliationWorkerState State,
    bool FirstPassCompleted,
    TimeSpan HeartbeatAge,
    TimeSpan MaximumHealthyHeartbeatAge,
    ReconciliationRunStatus? LastRunStatus,
    long LastFindingCount,
    bool LastRunTruncated);

internal sealed class PostgresReconciliationWorker
{
    private readonly ReconciliationRunner _runner;
    private readonly ReconciliationOptions _options;
    private readonly ReconciliationMetrics _metrics;
    private readonly object _gate = new();
    private ReconciliationWorkerState _state;
    private bool _firstPassCompleted;
    private long _lastHeartbeatTimestamp;
    private ReconciliationRunStatus? _lastRunStatus;
    private long _lastFindingCount;
    private bool _lastRunTruncated;

    public PostgresReconciliationWorker(
        ReconciliationRunner runner,
        ReconciliationOptions options,
        ReconciliationMetrics? metrics = null)
    {
        _runner = runner ??
            throw new ArgumentNullException(nameof(runner));
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _metrics = metrics ?? new ReconciliationMetrics();
        _state = options.Enabled
            ? ReconciliationWorkerState.Starting
            : ReconciliationWorkerState.Disabled;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        SetState(ReconciliationWorkerState.Running);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var report =
                        await _runner.RunScheduledAsync(
                            cancellationToken);
                    Record(report);
                }
                catch (NpgsqlException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    _metrics.RecordWorkerFailure(
                        "database_unavailable");
                }
                catch (TimeoutException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    _metrics.RecordWorkerFailure(
                        "database_timeout");
                }

                await Task.Delay(
                    _options.PollInterval,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            SetState(ReconciliationWorkerState.Stopped);
        }
        catch
        {
            SetState(ReconciliationWorkerState.Faulted);
            throw;
        }
    }

    public ReconciliationWorkerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var age = _lastHeartbeatTimestamp == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(
                    _lastHeartbeatTimestamp);
            return new ReconciliationWorkerSnapshot(
                _options.Enabled,
                _state,
                _firstPassCompleted,
                age,
                _options.PollInterval +
                    _options.RunTimeout +
                    TimeSpan.FromSeconds(15),
                _lastRunStatus,
                _lastFindingCount,
                _lastRunTruncated);
        }
    }

    private void Record(ReconciliationReport report)
    {
        lock (_gate)
        {
            _lastRunStatus = report.Status;
            _lastFindingCount = report.Findings.Sum(
                finding => finding.Count);
            _lastRunTruncated = report.Truncated;
            if (report.Status is ReconciliationRunStatus.Completed
                    or ReconciliationRunStatus.Truncated)
            {
                _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
                if (report.Status ==
                    ReconciliationRunStatus.Completed)
                {
                    _firstPassCompleted = true;
                }
            }
        }
    }

    private void SetState(ReconciliationWorkerState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }
}
