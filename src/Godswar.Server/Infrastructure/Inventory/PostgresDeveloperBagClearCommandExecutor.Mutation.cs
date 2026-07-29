using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresDeveloperBagClearCommandExecutor
{
    private async Task<long?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<DeveloperBagClearCommand> envelope,
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
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long revision && revision >= 0
            ? revision
            : null;
    }

    private async Task<IReadOnlyList<LockedKitBagItem>>
        LockKitBagAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            CancellationToken cancellationToken)
    {
        var items = new List<LockedKitBagItem>();
        await using var command = CreateCommand(
            """
            SELECT
                id,
                slot_index,
                to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index BETWEEN 0 AND 95
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(
                new LockedKitBagItem(
                    reader.GetInt64(0),
                    reader.GetInt16(1),
                    reader.GetString(2)));
        }

        return items;
    }

    private async Task DeleteItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        IReadOnlyList<LockedKitBagItem> items,
        CancellationToken cancellationToken)
    {
        var itemIds = items
            .Select(static item => item.ItemInstanceId)
            .ToArray();
        await using var command = CreateCommand(
            """
            WITH deleted AS (
                DELETE FROM public.character_items
                WHERE user_id = @characterId
                  AND item_location = 1
                  AND id = ANY(@itemIds)
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
                'developer-clearbag',
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
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemIds", itemIds);
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            items.Count)
        {
            throw new InvalidDataException(
                "The locked bag items were not deleted exactly once.");
        }
    }

    private async Task AdvanceRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<DeveloperBagClearCommand> envelope,
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
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
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
        CommandEnvelope<DeveloperBagClearCommand> envelope,
        long inventoryRevision,
        IReadOnlyList<LockedKitBagItem> items,
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
                @entryOrdinal,
                @itemInstanceId,
                'delete',
                1,
                @beforeState,
                NULL,
                'developer_bag_clear'
            );
            """,
            connection,
            transaction);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            command.Parameters.Clear();
            command.Parameters.AddWithValue("inboxId", inboxId);
            command.Parameters.AddWithValue(
                "accountId",
                envelope.Subject.AccountId);
            command.Parameters.AddWithValue(
                "characterId",
                envelope.Subject.CharacterId);
            command.Parameters.AddWithValue(
                "inventoryRevision",
                inventoryRevision);
            command.Parameters.AddWithValue(
                "entryOrdinal",
                checked((short)index));
            command.Parameters.AddWithValue(
                "itemInstanceId",
                item.ItemInstanceId);
            command.Parameters.Add(
                "beforeState",
                NpgsqlDbType.Jsonb).Value = item.BeforeState;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The bag-clear ledger append was not exact.");
            }
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
            DeveloperBagClearPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            DeveloperBagClearPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            DeveloperBagClearPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            DeveloperBagClearPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            DeveloperBagClearPersistenceCodec.OrderingPolicy);
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
                "The bag-clear outbox insert was not exact.");
        }
    }
}
