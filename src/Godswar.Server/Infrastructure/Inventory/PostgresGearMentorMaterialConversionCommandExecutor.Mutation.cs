using System.Globalization;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresGearMentorMaterialConversionCommandExecutor
{
    private async Task<LockedKitBag> LockKitBagAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        var projection = new CompactItemEntry[96];
        var items = new Dictionary<short, LockedInventoryItem>();
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
                to_jsonb(character_items)::text,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2
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
            var slot = reader.GetInt16(1);
            var item = ReadCompactItem(reader);
            projection[slot] = item;
            if (!items.TryAdd(
                    slot,
                    new LockedInventoryItem(
                        reader.GetInt64(0),
                        1,
                        slot,
                        item,
                        reader.GetString(32))))
            {
                throw new InvalidDataException(
                    "The authoritative kit bag contains a duplicate slot.");
            }
        }

        var compactProjection = string.Join(
            '#',
            projection.Select(
                static item => item.ToCompactString())) + '#';
        return new LockedKitBag(compactProjection, items);
    }

    private async Task<GearMentorMaterialConversionExecutionResult>
        PersistCommittedResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            MaterialCommandContext context,
            long currentInventoryRevision,
            GearMentorResult plan,
            LockedKitBag lockedBag,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        if (plan.Outputs.Count != 1)
        {
            throw new InvalidDataException(
                "A material conversion must produce exactly one output " +
                "definition.");
        }

        var inventoryRevision =
            checked(currentInventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            GearMentorMaterialConversionResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var expected = CompactItemEntry.Parse(
            context.ExpectedCompactItemState);
        var output = plan.Outputs[0];
        ResolveReceiptItems(
            context,
            GearMentorMaterialConversionResultStatus.Succeeded,
            out var sourceItemId,
            out var outputItemId,
            out var outputQuantity,
            out var isBound);
        if (sourceItemId != expected.Id ||
            outputItemId != output.ItemId ||
            outputQuantity != output.Quantity ||
            isBound != (output.Bound != 0))
        {
            throw new InvalidDataException(
                "The committed material plan differs from its recipe.");
        }

        var receipt =
            new GearMentorMaterialConversionExecutionReceipt(
                context.Family,
                context.Subject.CharacterId,
                GearMentorMaterialConversionResultStatus.Succeeded,
                GearMentorMaterialConversionNativeResults.GetResultSubId(
                    context.Family,
                    GearMentorMaterialConversionResultStatus.Succeeded),
                context.SelectedKitBagSlot,
                sourceItemId,
                outputItemId,
                outputQuantity,
                isBound,
                inventoryRevision,
                auditId.ToString(CultureInfo.InvariantCulture),
                eventId);
        var payload =
            GearMentorMaterialConversionPersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresGearMentorMaterialConversionCommandStage
                .AuditInserted,
            cancellationToken);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            context,
            GearMentorMaterialConversionResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            GearMentorMaterialConversionPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorMaterialConversionCommandStage
                .InboxInserted,
            cancellationToken);

        var mutations = await ApplyMutationsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            plan,
            lockedBag,
            cancellationToken);
        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            context,
            currentInventoryRevision,
            inventoryRevision,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorMaterialConversionCommandStage
                .InventoryMutated,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            context,
            inventoryRevision,
            mutations,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorMaterialConversionCommandStage
                .LedgerInserted,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            context.Family,
            inboxId,
            aggregateKey,
            inventoryRevision,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorMaterialConversionCommandStage
                .OutboxInserted,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorMaterialConversionCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresGearMentorMaterialConversionCommandStage.AfterCommit,
            cancellationToken);
        return GearMentorMaterialConversionExecutionResult
            .Committed(receipt);
    }

    private async Task<IReadOnlyList<InventoryMutation>>
        ApplyMutationsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            GearMentorResult plan,
            LockedKitBag lockedBag,
            CancellationToken cancellationToken)
    {
        if (!plan.Committed || plan.Mutations.Count == 0)
        {
            throw new InvalidDataException(
                "A committed material conversion must mutate inventory.");
        }

        var mutations = new List<InventoryMutation>(
            plan.Mutations.Count);
        foreach (var mutation in plan.Mutations)
        {
            lockedBag.Items.TryGetValue(
                checked((short)mutation.KitBagSlot),
                out var locked);
            var lockedItem = locked?.Item ?? CompactItemEntry.Empty;
            if (lockedItem != mutation.Before)
            {
                throw new InvalidDataException(
                    "The locked inventory differs from the validated " +
                    "material-conversion plan.");
            }

            mutations.Add(
                await ApplyMutationAsync(
                    connection,
                    transaction,
                    characterId,
                    mutation,
                    locked,
                    cancellationToken));
        }

        return mutations;
    }

    private async Task<InventoryMutation> ApplyMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        GearMentorSlotMutation mutation,
        LockedInventoryItem? locked,
        CancellationToken cancellationToken)
    {
        if (mutation.Before.IsEmpty)
        {
            if (locked is not null || mutation.After.IsEmpty)
            {
                throw new InvalidDataException(
                    "The material-conversion add mutation is invalid.");
            }

            return await InsertItemAsync(
                connection,
                transaction,
                characterId,
                checked((short)mutation.KitBagSlot),
                mutation.After,
                cancellationToken);
        }

        if (locked is null)
        {
            throw new InvalidDataException(
                "A locked material-conversion source is missing.");
        }

        if (mutation.After.IsEmpty)
        {
            return await DeleteItemAsync(
                connection,
                transaction,
                characterId,
                locked,
                cancellationToken);
        }

        return await UpdateItemAsync(
            connection,
            transaction,
            characterId,
            locked,
            mutation.After,
            cancellationToken);
    }

    private async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MaterialCommandContext context,
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
        command.Parameters.AddWithValue("nextRevision", nextRevision);
        command.Parameters.AddWithValue(
            "expectedRevision",
            expectedRevision);
        command.Parameters.AddWithValue(
            "accountId",
            context.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            context.Subject.CharacterId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The material-conversion inventory revision did not " +
                "advance exactly once.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        MaterialCommandContext context,
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
                context.Subject.AccountId);
            command.Parameters.AddWithValue(
                "characterId",
                context.Subject.CharacterId);
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
                GearMentorMaterialConversionPersistenceCodec
                    .LedgerReasonCode(context.Family));
            if (await command.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The material-conversion ledger append was not " +
                    "exact.");
            }
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
