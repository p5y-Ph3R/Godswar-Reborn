using Godswar.Server.Application.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static partial class B19ReconciliationRunnerChecks
{
    private static async Task CheckOutboxScopeRotationAsync()
    {
        var reader = new AlternatingOutboxReader(
            [10, 20, 30],
            [
                new ReconciliationOutboxPositionCursor("a", "a", "a"),
                new ReconciliationOutboxPositionCursor("b", "a", "a"),
                new ReconciliationOutboxPositionCursor("c", "a", "a")
            ]);
        var options = Options(batchSize: 1);
        options.MaximumOutboxEventsPerRun = 1;
        var runner = new ReconciliationRunner(reader, options);
        var reports = new List<ReconciliationReport>();
        for (var index = 0; index < 6; index++)
        {
            reports.Add(await runner.RunScheduledAsync());
        }

        Check.True(
            reports.Take(5).All(static report => report.Truncated) &&
            !reports[5].Truncated,
            "event and position pages jointly finish one bounded sweep");
        Check.True(
            reader.EventRequests.SequenceEqual(
            [
                new PageRequest(0, 1),
                new PageRequest(10, 1),
                new PageRequest(20, 1)
            ]),
            "outbox event pages retain their own continuation");
        Check.True(
            reader.PositionRequests.SequenceEqual(
            [
                ReconciliationOutboxPositionCursor.Start,
                new ReconciliationOutboxPositionCursor("a", "a", "a"),
                new ReconciliationOutboxPositionCursor("b", "a", "a")
            ]),
            "position pages alternate with events and cannot starve");
        Check.Equal(
            1L,
            FindCount(
                reports[5],
                ReconciliationCategory.OutboxConsumerPositionMismatch),
            "a position finding beyond earlier run budgets is observed");
    }

    private sealed class AlternatingOutboxReader(
        long[] eventKeys,
        ReconciliationOutboxPositionCursor[] positionKeys) :
        IReconciliationReader
    {
        public List<PageRequest> EventRequests { get; } = [];

        public List<ReconciliationOutboxPositionCursor> PositionRequests
        {
            get;
        } = [];

        public Task<IReconciliationSnapshot> OpenSnapshotAsync(
            TimeSpan commandTimeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReconciliationSnapshot>(
                new AlternatingOutboxSnapshot(
                    eventKeys,
                    positionKeys,
                    EventRequests,
                    PositionRequests));
        }
    }

    private sealed class AlternatingOutboxSnapshot(
        long[] eventKeys,
        ReconciliationOutboxPositionCursor[] positionKeys,
        List<PageRequest> eventRequests,
        List<ReconciliationOutboxPositionCursor> positionRequests) :
        IReconciliationSnapshot
    {
        public Task<ReconciliationPage> ReadCharacterPageAsync(
            long afterCharacterKey,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ReconciliationPage(
                afterCharacterKey,
                0,
                true,
                []));

        public Task<ReconciliationPage> ReadOutboxPageAsync(
            long afterOutboxKey,
            int limit,
            CancellationToken cancellationToken)
        {
            eventRequests.Add(new PageRequest(afterOutboxKey, limit));
            var page = eventKeys
                .Where(key => key > afterOutboxKey)
                .Take(limit)
                .ToArray();
            var reachedEnd =
                page.Length == 0 ||
                !eventKeys.Any(key => key > page[^1]);
            return Task.FromResult(new ReconciliationPage(
                page.Length == 0 ? afterOutboxKey : page[^1],
                page.Length,
                reachedEnd,
                []));
        }

        public Task<ReconciliationOutboxPositionPage>
            ReadOutboxPositionPageAsync(
                ReconciliationOutboxPositionCursor after,
                int limit,
                CancellationToken cancellationToken)
        {
            positionRequests.Add(after);
            var page = positionKeys
                .Where(key => ComparePosition(key, after) > 0)
                .Take(limit)
                .ToArray();
            var reachedEnd =
                page.Length == 0 ||
                !positionKeys.Any(key =>
                    ComparePosition(key, page[^1]) > 0);
            var findings = page.Any(key => key.ConsumerKey == "c")
                ? new[]
                {
                    Count(
                        ReconciliationCategory
                            .OutboxConsumerPositionMismatch,
                        1)
                }
                : [];
            return Task.FromResult(
                new ReconciliationOutboxPositionPage(
                    page.Length == 0 ? after : page[^1],
                    page.Length,
                    reachedEnd,
                    findings));
        }

        public Task<IReadOnlyList<ReconciliationCategoryCount>>
            ReadManifestAndContentAsync(
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReconciliationCategoryCount>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static int ComparePosition(
        ReconciliationOutboxPositionCursor left,
        ReconciliationOutboxPositionCursor right)
    {
        var consumer = string.CompareOrdinal(
            left.ConsumerKey,
            right.ConsumerKey);
        if (consumer != 0)
        {
            return consumer;
        }

        var aggregateType = string.CompareOrdinal(
            left.AggregateType,
            right.AggregateType);
        return aggregateType != 0
            ? aggregateType
            : string.CompareOrdinal(
                left.AggregateKey,
                right.AggregateKey);
    }
}
