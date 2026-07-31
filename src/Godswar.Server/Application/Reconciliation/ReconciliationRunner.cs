using System.Diagnostics;

namespace Godswar.Server.Application.Reconciliation;

internal sealed partial class ReconciliationRunner
{
    private readonly IReconciliationReader _reader;
    private readonly ReconciliationOptions _options;
    private readonly ReconciliationMetrics _metrics;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private ReconciliationScanState _continuation =
        ReconciliationScanState.Start;

    public ReconciliationRunner(
        IReconciliationReader reader,
        ReconciliationOptions options,
        ReconciliationMetrics? metrics = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _metrics = metrics ?? new ReconciliationMetrics();
    }

    public async Task<ReconciliationReport> RunAsync(
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            return await RunExclusiveAsync(cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    public Task<ReconciliationReport> RunScheduledAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(cancellationToken);

    private async Task<ReconciliationReport> RunExclusiveAsync(
        CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var manifestCounts =
            new Dictionary<ReconciliationCategory, long>();
        var scanCounts =
            new Dictionary<ReconciliationCategory, long>();
        var characterRows = 0;
        var outboxRows = 0;
        var workingState = _continuation;
        var scanPerformed = false;
        var truncated = false;
        var timedOut = false;

        using var timeout =
            new CancellationTokenSource(_options.RunTimeout);
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);

        try
        {
            await using var snapshot =
                await _reader.OpenSnapshotAsync(
                    _options.CommandTimeout,
                    linked.Token);
            var manifest = await snapshot
                .ReadManifestAndContentAsync(linked.Token);
            Add(manifestCounts, manifest);
            var schemaMismatch = manifest.Any(finding =>
                finding.Category ==
                    ReconciliationCategory
                        .SchemaMigrationManifestMismatch &&
                finding.Count > 0);
            if (!schemaMismatch)
            {
                scanPerformed = true;
                var characterResult =
                    await ScanCharactersAsync(
                        snapshot,
                        workingState,
                        scanCounts,
                        linked.Token);
                characterRows = characterResult.RowsScanned;
                workingState = characterResult.State;

                var outboxResult =
                    await ScanOutboxAsync(
                        snapshot,
                        workingState,
                        scanCounts,
                        linked.Token);
                outboxRows = outboxResult.RowsScanned;
                workingState = outboxResult.State;
                truncated = !workingState.SweepCompleted;
            }
            else
            {
                // The authoritative schema is not safe to inspect yet.
                // Preserve the last committed cursors and never claim that
                // the logical character/outbox sweep completed.
                truncated = true;
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested &&
                  timeout.IsCancellationRequested)
        {
            timedOut = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var status = timedOut
            ? ReconciliationRunStatus.TimedOut
            : truncated
                ? ReconciliationRunStatus.Truncated
                : ReconciliationRunStatus.Completed;
        var accumulatedCounts =
            ToDictionary(_continuation.AccumulatedFindings);
        Add(accumulatedCounts, ToCounts(scanCounts));
        var reportCounts =
            new Dictionary<ReconciliationCategory, long>(
                accumulatedCounts);
        Add(reportCounts, ToCounts(manifestCounts));
        var observedCounts =
            new Dictionary<ReconciliationCategory, long>(
                scanCounts);
        Add(observedCounts, ToCounts(manifestCounts));
        var report = new ReconciliationReport(
            SchemaVersion: 1,
            ReconciliationMode.ReportOnly,
            status,
            startedAtUtc,
            Math.Max(0, stopwatch.ElapsedMilliseconds),
            characterRows,
            outboxRows,
            truncated,
            ToCounts(reportCounts));
        _metrics.Record(report, ToCounts(observedCounts));
        if (!timedOut && scanPerformed)
        {
            _continuation =
                status == ReconciliationRunStatus.Completed
                    ? ReconciliationScanState.Start
                    : workingState with
                    {
                        AccumulatedFindings =
                            ToCounts(accumulatedCounts)
                    };
        }

        return report;
    }

    private static void Add(
        IDictionary<ReconciliationCategory, long> target,
        IEnumerable<ReconciliationCategoryCount> additions)
    {
        foreach (var addition in additions)
        {
            if (!Enum.IsDefined(addition.Category) ||
                addition.Count < 0)
            {
                throw new InvalidDataException(
                    "A reconciliation count cannot be negative.");
            }

            target.TryGetValue(addition.Category, out var current);
            target[addition.Category] =
                checked(current + addition.Count);
        }
    }

    private static Dictionary<ReconciliationCategory, long>
        ToDictionary(
            IEnumerable<ReconciliationCategoryCount> counts)
    {
        var result = new Dictionary<ReconciliationCategory, long>();
        Add(result, counts);
        return result;
    }

    private static IReadOnlyList<ReconciliationCategoryCount> ToCounts(
        IDictionary<ReconciliationCategory, long> counts) =>
        counts
            .Where(pair => pair.Value > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new ReconciliationCategoryCount(
                pair.Key,
                pair.Value))
            .ToArray();
}
