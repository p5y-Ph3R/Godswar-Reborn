using System.Text;
using Godswar.Server.Application.Commands;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemDeleteCommandExecutor
{
    private async Task<long?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT inventory_revision
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long revision && revision >= 0
            ? revision
            : null;
    }

    private async Task<LockedKitBagItem?> LockKitBagSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4,
                attribute5,
                attribute_level1, attribute_level2,
                attribute_level3, attribute_level4,
                attribute_level5,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket2_effect_id, holy_socket2_level,
                holy_socket3_effect_id, holy_socket3_level,
                holy_socket4_effect_id, holy_socket4_level,
                holy_socket5_effect_id, holy_socket5_level,
                holy_socket6_effect_id, holy_socket6_level,
                to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @kitBagSlot
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "kitBagSlot",
            checked((short)kitBagSlot));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LockedKitBagItem(
            reader.GetInt64(0),
            reader.GetInt16(1),
            ReadCompactItem(reader),
            reader.GetString(32));
    }

    private async Task DeleteItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedKitBagItem item,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            WITH deleted AS (
                DELETE FROM public.character_items
                WHERE id = @itemInstanceId
                  AND user_id = @characterId
                  AND item_location = 1
                  AND slot_index = @kitBagSlot
                RETURNING *
            )
            INSERT INTO public.character_item_audit (
                source,
                action,
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                old_item
            )
            SELECT
                'client-ground-delete',
                'delete',
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                item_exp,
                to_jsonb(deleted)
            FROM deleted;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            item.ItemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "kitBagSlot",
            item.SlotIndex);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The locked kit-bag item was not deleted exactly once.");
        }
    }

    private async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long expectedRevision,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @nextRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND inventory_revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "nextRevision",
            nextRevision);
        command.Parameters.AddWithValue(
            "expectedRevision",
            expectedRevision);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The inventory revision did not advance exactly once.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandSubject subject,
        long inventoryRevision,
        LockedKitBagItem item,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id,
                account_id,
                character_id,
                inventory_revision,
                entry_ordinal,
                item_instance_id,
                mutation_kind,
                state_contract_version,
                before_state,
                after_state,
                reason_code
            )
            VALUES (
                @inboxId,
                @accountId,
                @characterId,
                @inventoryRevision,
                0,
                @itemInstanceId,
                'delete',
                1,
                @beforeState,
                NULL,
                @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "itemInstanceId",
            item.ItemInstanceId);
        command.Parameters.Add(
            "beforeState",
            NpgsqlDbType.Jsonb).Value = item.BeforeState;
        command.Parameters.AddWithValue(
            "reasonCode",
            KitBagItemDeletePersistenceCodec.LedgerReasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The kit-bag delete ledger append was not exact.");
        }
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string aggregateKey,
        long inventoryRevision,
        Guid eventId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                max_attempts
            )
            VALUES (
                @eventId,
                @inboxId,
                @consumerKey,
                @aggregateType,
                @aggregateKey,
                @aggregateVersion,
                @eventType,
                @contractVersion,
                @orderingPolicy,
                @payload,
                @maxAttempts
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            KitBagItemDeletePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            KitBagItemDeletePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            KitBagItemDeletePersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            KitBagItemDeletePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            KitBagItemDeletePersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The kit-bag delete outbox insert was not exact.");
        }
    }
}
