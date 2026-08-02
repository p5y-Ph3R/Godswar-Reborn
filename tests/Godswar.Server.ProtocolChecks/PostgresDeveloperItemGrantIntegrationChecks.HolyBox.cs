using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private const uint EmptyHolyBoxItemId = 9020;

    private static async Task AssertEmptyHolyBoxGrantAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "box");
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(
            fixture,
            operationId,
            quantity: 1,
            itemId: EmptyHolyBoxItemId);

        DeveloperItemGrantExecutionResult first;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(
                    source,
                    itemContent: TestItemContent.HolySuitContent)
                .ExecuteAsync(envelope);
        }

        var receipt = RequireReceipt(
            first,
            DeveloperItemGrantExecutionDisposition.Committed,
            "empty Holy Box developer grant");
        Check.True(
            receipt.ItemId == EmptyHolyBoxItemId &&
            receipt.GrantedQuantity == 1 &&
            receipt.InventoryRevision == 1,
            "empty Holy Box grant returns the durable inventory receipt");
        await AssertEmptyHolyBoxStateAsync(
            connectionString,
            fixture.CharacterId,
            expectedDuplicateCount: 0);

        DeveloperItemGrantExecutionResult replay;
        await using (var source =
                     NpgsqlDataSource.Create(connectionString))
        {
            replay = await CreateExecutor(
                    source,
                    itemContent: TestItemContent.HolySuitContent)
                .ExecuteAsync(
                    CreateEnvelope(
                        fixture,
                        operationId,
                        quantity: 1,
                        connectionId: Guid.NewGuid(),
                        itemId: EmptyHolyBoxItemId));
        }

        var replayReceipt = RequireReceipt(
            replay,
            DeveloperItemGrantExecutionDisposition.Duplicate,
            "empty Holy Box exact retry");
        AssertReceiptsEqual(
            receipt,
            replayReceipt,
            "empty Holy Box retry returns the stored receipt");
        await AssertEmptyHolyBoxStateAsync(
            connectionString,
            fixture.CharacterId,
            expectedDuplicateCount: 1);
    }

    private static async Task AssertLegacyEmptyHolyBoxGrantAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "legacybox");
        await using var store = new PostgresGameStore(
            connectionString,
            TestItemContent.HolySuitContent);
        var result = await store.AddForgingMaterialAsync(
            fixture.AccountId,
            fixture.CharacterId,
            EmptyHolyBoxItemId,
            1);
        Check.True(
            result.Added &&
            result.Character is not null,
            "legacy local-development path accepts the pinned Holy Box");

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT item_exp, stack, bound, item_quality, item_grade
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @itemId;
            """,
            connection);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)EmptyHolyBoxItemId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 0 &&
            reader.GetInt16(1) == 1 &&
            reader.GetInt16(2) == 1 &&
            reader.GetInt16(3) == 1 &&
            reader.GetInt16(4) == 1 &&
            !await reader.ReadAsync(),
            "legacy Holy Box grant persists one bound empty box");
    }

    private static async Task AssertEmptyHolyBoxStateAsync(
        string connectionString,
        int characterId,
        int expectedDuplicateCount)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                item_row.item_exp,
                item_row.stack,
                item_row.bound,
                item_row.item_quality,
                item_row.item_grade,
                character_row.inventory_revision,
                (
                    SELECT count(*)::integer
                    FROM public.character_items duplicate_item
                    WHERE duplicate_item.user_id = @characterId
                      AND duplicate_item.item_location = 1
                      AND duplicate_item.prop_id = @itemId
                ),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.reason_code =
                          'developer_empty_holy_box_grant'
                ),
                COALESCE((
                    SELECT max(inbox.duplicate_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_type = 'character_inventory'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family =
                          'developer_item_grant'
                ), 0)::integer
            FROM public.character_items item_row
            JOIN public.character_base character_row
              ON character_row.id = item_row.user_id
            WHERE item_row.user_id = @characterId
              AND item_row.item_location = 1
              AND item_row.prop_id = @itemId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)EmptyHolyBoxItemId));
        command.Parameters.AddWithValue(
            "aggregateKey",
            DeveloperItemGrantPersistenceCodec.AggregateKey(characterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 0 &&
            reader.GetInt16(1) == 1 &&
            reader.GetInt16(2) == 1 &&
            reader.GetInt16(3) == 1 &&
            reader.GetInt16(4) == 1 &&
            reader.GetInt64(5) == 1 &&
            reader.GetInt32(6) == 1 &&
            reader.GetInt32(7) == 1 &&
            reader.GetInt32(8) == expectedDuplicateCount &&
            !await reader.ReadAsync(),
            "empty Holy Box is inserted once, bound, and with exactly zero accumulated EXP");
    }
}
