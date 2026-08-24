using System.Buffers;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseExpansionCommandExecutor
{
    private async Task ApplyKeyMutationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        ExpansionPlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var item in plan.KeyItems)
        {
            await InsertKeyCompatibilityAuditAsync(
                connection,
                transaction,
                characterId,
                plan.KeyItemId,
                item,
                cancellationToken);
            var sql = item.AfterStack == 0
                ? """
                  DELETE FROM public.character_items
                  WHERE id = @itemInstanceId
                    AND user_id = @characterId
                    AND item_location = 1
                    AND slot_index = @slot
                    AND prop_id = @keyItemId
                    AND stack = @beforeStack
                    AND NOT EXISTS (
                        SELECT 1
                        FROM public.sealed_pet_items link
                        WHERE link.item_instance_id = character_items.id
                    );
                  """
                : """
                  UPDATE public.character_items
                  SET stack = @afterStack,
                      updated_at = transaction_timestamp()
                  WHERE id = @itemInstanceId
                    AND user_id = @characterId
                    AND item_location = 1
                    AND slot_index = @slot
                    AND prop_id = @keyItemId
                    AND stack = @beforeStack;
                  """;
            await using var command = CreateCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(
                "itemInstanceId",
                item.ItemInstanceId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("slot", item.Slot);
            command.Parameters.AddWithValue("keyItemId", plan.KeyItemId);
            command.Parameters.AddWithValue("beforeStack", item.BeforeStack);
            if (item.AfterStack > 0)
            {
                command.Parameters.AddWithValue(
                    "afterStack",
                    checked((short)item.AfterStack));
            }
            await RequireOneAsync(
                command,
                "A Storage Box Key mutation was not exact.",
                cancellationToken);
        }
    }

    private async Task InsertKeyCompatibilityAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int keyItemId,
        LockedKeyItem item,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_item_audit (
                source, action, user_id, item_location, slot_index,
                prop_id, old_item)
            VALUES (
                'warehouse-expansion', @action, @characterId, 1, @slot,
                @keyItemId, @oldItem);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "action",
            item.AfterStack == 0 ? "delete" : "update");
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", item.Slot);
        command.Parameters.AddWithValue("keyItemId", keyItemId);
        command.Parameters.Add("oldItem", NpgsqlDbType.Jsonb).Value =
            item.BeforeState;
        await RequireOneAsync(
            command,
            "The Storage Box Key compatibility audit was not exact.",
            cancellationToken);
    }

    private async Task AdvanceRevisionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        LockedCharacter character,
        ExpansionPlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET warehouse_capacity = @capacity,
                warehouse_revision = @warehouseRevision,
                inventory_revision = @inventoryRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND warehouse_capacity = @expectedCapacity
              AND warehouse_revision = @expectedWarehouseRevision
              AND inventory_revision = @expectedInventoryRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("capacity", plan.CurrentCapacity);
        command.Parameters.AddWithValue(
            "warehouseRevision",
            plan.NextWarehouseRevision);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            plan.NextInventoryRevision);
        command.Parameters.AddWithValue("accountId", subject.AccountId);
        command.Parameters.AddWithValue("characterId", subject.CharacterId);
        command.Parameters.AddWithValue(
            "expectedCapacity",
            character.Capacity);
        command.Parameters.AddWithValue(
            "expectedWarehouseRevision",
            character.WarehouseRevision);
        command.Parameters.AddWithValue(
            "expectedInventoryRevision",
            character.InventoryRevision);
        await RequireOneAsync(
            command,
            "Warehouse expansion revisions did not advance exactly once.",
            cancellationToken);
    }

    private async Task InsertExpansionLedgersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        long inboxId,
        long revision,
        ExpansionPlan plan,
        CancellationToken cancellationToken)
    {
        for (short ordinal = 0; ordinal < plan.KeyItems.Count; ordinal++)
        {
            var item = plan.KeyItems[ordinal];
            var after = await ReadKeyStateAsync(
                connection,
                transaction,
                subject.CharacterId,
                item.ItemInstanceId,
                cancellationToken);
            await using var command = CreateCommand(
                """
                INSERT INTO public.character_inventory_ledger (
                    command_inbox_id, account_id, character_id,
                    inventory_revision, entry_ordinal, item_instance_id,
                    mutation_kind, state_contract_version, before_state,
                    after_state, reason_code)
                VALUES (
                    @inboxId, @accountId, @characterId, @revision,
                    @ordinal, @itemInstanceId, @kind, 1, @beforeState,
                    @afterState, @reasonCode);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("inboxId", inboxId);
            command.Parameters.AddWithValue("accountId", subject.AccountId);
            command.Parameters.AddWithValue("characterId", subject.CharacterId);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("ordinal", ordinal);
            command.Parameters.AddWithValue(
                "itemInstanceId",
                item.ItemInstanceId);
            command.Parameters.AddWithValue(
                "kind",
                after is null ? "delete" : "update");
            command.Parameters.Add("beforeState", NpgsqlDbType.Jsonb).Value =
                item.BeforeState;
            command.Parameters.Add("afterState", NpgsqlDbType.Jsonb).Value =
                after is null ? DBNull.Value : after;
            command.Parameters.AddWithValue(
                "reasonCode",
                WarehouseExpansionPersistenceCodec.LedgerReasonCode);
            await RequireOneAsync(
                command,
                "A warehouse expansion ledger append was not exact.",
                cancellationToken);
        }
    }

    private async Task<string?> ReadKeyStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long itemInstanceId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT to_jsonb(item)::text
            FROM public.character_items item
            WHERE item.id = @itemInstanceId
              AND item.user_id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemInstanceId", itemInstanceId);
        command.Parameters.AddWithValue("characterId", characterId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task InsertExpansionOutboxesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int characterId,
        ExpansionPlan plan,
        Guid inventoryEventId,
        Guid capacityEventId,
        PersistedEvidence evidence,
        CancellationToken cancellationToken)
    {
        await InsertExpansionOutboxAsync(
            connection,
            transaction,
            inboxId,
            inventoryEventId,
            WarehouseExpansionPersistenceCodec.InventoryConsumerKey,
            WarehouseExpansionPersistenceCodec.InventoryAggregateType,
            WarehouseExpansionPersistenceCodec.InventoryAggregateKey(
                characterId),
            plan.NextInventoryRevision,
            WarehouseExpansionPersistenceCodec.InventoryEventType,
            evidence.Payload,
            cancellationToken);
        var capacityPayload = WarehouseExpansionPersistenceCodec.Encode(
            evidence.Receipt with { OutboxEventId = capacityEventId });
        await InsertExpansionOutboxAsync(
            connection,
            transaction,
            inboxId,
            capacityEventId,
            WarehouseExpansionPersistenceCodec.WarehouseConsumerKey,
            WarehouseExpansionPersistenceCodec.WarehouseAggregateType,
            WarehouseExpansionPersistenceCodec.WarehouseAggregateKey(
                characterId),
            plan.NextWarehouseRevision,
            WarehouseExpansionPersistenceCodec.WarehouseEventType,
            capacityPayload,
            cancellationToken);
    }

    private async Task InsertExpansionOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        Guid eventId,
        string consumerKey,
        string aggregateType,
        string aggregateKey,
        long revision,
        string eventType,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id, command_inbox_id, consumer_key, aggregate_type,
                aggregate_key, aggregate_version, event_type,
                contract_version, ordering_policy, payload, max_attempts)
            VALUES (
                @eventId, @inboxId, @consumerKey, @aggregateType,
                @aggregateKey, @revision, @eventType, @contractVersion,
                @orderingPolicy, @payload, @maxAttempts);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("consumerKey", consumerKey);
        command.Parameters.AddWithValue("aggregateType", aggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            WarehouseExpansionPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            WarehouseExpansionPersistenceCodec.OrderingPolicy);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue("maxAttempts", _maximumOutboxAttempts);
        await RequireOneAsync(
            command,
            "A warehouse expansion outbox append was not exact.",
            cancellationToken);
    }

    private async Task InsertSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<WarehouseExpansionCommand> envelope,
        PersistedEvidence evidence,
        ExpansionPlan plan,
        Guid inventoryEventId,
        Guid capacityEventId,
        CancellationToken cancellationToken)
    {
        var mutationBuffer = new ArrayBufferWriter<byte>(1_024);
        using (var writer = new Utf8JsonWriter(mutationBuffer))
        {
            WarehouseTransferPersistenceCodec.WriteMutations(
                writer,
                plan.Mutations);
        }
        await using var command = CreateCommand(
            """
            INSERT INTO public.warehouse_expansion_settlements (
                account_id, character_id, previous_capacity,
                current_capacity, keys_consumed, key_item_id,
                policy_revision, policy_sha256, warehouse_revision,
                inventory_revision, item_mutations, command_inbox_id,
                audit_id, capacity_event_id, inventory_event_id)
            VALUES (
                @accountId, @characterId, @previousCapacity,
                @currentCapacity, @keysConsumed, @keyItemId,
                @policyRevision, @policySha256, @warehouseRevision,
                @inventoryRevision, @mutations, @inboxId,
                @auditId, @capacityEventId, @inventoryEventId);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "previousCapacity",
            checked((short)plan.PreviousCapacity));
        command.Parameters.AddWithValue(
            "currentCapacity",
            checked((short)plan.CurrentCapacity));
        command.Parameters.AddWithValue(
            "keysConsumed",
            checked((short)plan.ConsumedKeys));
        command.Parameters.AddWithValue("keyItemId", plan.KeyItemId);
        command.Parameters.AddWithValue("policyRevision", _policy.Revision);
        command.Parameters.AddWithValue("policySha256", _policy.Sha256);
        command.Parameters.AddWithValue(
            "warehouseRevision",
            plan.NextWarehouseRevision);
        command.Parameters.AddWithValue(
            "inventoryRevision",
            plan.NextInventoryRevision);
        command.Parameters.Add("mutations", NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(mutationBuffer.WrittenSpan);
        command.Parameters.AddWithValue("inboxId", evidence.InboxId);
        command.Parameters.AddWithValue("auditId", evidence.AuditId);
        command.Parameters.AddWithValue("capacityEventId", capacityEventId);
        command.Parameters.AddWithValue("inventoryEventId", inventoryEventId);
        await RequireOneAsync(
            command,
            "Warehouse expansion settlement was not exact.",
            cancellationToken);
    }

    private static async Task RequireOneAsync(
        NpgsqlCommand command,
        string message,
        CancellationToken cancellationToken)
    {
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(message);
        }
    }
}
