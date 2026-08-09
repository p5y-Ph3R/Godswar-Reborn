using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private const uint PermanentCostumeItemId = 8068;

    private static async Task AssertPermanentCostumeGrantAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "costume");
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(
            fixture,
            operationId,
            quantity: 1,
            itemId: PermanentCostumeItemId);

        DeveloperItemGrantExecutionResult first;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            first = await CreateExecutor(source).ExecuteAsync(envelope);
        }

        var receipt = RequireReceipt(
            first,
            DeveloperItemGrantExecutionDisposition.Committed,
            "permanent costume developer grant");
        Check.True(
            receipt.ItemId == PermanentCostumeItemId &&
            receipt.GrantedQuantity == 1 &&
            receipt.InventoryRevision == 1,
            "permanent costume grant returns its durable receipt");

        DeveloperItemGrantExecutionResult replay;
        await using (var source = NpgsqlDataSource.Create(connectionString))
        {
            replay = await CreateExecutor(source).ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    operationId,
                    quantity: 1,
                    connectionId: Guid.NewGuid(),
                    itemId: PermanentCostumeItemId));
        }

        var replayReceipt = RequireReceipt(
            replay,
            DeveloperItemGrantExecutionDisposition.Duplicate,
            "permanent costume exact retry");
        AssertReceiptsEqual(
            receipt,
            replayReceipt,
            "permanent costume retry returns the stored receipt");
        await AssertPermanentCostumeStateAsync(
            connectionString,
            fixture.CharacterId);
    }

    private static async Task AssertPermanentCostumeStateAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                item_row.item_location,
                item_row.slot_index,
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
                      AND ledger.reason_code = 'developer_costume_grant'
                ),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.reason_code =
                          'developer_empty_holy_box_grant'
                      AND (ledger.after_state ->> 'prop_id')::integer =
                          @itemId
                ),
                COALESCE((
                    SELECT max(inbox.duplicate_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_type = 'character_inventory'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'developer_item_grant'
                ), 0)::integer,
                (
                    SELECT count(*)::integer
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = 'character_inventory'
                      AND outbox.aggregate_key = @aggregateKey
                      AND outbox.event_type =
                          'inventory.developer_item_granted'
                )
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
            checked((int)PermanentCostumeItemId));
        command.Parameters.AddWithValue(
            "aggregateKey",
            DeveloperItemGrantPersistenceCodec.AggregateKey(characterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt16(0) == 1 &&
            reader.GetInt16(1) == 0 &&
            reader.GetInt32(2) == 0 &&
            reader.GetInt16(3) == 1 &&
            reader.GetInt16(4) == 1 &&
            reader.GetInt16(5) == 1 &&
            reader.GetInt16(6) == 1 &&
            reader.GetInt64(7) == 1 &&
            reader.GetInt32(8) == 1 &&
            reader.GetInt32(9) == 1 &&
            reader.GetInt32(10) == 0 &&
            reader.GetInt32(11) == 1 &&
            reader.GetInt32(12) == 1 &&
            !await reader.ReadAsync(),
            "permanent costume is inserted once as a bound Fashion item " +
            "with costume-specific durable evidence");
    }
}
