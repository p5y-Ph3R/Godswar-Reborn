using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresDeveloperItemGrantIntegrationChecks
{
    private static async Task AssertSocketSpellGrantsAsync(
        string connectionString)
    {
        const uint firstItemId = 4270;
        const uint lastItemId = 4273;
        const int quantity = 99;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "socket");

        for (var itemId = firstItemId; itemId <= lastItemId; itemId++)
        {
            DeveloperItemGrantExecutionResult result;
            await using (var source =
                         NpgsqlDataSource.Create(connectionString))
            {
                result = await CreateExecutor(source).ExecuteAsync(
                    CreateEnvelope(
                        fixture,
                        Guid.NewGuid(),
                        quantity,
                        itemId: itemId));
            }

            var receipt = RequireReceipt(
                result,
                DeveloperItemGrantExecutionDisposition.Committed,
                $"Socket Spell {itemId} developer grant");
            Check.True(
                receipt.ItemId == itemId &&
                receipt.GrantedQuantity == quantity &&
                receipt.InventoryRevision == itemId - firstItemId + 1,
                $"Socket Spell {itemId} returns its authoritative receipt");
        }

        await AssertSocketSpellGrantStateAsync(
            connectionString,
            fixture.CharacterId);
    }

    private static async Task AssertSocketSpellGrantStateAsync(
        string connectionString,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                (
                    SELECT count(*)::integer
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                      AND item_row.prop_id BETWEEN 4270 AND 4273
                      AND item_row.stack = 99
                      AND item_row.bound = 0
                ),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.reason_code =
                          'developer_socket_spell_grant'
                      AND ledger.mutation_kind = 'add'
                      AND (ledger.after_state ->> 'prop_id')::integer
                          BETWEEN 4270 AND 4273
                      AND (ledger.after_state ->> 'stack')::integer = 99
                      AND (ledger.after_state ->> 'bound')::integer = 0
                ),
                (
                    SELECT count(*)::integer
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = @characterId
                      AND ledger.reason_code =
                          'developer_empty_holy_box_grant'
                      AND (ledger.after_state ->> 'prop_id')::integer
                          BETWEEN 4270 AND 4273
                ),
                (
                    SELECT count(*)::integer
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_type = 'character_inventory'
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = 'developer_item_grant'
                ),
                (
                    SELECT count(*)::integer
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = 'character_inventory'
                      AND outbox.aggregate_key = @aggregateKey
                      AND outbox.event_type =
                          'inventory.developer_item_granted'
                )
            FROM public.character_base character_row
            WHERE character_row.id = @characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            DeveloperItemGrantPersistenceCodec.AggregateKey(characterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 4 &&
            reader.GetInt32(1) == 4 &&
            reader.GetInt32(2) == 4 &&
            reader.GetInt32(3) == 0 &&
            reader.GetInt32(4) == 4 &&
            reader.GetInt32(5) == 4 &&
            !await reader.ReadAsync(),
            "Socket Spell grants persist four unbound stacks with exact " +
            "inbox, ledger, and outbox evidence");
    }
}
