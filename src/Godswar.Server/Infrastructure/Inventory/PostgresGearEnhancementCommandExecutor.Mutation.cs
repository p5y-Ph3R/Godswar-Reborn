using System.Globalization;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresGearEnhancementCommandExecutor
{
    private async Task<GearEnhancementExecutionResult>
        PersistCommittedResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GearEnhancementCommandContext context,
            long currentInventoryRevision,
            GearEnhancementResult plan,
            LockedKitBag lockedBag,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var orderedPlan = ValidateAndOrderPlan(
            context.Command,
            plan,
            lockedBag);
        var receiptMutations = orderedPlan
            .Select(static item =>
                new GearEnhancementReceiptMutation(
                    item.Role,
                    item.Plan.KitBagSlot,
                    item.Plan.Before.Id,
                    item.Plan.Before.ToCompactString(),
                    item.Plan.After.ToCompactString()))
            .ToArray();

        var inventoryRevision =
            checked(currentInventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            GearEnhancementCommandResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new GearEnhancementExecutionReceipt(
            context.Subject.CharacterId,
            context.Command.Operation,
            context.Command.NpcId,
            context.Command.DialogIndex,
            GearEnhancementCommandResultStatus.Succeeded,
            GearEnhancementNativeResults.GetResultSubId(
                context.Command.Operation,
                GearEnhancementCommandResultStatus.Succeeded),
            receiptMutations,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload = GearEnhancementPersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresGearEnhancementCommandStage.AuditInserted,
            cancellationToken);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            context.Family,
            GearEnhancementCommandResultStatus.Succeeded,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            GearEnhancementPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearEnhancementCommandStage.InboxInserted,
            cancellationToken);

        var mutations = await ApplyMutationsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            orderedPlan,
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
            PostgresGearEnhancementCommandStage
                .InventoryRevisionAdvanced,
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
            PostgresGearEnhancementCommandStage.LedgerInserted,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            context.Family,
            aggregateKey,
            inventoryRevision,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresGearEnhancementCommandStage.OutboxInserted,
            cancellationToken);
        await ReachAsync(
            PostgresGearEnhancementCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresGearEnhancementCommandStage.AfterCommit,
            cancellationToken);
        return GearEnhancementExecutionResult.Committed(receipt);
    }

    private static IReadOnlyList<OrderedPlanMutation>
        ValidateAndOrderPlan(
            GearEnhancementCommand command,
            GearEnhancementResult plan,
            LockedKitBag lockedBag)
    {
        if (!plan.Committed ||
            plan.Operation != MapOperation(command.Operation) ||
            plan.Mutations.Count != 3)
        {
            throw new InvalidDataException(
                "A successful Gear Enhancement plan must contain exactly " +
                "three mutations for its requested operation.");
        }

        var bySlot = plan.Mutations.ToDictionary(
            static mutation => mutation.KitBagSlot);
        if (bySlot.Count != 3)
        {
            throw new InvalidDataException(
                "The Gear Enhancement plan contains duplicate slots.");
        }

        var ordered = new[]
        {
            CreateOrderedMutation(command.Gear, bySlot, lockedBag),
            CreateOrderedMutation(command.Catalyst, bySlot, lockedBag),
            CreateOrderedMutation(
                command.AttributeStone,
                bySlot,
                lockedBag)
        };
        if (ordered[0].Plan.After.IsEmpty ||
            ordered[0].Plan.After.Stack != 1 ||
            ordered[0].Plan.Before != plan.EquipmentBefore ||
            ordered[0].Plan.After != plan.EquipmentAfter ||
            !ConsumesExactlyOne(ordered[1].Plan) ||
            !ConsumesExactlyOne(ordered[2].Plan))
        {
            throw new InvalidDataException(
                "The Gear Enhancement plan violates item-role semantics.");
        }

        return ordered;
    }

    private static bool ConsumesExactlyOne(
        GearEnhancementSlotMutation mutation)
    {
        if (mutation.Before.IsEmpty || mutation.Before.Stack < 1)
        {
            return false;
        }

        var expected = mutation.Before.Stack == 1
            ? CompactItemEntry.Empty
            : mutation.Before with
            {
                Stack = checked((short)(mutation.Before.Stack - 1))
            };
        return mutation.After == expected;
    }

    private static OrderedPlanMutation CreateOrderedMutation(
        GearEnhancementCommandSelection selection,
        IReadOnlyDictionary<int, GearEnhancementSlotMutation> bySlot,
        LockedKitBag lockedBag)
    {
        if (!bySlot.TryGetValue(selection.KitBagSlot, out var mutation) ||
            mutation.Before.IsEmpty ||
            mutation.Before != CompactItemEntry.Parse(
                selection.ExpectedCompactItemState) ||
            !lockedBag.Items.TryGetValue(
                checked((short)selection.KitBagSlot),
                out var locked) ||
            locked.Item != mutation.Before)
        {
            throw new InvalidDataException(
                "The locked inventory differs from the validated Gear " +
                "Enhancement plan.");
        }

        return new OrderedPlanMutation(
            selection.Role,
            mutation);
    }

    private async Task<IReadOnlyList<InventoryMutation>>
        ApplyMutationsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            IReadOnlyList<OrderedPlanMutation> orderedPlan,
            LockedKitBag lockedBag,
            CancellationToken cancellationToken)
    {
        var mutations =
            new List<InventoryMutation>(orderedPlan.Count);
        foreach (var ordered in orderedPlan)
        {
            var slot = checked((short)ordered.Plan.KitBagSlot);
            if (!lockedBag.Items.TryGetValue(slot, out var locked))
            {
                throw new InvalidDataException(
                    "A locked Gear Enhancement source is missing.");
            }

            var mutation =
                ordered.Plan.After.IsEmpty
                    ? await DeleteItemAsync(
                        connection,
                        transaction,
                        characterId,
                        ordered.Role,
                        locked,
                        cancellationToken)
                    : await UpdateItemAsync(
                        connection,
                        transaction,
                        characterId,
                        ordered.Role,
                        locked,
                        ordered.Plan.After,
                        cancellationToken);
            mutations.Add(mutation);
            await ReachAsync(
                MutationStage(ordered.Role),
                cancellationToken);
        }

        return mutations;
    }

    private static PostgresGearEnhancementCommandStage MutationStage(
        GearEnhancementCommandItemRole role) =>
        role switch
        {
            GearEnhancementCommandItemRole.Gear =>
                PostgresGearEnhancementCommandStage.GearMutated,
            GearEnhancementCommandItemRole.Catalyst =>
                PostgresGearEnhancementCommandStage.CatalystMutated,
            GearEnhancementCommandItemRole.AttributeStone =>
                PostgresGearEnhancementCommandStage
                    .AttributeStoneMutated,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

    private async Task AdvanceInventoryRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GearEnhancementCommandContext context,
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
                "The Gear Enhancement inventory revision did not advance " +
                "exactly once.");
        }
    }

    private async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        GearEnhancementCommandContext context,
        long inventoryRevision,
        IReadOnlyList<InventoryMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (mutations.Count != 3 ||
            mutations[0].Role != GearEnhancementCommandItemRole.Gear ||
            mutations[1].Role !=
                GearEnhancementCommandItemRole.Catalyst ||
            mutations[2].Role !=
                GearEnhancementCommandItemRole.AttributeStone)
        {
            throw new InvalidDataException(
                "Gear Enhancement ledger evidence is not in role order.");
        }

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
                GearEnhancementPersistenceCodec.LedgerReasonCode(
                    context.Family));
            if (await command.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The Gear Enhancement ledger append was not exact.");
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

    private sealed record OrderedPlanMutation(
        GearEnhancementCommandItemRole Role,
        GearEnhancementSlotMutation Plan);
}
