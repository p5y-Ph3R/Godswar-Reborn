using System.Globalization;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresGearMentorDecomposeCommandExecutor
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
            var slot = reader.GetInt16(1);
            var item = ReadCompactItem(reader);
            projection[slot] = item;
            if (!items.TryAdd(
                    slot,
                    new LockedInventoryItem(
                        reader.GetInt64(0),
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

    private async Task<GearMentorDecomposeGearExecutionResult>
        PersistCommittedResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            DecomposeCommandContext context,
            long currentInventoryRevision,
            GearMentorResult plan,
            LockedKitBag lockedBag,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        if (plan.Outputs.Count != context.Selections.Length)
        {
            throw new InvalidDataException(
                "Decompose must produce one Dust outcome per selected " +
                "gear item.");
        }

        var receiptSelections = CreateReceiptSelections(context);
        var dustOutcomes =
            new GearMentorDecomposeDustOutcome[plan.Outputs.Count];
        for (var index = 0; index < plan.Outputs.Count; index++)
        {
            var output = plan.Outputs[index];
            var expected = CompactItemEntry.Parse(
                context.Selections[index].ExpectedCompactItemState);
            if (!_itemContent.Templates.Materials.TryGetDust(
                    output.ItemId,
                    out _) ||
                output.Quantity is
                    < 1 or
                    > GearMentorDecomposeGearExecutionReceipt
                        .MaximumDustQuantity ||
                output.Bound != expected.Bound)
            {
                throw new InvalidDataException(
                    "The committed Decompose plan produced invalid Dust.");
            }

            dustOutcomes[index] = new GearMentorDecomposeDustOutcome(
                context.Selections[index].SelectedKitBagSlot,
                output.ItemId,
                output.Quantity,
                output.Bound);
        }

        var inventoryRevision =
            checked(currentInventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            GearMentorDecomposeGearResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt =
            new GearMentorDecomposeGearExecutionReceipt(
                context.Subject.CharacterId,
                GearMentorDecomposeGearResultStatus.Succeeded,
                GearMentorDecomposeGearNativeResults.GetResultSubId(
                    GearMentorDecomposeGearResultStatus.Succeeded),
                receiptSelections,
                dustOutcomes,
                inventoryRevision,
                auditId.ToString(CultureInfo.InvariantCulture),
                eventId);
        var payload =
            GearMentorDecomposePersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresGearMentorDecomposeCommandStage.AuditInserted,
            cancellationToken);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            GearMentorDecomposeGearResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            GearMentorDecomposePersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorDecomposeCommandStage.InboxInserted,
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
            PostgresGearMentorDecomposeCommandStage.InventoryMutated,
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
            PostgresGearMentorDecomposeCommandStage.LedgerInserted,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            inventoryRevision,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorDecomposeCommandStage.OutboxInserted,
            cancellationToken);
        await ReachAsync(
            PostgresGearMentorDecomposeCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresGearMentorDecomposeCommandStage.AfterCommit,
            cancellationToken);
        return GearMentorDecomposeGearExecutionResult.Committed(receipt);
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
                "A committed Decompose command must mutate inventory.");
        }

        var mutations = new List<InventoryMutation>(
            plan.Mutations.Count);
        var previousSlot = -1;
        foreach (var mutation in plan.Mutations)
        {
            if (mutation.KitBagSlot <= previousSlot)
            {
                throw new InvalidDataException(
                    "Decompose mutations must be in strict slot order.");
            }

            previousSlot = mutation.KitBagSlot;
            lockedBag.Items.TryGetValue(
                checked((short)mutation.KitBagSlot),
                out var locked);
            var lockedItem = locked?.Item ?? CompactItemEntry.Empty;
            if (lockedItem != mutation.Before)
            {
                throw new InvalidDataException(
                    "The locked inventory differs from the validated " +
                    "Decompose plan.");
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
                    "The Decompose add mutation is invalid.");
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
                "A locked Decompose source is missing.");
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
        DecomposeCommandContext context,
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
                "The Decompose inventory revision did not advance " +
                "exactly once.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        DecomposeCommandContext context,
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
                GearMentorDecomposePersistenceCodec.LedgerReasonCode);
            if (await command.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Decompose ledger append was not exact.");
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
