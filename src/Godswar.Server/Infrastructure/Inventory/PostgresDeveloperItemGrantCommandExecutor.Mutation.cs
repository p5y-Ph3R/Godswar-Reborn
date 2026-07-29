using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresDeveloperItemGrantCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<DeveloperItemGrantCommand> envelope,
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
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
            reader.GetInt64(0) >= 0
            ? new LockedCharacter(reader.GetInt64(0))
            : null;
    }

    private async Task<LockedKitBag> LockKitBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        DeveloperGrantMaterialDefinition material,
        CancellationToken cancellationToken)
    {
        var occupied = new bool[KitBagItemGrantPlanner.SlotCount];
        var fillable = new List<LockedKitBagItem>();
        await using var command = CreateCommand(
            """
            SELECT
                id,
                slot_index,
                prop_id,
                bound,
                stack,
                to_jsonb(character_items)::text
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index BETWEEN 0 AND @lastSlot
            ORDER BY slot_index
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "lastSlot",
            KitBagItemGrantPlanner.SlotCount - 1);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt16(1);
            occupied[slot] = true;
            if (reader.GetInt32(2) ==
                    checked((int)material.ItemId) &&
                reader.GetInt16(3) == material.GrantedBound &&
                reader.GetInt16(4) < material.StackCap)
            {
                fillable.Add(
                    new LockedKitBagItem(
                        reader.GetInt64(0),
                        slot,
                        reader.GetInt16(4),
                        reader.GetString(5)));
            }
        }

        var empty = Enumerable.Range(0, occupied.Length)
            .Where(slot => !occupied[slot])
            .Select(static slot => checked((short)slot))
            .ToArray();
        return new LockedKitBag(fillable, empty);
    }

    private async Task<IReadOnlyList<InventoryMutation>>
        ApplyInventoryGrantAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<DeveloperItemGrantCommand> envelope,
            DeveloperGrantMaterialDefinition material,
            LockedKitBag items,
            long inventoryRevision,
            CancellationToken cancellationToken)
    {
        var mutations = new List<InventoryMutation>();
        var remaining = envelope.Command.Quantity;
        foreach (var existing in items.FillableStacks)
        {
            if (remaining == 0)
            {
                break;
            }

            var added = Math.Min(
                remaining,
                material.StackCap - existing.Stack);
            var updatedStack = checked((short)(existing.Stack + added));
            await using var command = CreateCommand(
                """
                UPDATE public.character_items
                SET stack = @updatedStack,
                    updated_at = now()
                WHERE id = @itemInstanceId
                  AND stack = @expectedStack
                RETURNING to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "updatedStack",
                updatedStack);
            command.Parameters.AddWithValue(
                "itemInstanceId",
                existing.ItemInstanceId);
            command.Parameters.AddWithValue(
                "expectedStack",
                existing.Stack);
            var afterState =
                await command.ExecuteScalarAsync(cancellationToken)
                    as string ??
                throw new InvalidDataException(
                    "The locked inventory stack was not updated " +
                    "exactly once.");
            mutations.Add(
                new InventoryMutation(
                    existing.ItemInstanceId,
                    "update",
                    existing.BeforeState,
                    afterState));
            remaining -= added;
        }

        foreach (var slot in items.EmptySlots)
        {
            if (remaining == 0)
            {
                break;
            }

            var stack = Math.Min(remaining, material.StackCap);
            await using var command = CreateCommand(
                """
                INSERT INTO public.character_items (
                    user_id,
                    item_location,
                    slot_index,
                    prop_id,
                    item_quality,
                    item_grade,
                    bound,
                    stack,
                    item_exp,
                    holy_suit_code
                )
                VALUES (
                    @characterId,
                    1,
                    @slotIndex,
                    @itemId,
                    1,
                    1,
                    @bound,
                    @stack,
                    0,
                    0
                )
                RETURNING
                    id,
                    to_jsonb(character_items)::text;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "characterId",
                envelope.Subject.CharacterId);
            command.Parameters.AddWithValue("slotIndex", slot);
            command.Parameters.AddWithValue(
                "itemId",
                checked((int)envelope.Command.ItemId));
            command.Parameters.AddWithValue(
                "bound",
                material.GrantedBound);
            command.Parameters.AddWithValue(
                "stack",
                checked((short)stack));
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "The inventory item insert returned no state.");
            }

            mutations.Add(
                new InventoryMutation(
                    reader.GetInt64(0),
                    "add",
                    BeforeState: null,
                    reader.GetString(1)));
            remaining -= stack;
        }

        if (remaining != 0 || mutations.Count == 0)
        {
            throw new InvalidDataException(
                "Validated inventory capacity did not consume the " +
                "complete grant.");
        }

        await using var revisionCommand = CreateCommand(
            """
            UPDATE public.character_base
            SET inventory_revision = @inventoryRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND inventory_revision = @expectedRevision;
            """,
            connection,
            transaction);
        revisionCommand.Parameters.AddWithValue(
            "inventoryRevision",
            inventoryRevision);
        revisionCommand.Parameters.AddWithValue(
            "expectedRevision",
            inventoryRevision - 1);
        revisionCommand.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        revisionCommand.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        if (await revisionCommand.ExecuteNonQueryAsync(
                cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The inventory revision did not advance exactly once.");
        }

        return mutations;
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandEnvelope<DeveloperItemGrantCommand> envelope,
        long inventoryRevision,
        IReadOnlyList<InventoryMutation> mutations,
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
                @mutationKind,
                1,
                @beforeState,
                @afterState,
                @reasonCode
            );
            """,
            connection,
            transaction);
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
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
                mutation.ItemInstanceId);
            command.Parameters.AddWithValue(
                "mutationKind",
                mutation.MutationKind);
            AddJsonParameter(
                command,
                "beforeState",
                mutation.BeforeState);
            AddJsonParameter(
                command,
                "afterState",
                mutation.AfterState);
            command.Parameters.AddWithValue(
                "reasonCode",
                "developer_material_grant");
            if (await command.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The inventory ledger append was not exact.");
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
            DeveloperItemGrantPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            DeveloperItemGrantPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            inventoryRevision);
        command.Parameters.AddWithValue(
            "eventType",
            DeveloperItemGrantPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            DeveloperItemGrantPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            DeveloperItemGrantPersistenceCodec.OrderingPolicy);
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
                "The inventory grant outbox insert was not exact.");
        }
    }

    private static void AddJsonParameter(
        NpgsqlCommand command,
        string name,
        string? value)
    {
        command.Parameters.Add(
            name,
            NpgsqlDbType.Jsonb).Value =
            value is null ? DBNull.Value : value;
    }
}
