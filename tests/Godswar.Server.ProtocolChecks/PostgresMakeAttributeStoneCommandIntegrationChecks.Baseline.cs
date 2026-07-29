using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresMakeAttributeStoneCommandIntegrationChecks
{
    private static async Task
        AssertRuntimeBaselineCreationRollbackAndRetryAsync(
            string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "base",
            captureBaseline: false);
        var envelope = CreateEnvelope(fixture, Guid.NewGuid());

        AssertNoRuntimeBaselineState(
            await ReadStateAsync(connectionString, fixture),
            "fixture starts without economy evidence");
        Check.True(
            await ReadBaselineEvidenceAsync(
                connectionString,
                fixture) == (0, 0),
            "fixture starts without baseline or baseline items");

        await using (var faultSource =
                     NpgsqlDataSource.Create(connectionString))
        {
            await AssertInjectedFaultAsync(
                () => CreateExecutor(
                        faultSource,
                        new ThrowingStoneProbe(
                            PostgresMakeAttributeStoneCommandStage
                                .AuditInserted))
                    .ExecuteAsync(envelope),
                PostgresMakeAttributeStoneCommandStage.AuditInserted);
        }

        AssertNoRuntimeBaselineState(
            await ReadStateAsync(connectionString, fixture),
            "pre-commit failure rolls back the runtime baseline");
        Check.True(
            await ReadBaselineEvidenceAsync(
                connectionString,
                fixture) == (0, 0),
            "pre-commit failure leaves no partial baseline evidence");

        await using (var recoverySource =
                     NpgsqlDataSource.Create(connectionString))
        {
            _ = RequireReceipt(
                await CreateExecutor(recoverySource)
                    .ExecuteAsync(envelope),
                MakeAttributeStoneExecutionDisposition.Committed,
                "runtime-baseline retry");
        }

        Check.True(
            await ReadBaselineEvidenceAsync(
                connectionString,
                fixture) == (1, 1),
            "retry atomically captures one baseline and its source item");
        AssertCommittedState(
            await ReadStateAsync(connectionString, fixture),
            expectedDuplicateCount: 0,
            expectedConflictCount: 0,
            "runtime-baseline retry commits the recipe exactly once");
    }

    private static void AssertNoRuntimeBaselineState(
        StoneDurableState state,
        string description)
    {
        Check.True(
            state.InventoryRevision == 0 &&
            state.DustQuantity == RecipeDustQuantity &&
            state.StoneQuantity == 0 &&
            state.TotalBagItemCount == 1 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0 &&
            !state.IsReconciled,
            description);
    }

    private static async Task<(long BaselineCount, long ItemCount)>
        ReadBaselineEvidenceAsync(
            string connectionString,
            StoneFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (
                    SELECT count(*)::bigint
                    FROM public.character_economy_baseline baseline
                    WHERE baseline.account_id = @accountId
                      AND baseline.character_id = @characterId
                      AND baseline.inventory_revision = 0
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_inventory_baseline_items item
                    WHERE item.account_id = @accountId
                      AND item.character_id = @characterId
                );
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The Make Attribute Stone baseline evidence disappeared.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
