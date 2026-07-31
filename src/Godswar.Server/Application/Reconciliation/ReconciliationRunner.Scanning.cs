namespace Godswar.Server.Application.Reconciliation;

internal sealed partial class ReconciliationRunner
{
    private async Task<ScopeScanResult> ScanCharactersAsync(
        IReconciliationSnapshot snapshot,
        ReconciliationScanState state,
        IDictionary<ReconciliationCategory, long> counts,
        CancellationToken cancellationToken)
    {
        if (state.CharactersComplete)
        {
            return new ScopeScanResult(state, 0);
        }

        var cursor = state.CharacterCursor;
        var rows = 0;
        var remaining = _options.MaximumCharactersPerRun;
        while (remaining > 0)
        {
            var limit = Math.Min(_options.BatchSize, remaining);
            var page = await snapshot.ReadCharacterPageAsync(
                cursor,
                limit,
                cancellationToken);
            ValidatePage(cursor, page, limit);
            rows += page.RowsScanned;
            remaining -= page.RowsScanned;
            Add(counts, page.Findings);
            if (page.ReachedEnd)
            {
                return new ScopeScanResult(
                    state with
                    {
                        CharacterCursor = 0,
                        CharactersComplete = true
                    },
                    rows);
            }

            cursor = page.NextKey;
        }

        return new ScopeScanResult(
            state with { CharacterCursor = cursor },
            rows);
    }

    private async Task<ScopeScanResult> ScanOutboxAsync(
        IReconciliationSnapshot snapshot,
        ReconciliationScanState state,
        IDictionary<ReconciliationCategory, long> counts,
        CancellationToken cancellationToken)
    {
        if (state.OutboxComplete)
        {
            return new ScopeScanResult(state, 0);
        }

        var working = state;
        var rows = 0;
        var remaining = _options.MaximumOutboxEventsPerRun;
        while (remaining > 0 && !working.OutboxComplete)
        {
            var scanEvents =
                SelectOutboxScope(working) == OutboxScanScope.Events;
            var limit = Math.Min(_options.BatchSize, remaining);
            if (scanEvents)
            {
                var page = await snapshot.ReadOutboxPageAsync(
                    working.OutboxEventCursor,
                    limit,
                    cancellationToken);
                ValidatePage(
                    working.OutboxEventCursor,
                    page,
                    limit);
                rows += page.RowsScanned;
                remaining -= page.RowsScanned;
                Add(counts, page.Findings);
                working = working with
                {
                    OutboxEventCursor = page.ReachedEnd
                        ? 0
                        : page.NextKey,
                    OutboxEventsComplete = page.ReachedEnd,
                    NextOutboxScope = OutboxScanScope.Positions
                };
            }
            else
            {
                var page =
                    await snapshot.ReadOutboxPositionPageAsync(
                        working.OutboxPositionCursor,
                        limit,
                        cancellationToken);
                ValidatePositionPage(
                    working.OutboxPositionCursor,
                    page,
                    limit);
                rows += page.RowsScanned;
                remaining -= page.RowsScanned;
                Add(counts, page.Findings);
                working = working with
                {
                    OutboxPositionCursor = page.ReachedEnd
                        ? ReconciliationOutboxPositionCursor.Start
                        : page.NextCursor,
                    OutboxPositionsComplete = page.ReachedEnd,
                    NextOutboxScope = OutboxScanScope.Events
                };
            }
        }

        return new ScopeScanResult(working, rows);
    }

    private static OutboxScanScope SelectOutboxScope(
        ReconciliationScanState state)
    {
        if (state.OutboxEventsComplete)
        {
            return OutboxScanScope.Positions;
        }

        if (state.OutboxPositionsComplete)
        {
            return OutboxScanScope.Events;
        }

        return state.NextOutboxScope;
    }

    private static void ValidatePage(
        long cursor,
        ReconciliationPage page,
        int limit)
    {
        if (page.RowsScanned < 0 ||
            page.RowsScanned > limit ||
            (!page.ReachedEnd &&
             (page.RowsScanned == 0 || page.NextKey <= cursor)))
        {
            throw new InvalidDataException(
                "The reconciliation reader returned an invalid page.");
        }
    }

    private static void ValidatePositionPage(
        ReconciliationOutboxPositionCursor cursor,
        ReconciliationOutboxPositionPage page,
        int limit)
    {
        if (page.RowsScanned < 0 ||
            page.RowsScanned > limit ||
            (!page.ReachedEnd &&
             (page.RowsScanned == 0 ||
              Compare(page.NextCursor, cursor) <= 0)))
        {
            throw new InvalidDataException(
                "The reconciliation reader returned an invalid position page.");
        }
    }

    private static int Compare(
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

    private enum OutboxScanScope : byte
    {
        Events = 1,
        Positions = 2
    }

    private readonly record struct ReconciliationScanState(
        long CharacterCursor,
        bool CharactersComplete,
        long OutboxEventCursor,
        bool OutboxEventsComplete,
        ReconciliationOutboxPositionCursor OutboxPositionCursor,
        bool OutboxPositionsComplete,
        OutboxScanScope NextOutboxScope,
        IReadOnlyList<ReconciliationCategoryCount>
            AccumulatedFindings)
    {
        public static ReconciliationScanState Start => new(
            CharacterCursor: 0,
            CharactersComplete: false,
            OutboxEventCursor: 0,
            OutboxEventsComplete: false,
            ReconciliationOutboxPositionCursor.Start,
            OutboxPositionsComplete: false,
            OutboxScanScope.Events,
            AccumulatedFindings: []);

        public bool OutboxComplete =>
            OutboxEventsComplete && OutboxPositionsComplete;

        public bool SweepCompleted =>
            CharactersComplete && OutboxComplete;
    }

    private readonly record struct ScopeScanResult(
        ReconciliationScanState State,
        int RowsScanned);
}
