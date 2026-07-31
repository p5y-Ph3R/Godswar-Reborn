using Godswar.Server.Application.Reconciliation;
using System.Diagnostics;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B19ReconciliationRunnerChecks
{
    internal const string CheckName =
        "B19 bounded keyset reconciliation runtime";

    public static async Task RunAsync()
    {
        await CheckKeysetAggregationAsync();
        await CheckTruncationBoundAsync();
        await CheckMultiRunRotationAsync();
        await CheckOutboxScopeRotationAsync();
        await CheckSchemaMismatchFailsClosedAsync();
        await CheckInvalidPageRejectionAsync();
        await CheckInterruptedContinuationRollbackAsync();
        await CheckCancellationAndRestartAsync();
        await CheckTimeoutAsync();
    }

    private static async Task CheckKeysetAggregationAsync()
    {
        var snapshot = new ScriptedSnapshot(
            characterPages:
            [
                new ReconciliationPage(
                    11,
                    2,
                    false,
                    [Count(
                        ReconciliationCategory.WalletBalanceMismatch,
                        1)]),
                new ReconciliationPage(
                    29,
                    1,
                    true,
                    [Count(
                        ReconciliationCategory.WalletBalanceMismatch,
                        2)])
            ],
            outboxPages:
            [
                new ReconciliationPage(
                    7,
                    2,
                    true,
                    [Count(
                        ReconciliationCategory.OutboxExpiredLease,
                        1)])
            ],
            manifest:
            [
                Count(
                    ReconciliationCategory.NpcContentCountMismatch,
                    1)
            ]);
        var reader = new ScriptedReader(snapshot);
        var runner = new ReconciliationRunner(
            reader,
            Options(batchSize: 2));

        var report = await runner.RunAsync();

        Check.Equal(
            (int)ReconciliationRunStatus.Completed,
            (int)report.Status,
            "a finite scan reaches completion");
        Check.True(
            !report.Truncated,
            "a complete scan is not marked truncated");
        Check.Equal(
            3,
            report.CharacterRowsScanned,
            "all character rows are counted");
        Check.Equal(
            2,
            report.OutboxRowsScanned,
            "all outbox rows are counted");
        Check.True(
            snapshot.CharacterRequests.SequenceEqual(
            [
                new PageRequest(0, 2),
                new PageRequest(11, 2)
            ]),
            "character pages resume from the preceding key");
        Check.True(
            snapshot.OutboxRequests.SequenceEqual(
            [
                new PageRequest(0, 2)
            ]),
            "outbox scan starts from its independent keyset");
        Check.Equal(
            3L,
            FindCount(
                report,
                ReconciliationCategory.WalletBalanceMismatch),
            "page findings are aggregated without identities");
        Check.Equal(
            1L,
            FindCount(
                report,
                ReconciliationCategory.OutboxExpiredLease),
            "outbox findings are retained");
        Check.True(
            report.Findings
                .Select(static finding => finding.Category)
                .SequenceEqual(report.Findings
                    .Select(static finding => finding.Category)
                    .Order()),
            "report categories have deterministic finite ordering");
        Check.Equal(
            TimeSpan.FromMilliseconds(250).Ticks,
            reader.CommandTimeout.Ticks,
            "the reader receives the configured command deadline");
        Check.True(
            snapshot.Disposed,
            "the consistent snapshot is disposed after the run");
    }

    private static async Task CheckTruncationBoundAsync()
    {
        var snapshot = new ScriptedSnapshot(
            characterPages:
            [
                new ReconciliationPage(10, 2, false, []),
                new ReconciliationPage(20, 1, false, [])
            ]);
        var options = Options(batchSize: 2);
        options.MaximumCharactersPerRun = 3;
        var report = await new ReconciliationRunner(
            new ScriptedReader(snapshot),
            options).RunAsync();

        Check.Equal(
            (int)ReconciliationRunStatus.Truncated,
            (int)report.Status,
            "an exhausted row budget is reported as truncated");
        Check.True(
            report.Truncated,
            "truncation is explicit in the report");
        Check.Equal(
            3,
            report.CharacterRowsScanned,
            "the character scan stops at its exact budget");
        Check.Equal(
            1,
            snapshot.OutboxRequests.Count,
            "outbox retains its independent bounded scan after character truncation");
        Check.True(
            snapshot.CharacterRequests.SequenceEqual(
            [
                new PageRequest(0, 2),
                new PageRequest(10, 1)
            ]),
            "the final page is reduced to the remaining budget");
    }

    private static async Task CheckInvalidPageRejectionAsync()
    {
        await ExpectAsync<InvalidDataException>(
            () => RunOnePageAsync(
                new ReconciliationPage(0, 0, false, [])),
            "a nonterminal empty page cannot spin forever");
        await ExpectAsync<InvalidDataException>(
            () => RunOnePageAsync(
                new ReconciliationPage(0, 3, true, [])),
            "a reader cannot exceed its requested page bound");
        await ExpectAsync<InvalidDataException>(
            () => RunOnePageAsync(
                new ReconciliationPage(
                    1,
                    1,
                    true,
                    [Count(
                        ReconciliationCategory.WalletBalanceMismatch,
                        -1)])),
            "negative finding counts are rejected");
    }

    private static async Task CheckSchemaMismatchFailsClosedAsync()
    {
        var snapshot = new ScriptedSnapshot(
            manifest:
            [
                Count(
                    ReconciliationCategory
                        .SchemaMigrationManifestMismatch,
                    1)
            ]);
        var report = await new ReconciliationRunner(
            new ScriptedReader(snapshot),
            Options()).RunScheduledAsync();

        Check.True(
            report.Status == ReconciliationRunStatus.Truncated &&
            report.Truncated,
            "schema mismatch is an incomplete sweep, never a clean pass");
        Check.Equal(
            0,
            snapshot.CharacterRequests.Count,
            "schema mismatch prevents character reads");
        Check.Equal(
            0,
            snapshot.OutboxRequests.Count,
            "schema mismatch prevents outbox reads");
    }

    private static async Task CheckMultiRunRotationAsync()
    {
        var reader = new RotatingReader([10, 20, 30, 40, 50]);
        var options = Options(batchSize: 2);
        options.MaximumCharactersPerRun = 2;
        var runner = new ReconciliationRunner(reader, options);

        var first = await runner.RunScheduledAsync();
        var second = await runner.RunScheduledAsync();
        var third = await runner.RunScheduledAsync();
        _ = await runner.RunScheduledAsync();

        Check.True(
            first.Truncated && second.Truncated && !third.Truncated,
            "bounded runs explicitly finish one rotating scan cycle");
        Check.True(
            reader.CharacterRequests.SequenceEqual(
            [
                new PageRequest(0, 2),
                new PageRequest(20, 2),
                new PageRequest(40, 2),
                new PageRequest(0, 2)
            ]),
            "successive runs resume beyond the prior bound and restart " +
            "only after reaching the end");
        Check.Equal(
            2L,
            FindCount(
                third,
                ReconciliationCategory.WalletBalanceMismatch),
            "completed sweep retains an early finding and also observes " +
            "a finding beyond the first run budget");
    }

    private static async Task CheckCancellationAndRestartAsync()
    {
        var blocking = new BlockingSnapshot();
        var reader = new ScriptedReader(blocking);
        using var cancellation = new CancellationTokenSource();
        var run = new ReconciliationRunner(
            reader,
            Options()).RunAsync(cancellation.Token);

        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(
            () => run,
            "caller cancellation is observable");
        Check.True(
            blocking.Disposed,
            "cancellation disposes the interrupted snapshot");

        var replacement = new ScriptedSnapshot(
            characterPages:
            [
                new ReconciliationPage(1, 1, true, [])
            ],
            outboxPages:
            [
                new ReconciliationPage(1, 1, true, [])
            ]);
        var replacementReader = new ScriptedReader(replacement);
        var report = await new ReconciliationRunner(
            replacementReader,
            Options()).RunAsync();
        Check.Equal(
            (int)ReconciliationRunStatus.Completed,
            (int)report.Status,
            "a new snapshot safely restarts after interruption");
        Check.Equal(
            0L,
            replacement.CharacterRequests[0].AfterKey,
            "a restarted snapshot does not reuse an invalid old cursor");
    }

    private static async Task CheckTimeoutAsync()
    {
        var blocking = new BlockingSnapshot();
        var options = Options();
        options.CommandTimeoutMilliseconds = 100;
        options.RunTimeoutMilliseconds = 100;
        var report = await new ReconciliationRunner(
            new ScriptedReader(blocking),
            options).RunAsync();

        Check.Equal(
            (int)ReconciliationRunStatus.TimedOut,
            (int)report.Status,
            "the run deadline produces a finite timed-out report");
        Check.True(
            blocking.Disposed,
            "timeout disposes the interrupted snapshot");
    }

    private static async Task RunOnePageAsync(
        ReconciliationPage page)
    {
        var snapshot = new ScriptedSnapshot(
            characterPages: [page]);
        _ = await new ReconciliationRunner(
            new ScriptedReader(snapshot),
            Options(batchSize: 2)).RunAsync();
    }

    private static ReconciliationOptions Options(int batchSize = 4) =>
        new()
        {
            BatchSize = batchSize,
            MaximumCharactersPerRun = 20,
            MaximumOutboxEventsPerRun = 20,
            PollIntervalMilliseconds = 10_000,
            CommandTimeoutMilliseconds = 250,
            RunTimeoutMilliseconds = 2_000
        };

    private static ReconciliationCategoryCount Count(
        ReconciliationCategory category,
        long count) =>
        new(category, count);

    private static long FindCount(
        ReconciliationReport report,
        ReconciliationCategory category) =>
        report.Findings.Single(finding =>
            finding.Category == category).Count;

    private static async Task ExpectAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected " +
            $"{typeof(TException).Name}.");
    }

    private readonly record struct PageRequest(long AfterKey, int Limit);

    private sealed class ScriptedReader(IReconciliationSnapshot snapshot) :
        IReconciliationReader
    {
        public TimeSpan CommandTimeout { get; private set; }

        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandTimeout = commandTimeout;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ScriptedSnapshot : IReconciliationSnapshot
    {
        private readonly Queue<ReconciliationPage> _characterPages;
        private readonly Queue<ReconciliationPage> _outboxPages;
        private readonly IReadOnlyList<ReconciliationCategoryCount> _manifest;

        public ScriptedSnapshot(
            IEnumerable<ReconciliationPage>? characterPages = null,
            IEnumerable<ReconciliationPage>? outboxPages = null,
            IReadOnlyList<ReconciliationCategoryCount>? manifest = null)
        {
            _characterPages = new Queue<ReconciliationPage>(
                characterPages ??
                [new ReconciliationPage(0, 0, true, [])]);
            _outboxPages = new Queue<ReconciliationPage>(
                outboxPages ??
                [new ReconciliationPage(0, 0, true, [])]);
            _manifest = manifest ?? [];
        }

        public List<PageRequest> CharacterRequests { get; } = [];

        public List<PageRequest> OutboxRequests { get; } = [];

        public bool Disposed { get; private set; }

        public Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CharacterRequests.Add(new(afterCharacterKey, limit));
            return Task.FromResult(_characterPages.Dequeue());
        }

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutboxRequests.Add(new(afterOutboxKey, limit));
            return Task.FromResult(_outboxPages.Dequeue());
        }

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationOutboxPositionPage(
                after,
                0,
                true,
                []));

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_manifest);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSnapshot : IReconciliationSnapshot
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public async Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            throw new UnreachableException();

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RotatingReader(long[] characterKeys) :
        IReconciliationReader
    {
        public List<PageRequest> CharacterRequests { get; } = [];

        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReconciliationSnapshot>(
                new RotatingSnapshot(
                    characterKeys,
                    CharacterRequests));
        }
    }

    private sealed class RotatingSnapshot(
        long[] characterKeys,
        List<PageRequest> requests) : IReconciliationSnapshot
    {
        public Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(new PageRequest(afterCharacterKey, limit));
            var page = characterKeys
                .Where(key => key > afterCharacterKey)
                .Take(limit)
                .ToArray();
            var reachedEnd =
                page.Length == 0 ||
                !characterKeys.Any(key => key > page[^1]);
            var findingCount = page.Count(key => key is 10 or 50);
            var findings = findingCount > 0
                ? new[]
                {
                    Count(
                        ReconciliationCategory.WalletBalanceMismatch,
                        findingCount)
                }
                : [];
            return Task.FromResult(new ReconciliationPage(
                page.Length == 0 ? afterCharacterKey : page[^1],
                page.Length,
                reachedEnd,
                findings));
        }

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(
                afterOutboxKey,
                0,
                true,
                []));

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationOutboxPositionPage(
                after,
                0,
                true,
                []));

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
