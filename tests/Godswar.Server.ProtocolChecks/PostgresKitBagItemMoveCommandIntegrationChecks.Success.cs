using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresKitBagItemMoveCommandIntegrationChecks
{
    private static async Task AssertMoveAndSwapAsync(
        string connectionString)
    {
        await AssertMoveToEmptyAsync(connectionString);
        await AssertOccupiedSwapAsync(connectionString);
    }

    private static async Task AssertMoveToEmptyAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "move",
            sourceItem: Item(4212, 2) with
            {
                SocketCount = 1,
                Socket1EffectId = 2,
                Socket1Level = 10,
                Socket1Value = 797
            });
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await ExecuteAsync(
            CreateExecutor(dataSource),
            fixture,
            Guid.NewGuid());
        RequireReceipt(
            result,
            KitBagItemMoveExecutionDisposition.Committed,
            KitBagItemMoveResultStatus.Moved,
            "move to empty");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "move revision");
        Check.Equal(0L, state.SourceItemId, "source becomes empty");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.DestinationItemId,
            "source instance identity moves to destination");
        Check.Equal(0L, state.TemporaryItemCount, "no temp remains");
        Check.Equal(1L, state.AuditCount, "move command audit");
        Check.Equal(1L, state.InboxCount, "move command inbox");
        Check.Equal(
            1L,
            state.CompatibilityAuditCount,
            "one moved-item compatibility audit");
        Check.Equal(1L, state.LedgerCount, "one move ledger");
        Check.Equal(1L, state.OutboxCount, "one strict outbox event");
        Check.True(state.IsReconciled, "move inventory reconciles");
        await AssertLedgerOrdinalsAsync(
            connectionString,
            fixture,
            [0]);
    }

    private static async Task AssertOccupiedSwapAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "swap",
            destinationPresent: true);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await ExecuteAsync(
            CreateExecutor(dataSource),
            fixture,
            Guid.NewGuid());
        RequireReceipt(
            result,
            KitBagItemMoveExecutionDisposition.Committed,
            KitBagItemMoveResultStatus.Swapped,
            "occupied swap");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(1L, state.InventoryRevision, "swap revision");
        Check.Equal(
            fixture.DestinationItemId!.Value,
            state.SourceItemId,
            "destination identity moves to source");
        Check.Equal(
            fixture.SourceItemId!.Value,
            state.DestinationItemId,
            "source identity moves to destination");
        Check.Equal(0L, state.TemporaryItemCount, "swap temp cleared");
        Check.Equal(1L, state.AuditCount, "swap command audit");
        Check.Equal(1L, state.InboxCount, "swap command inbox");
        Check.Equal(
            2L,
            state.CompatibilityAuditCount,
            "each swapped item has compatibility audit");
        Check.Equal(2L, state.LedgerCount, "two ordered swap ledgers");
        Check.Equal(1L, state.OutboxCount, "single swap outbox");
        Check.True(state.IsReconciled, "swap inventory reconciles");
        await AssertLedgerOrdinalsAsync(
            connectionString,
            fixture,
            [0, 1]);
    }

    private static async Task AssertLedgerOrdinalsAsync(
        string connectionString,
        MoveFixture fixture,
        int[] expected)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT entry_ordinal
            FROM public.character_inventory_ledger
            WHERE account_id = @accountId
              AND character_id = @characterId
              AND inventory_revision = 1
              AND mutation_kind = 'move'
              AND before_state IS NOT NULL
              AND after_state IS NOT NULL
            ORDER BY entry_ordinal;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        var actual = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetInt16(0));
        }
        Check.True(
            actual.SequenceEqual(expected),
            "movement ledgers use deterministic ordinals");
    }
}
