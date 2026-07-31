using System.Diagnostics;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server.Infrastructure.Reconciliation;

internal sealed class PostgresExpiredOutboxLeaseRepairer :
    IReconciliationRepairer
{
    private readonly PostgresOutboxDispatcher _dispatcher;
    private readonly ReconciliationMetrics _metrics;

    public PostgresExpiredOutboxLeaseRepairer(
        PostgresOutboxDispatcher dispatcher,
        ReconciliationMetrics? metrics = null)
    {
        _dispatcher = dispatcher ??
            throw new ArgumentNullException(nameof(dispatcher));
        _metrics = metrics ?? new ReconciliationMetrics();
    }

    public async Task<ExpiredOutboxLeaseRepairResult>
        RecoverExpiredOutboxLeasesAsync(
            int maximumRepairs,
            CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int repaired;
        try
        {
            repaired = await _dispatcher.RecoverExpiredLeasesAsync(
                maximumRepairs,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _metrics.RecordRepairFailure(
                cancelled: true,
                Math.Max(0, stopwatch.ElapsedMilliseconds));
            throw;
        }
        catch
        {
            _metrics.RecordRepairFailure(
                cancelled: false,
                Math.Max(0, stopwatch.ElapsedMilliseconds));
            throw;
        }

        var limitReached = repaired == maximumRepairs;
        _metrics.RecordRepairCompleted(
            repaired,
            limitReached,
            Math.Max(0, stopwatch.ElapsedMilliseconds));
        return new ExpiredOutboxLeaseRepairResult(
            repaired,
            limitReached);
    }
}
