using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemMoveCommandIntegrationChecks
{
    private static async Task AssertTerminalSafetyAsync(
        string connectionString)
    {
        await AssertEmptySourceReplaySafetyAsync(connectionString);
        await AssertStaleSourceAsync(connectionString);
        await AssertStaleDestinationAsync(connectionString);
    }

    private static async Task AssertEmptySourceReplaySafetyAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "empty",
            sourcePresent: false,
            destinationPresent: true);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var terminal = RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            KitBagItemMoveExecutionDisposition.TerminalRejected,
            KitBagItemMoveResultStatus.EmptySource,
            "empty source");
        var beforeReplacement = await ReadStateAsync(
            connectionString,
            fixture);
        AssertTerminalEvidence(
            beforeReplacement,
            "empty source");

        var replacementId = await InsertItemAsync(
            connectionString,
            fixture,
            fixture.SourceSlot,
            Item(4220, 3));
        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.Subject.CharacterId),
                operationId,
                fixture.SourceSlot,
                fixture.DestinationSlot),
            KitBagItemMoveExecutionDisposition.Duplicate,
            KitBagItemMoveResultStatus.EmptySource,
            "late replacement replay");
        Check.True(
            replay == terminal,
            "late replacement returns stored terminal receipt");
        var afterReplacement = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(
            replacementId,
            afterReplacement.SourceItemId,
            "late replacement is never moved by replay");
        Check.Equal(
            fixture.DestinationItemId!.Value,
            afterReplacement.DestinationItemId,
            "terminal replay never touches destination");
        Check.Equal(
            0L,
            afterReplacement.LedgerCount,
            "terminal replay creates no movement ledger");
    }

    private static async Task AssertStaleSourceAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "stales");
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await ExecuteAsync(
            CreateExecutor(dataSource),
            fixture,
            Guid.NewGuid(),
            expectedSource: Item(4214, 1).ToCompactString());
        var receipt = RequireReceipt(
            result,
            KitBagItemMoveExecutionDisposition.TerminalRejected,
            KitBagItemMoveResultStatus.StaleSource,
            "stale source");
        Check.True(
            receipt.AuthoritativeSourceCompactItemState ==
                fixture.SourceState,
            "stale-source receipt returns authoritative source");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        AssertTerminalEvidence(state, "stale source");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.SourceItemId,
            "stale source does not move");
    }

    private static async Task AssertStaleDestinationAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "staled",
            destinationPresent: true);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await ExecuteAsync(
            CreateExecutor(dataSource),
            fixture,
            Guid.NewGuid(),
            expectedDestination: "[]");
        var receipt = RequireReceipt(
            result,
            KitBagItemMoveExecutionDisposition.TerminalRejected,
            KitBagItemMoveResultStatus.StaleDestination,
            "stale destination");
        Check.True(
            receipt.AuthoritativeDestinationCompactItemState ==
                fixture.DestinationState,
            "stale-destination receipt returns authoritative state");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        AssertTerminalEvidence(state, "stale destination");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.SourceItemId,
            "stale destination preserves source");
        Check.Equal(
            fixture.DestinationItemId!.Value,
            state.DestinationItemId,
            "stale destination preserves destination");
    }

    private static void AssertTerminalEvidence(
        MoveDurableState state,
        string description)
    {
        Check.Equal(0L, state.InventoryRevision, $"{description} revision");
        Check.Equal(1L, state.AuditCount, $"{description} audit");
        Check.Equal(1L, state.InboxCount, $"{description} inbox");
        Check.Equal(
            0L,
            state.CompatibilityAuditCount,
            $"{description} compatibility audit");
        Check.Equal(0L, state.LedgerCount, $"{description} ledger");
        Check.Equal(0L, state.OutboxCount, $"{description} outbox");
        Check.Equal(
            0L,
            state.TemporaryItemCount,
            $"{description} temp item");
    }
}
