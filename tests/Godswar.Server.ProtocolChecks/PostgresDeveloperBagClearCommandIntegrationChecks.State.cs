using System.Globalization;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresDeveloperBagClearCommandIntegrationChecks
{
    private static async Task<ClearDurableState> ReadStateAsync(
        string connectionString,
        ClearFixture fixture,
        long? lateItemInstanceId = null)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                character_row.inventory_revision,
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 1
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE item_row.user_id = @characterId
                      AND item_row.item_location = 0
                      AND item_row.prop_id = @equipmentItemId
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_items item_row
                    WHERE @lateItemInstanceId IS NOT NULL
                      AND item_row.id = @lateItemInstanceId
                      AND item_row.user_id = @characterId
                      AND item_row.item_location = 1
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.command_audit audit
                    WHERE audit.principal_type = @principalType
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = @aggregateType
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.account_id = @accountId
                      AND ledger.character_id = @characterId
                      AND ledger.reason_code = 'developer_bag_clear'
                      AND ledger.mutation_kind = 'delete'
                      AND ledger.before_state IS NOT NULL
                      AND ledger.after_state IS NULL
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.character_item_audit item_audit
                    WHERE item_audit.user_id = @characterId
                      AND item_audit.source = 'developer-clearbag'
                      AND item_audit.action = 'delete'
                ),
                (
                    SELECT count(*)::bigint
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @aggregateKey
                      AND outbox.event_type = @eventType
                ),
                COALESCE((
                    SELECT max(inbox.duplicate_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                reconciliation.is_reconciled
            FROM public.character_base character_row
            JOIN public.character_inventory_reconciliation reconciliation
              ON reconciliation.character_id = character_row.id
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId;
            """,
            connection);
        AddStateParameters(
            command,
            fixture,
            lateItemInstanceId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The developer bag-clear fixture disappeared.");
        }

        return new ClearDurableState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetBoolean(10));
    }

    private static void AddStateParameters(
        NpgsqlCommand command,
        ClearFixture fixture,
        long? lateItemInstanceId)
    {
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "equipmentItemId",
            EquipmentItemId);
        command.Parameters.Add(
            "lateItemInstanceId",
            NpgsqlDbType.Bigint).Value =
            lateItemInstanceId.HasValue
                ? lateItemInstanceId.Value
                : DBNull.Value;
        command.Parameters.AddWithValue(
            "principalType",
            DeveloperBagClearPersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateType",
            DeveloperBagClearPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            DeveloperBagClearPersistenceCodec.AggregateKey(
                fixture.CharacterId));
        command.Parameters.AddWithValue(
            "commandFamily",
            DeveloperBagClearPersistenceCodec.CommandFamily);
        command.Parameters.AddWithValue(
            "eventType",
            DeveloperBagClearPersistenceCodec.EventType);
    }

    private sealed record ClearDurableState(
        long InventoryRevision,
        long BagItemCount,
        long EquipmentItemCount,
        long LateItemCount,
        long CommandAuditCount,
        long InboxCount,
        long LedgerCount,
        long ItemAuditCount,
        long OutboxCount,
        int DuplicateCount,
        bool IsReconciled);
}
