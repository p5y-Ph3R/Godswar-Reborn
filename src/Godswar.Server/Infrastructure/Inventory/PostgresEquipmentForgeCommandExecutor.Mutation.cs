using System.Globalization;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresEquipmentForgeCommandExecutor
{
    private async Task<EquipmentForgeExecutionResult>
        PersistCommittedResultAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            EquipmentForgeCommandContext context,
            LockedCharacter character,
            ForgePersistencePlan plan,
            int roll,
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
        var status = plan.Succeeded
            ? EquipmentForgeCommandResultStatus.Succeeded
            : EquipmentForgeCommandResultStatus.FailedRoll;
        var inventoryRevision =
            checked(character.InventoryRevision + 1);
        var chargesSilver = plan.Calculation.SilverCost > 0;
        var walletRevision = chargesSilver
            ? checked(character.WalletRevision + 1)
            : character.WalletRevision;
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            context,
            status,
            plan,
            roll,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new EquipmentForgeExecutionReceipt(
            context.Subject.CharacterId,
            status,
            (int)plan.Calculation.Operation,
            roll,
            plan.Calculation.SuccessProbability,
            plan.Calculation.SilverCost,
            orderedPlan.Equipment.Plan.Before.ToCompactString(),
            orderedPlan.Equipment.Plan.After.ToCompactString(),
            orderedPlan.Materials.Select(
                static item =>
                    new EquipmentForgeReceiptMaterial(
                        item.Role,
                        item.Plan.Slot,
                        item.Plan.Before.Id,
                        item.Quantity,
                        item.Plan.Before.Stack,
                        item.Plan.After.Stack))
                .ToArray(),
            walletRevision,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload = EquipmentForgePersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage.AuditInserted,
            ordinal: -1,
            cancellationToken);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            status,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            EquipmentForgePersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage.InboxInserted,
            ordinal: -1,
            cancellationToken);

        var mutations = await ApplyMutationsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            orderedPlan,
            lockedBag,
            cancellationToken);
        if (chargesSilver)
        {
            await UpdateWalletAsync(
                connection,
                transaction,
                context,
                character,
                plan.UpdatedSilver,
                walletRevision,
                cancellationToken);
            await ReachAsync(
                PostgresEquipmentForgeCommandStage.WalletUpdated,
                ordinal: -1,
                cancellationToken);
        }

        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            context,
            character.InventoryRevision,
            inventoryRevision,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage
                .InventoryRevisionAdvanced,
            ordinal: -1,
            cancellationToken);
        if (chargesSilver)
        {
            await InsertCurrencyLedgerAsync(
                connection,
                transaction,
                inboxId,
                context,
                character,
                plan.UpdatedSilver,
                walletRevision,
                cancellationToken);
            await ReachAsync(
                PostgresEquipmentForgeCommandStage
                    .CurrencyLedgerInserted,
                ordinal: -1,
                cancellationToken);
        }

        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            context,
            inventoryRevision,
            mutations,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage
                .InventoryLedgerInserted,
            ordinal: -1,
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
            PostgresEquipmentForgeCommandStage.OutboxInserted,
            ordinal: -1,
            cancellationToken);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage.BeforeCommit,
            ordinal: -1,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresEquipmentForgeCommandStage.AfterCommit,
            ordinal: -1,
            cancellationToken);
        return EquipmentForgeExecutionResult.Committed(receipt);
    }

    private static OrderedForgePlan ValidateAndOrderPlan(
        EquipmentForgeCommand command,
        ForgePersistencePlan plan,
        LockedKitBag lockedBag)
    {
        var expectedCount = 2 + command.OddsMaterials.Length;
        if (plan.Mutations.Count != expectedCount)
        {
            throw new InvalidDataException(
                "The equipment-forge plan has an invalid mutation count.");
        }
        var bySlot = plan.Mutations.ToDictionary(
            static mutation => mutation.Slot);
        if (bySlot.Count != expectedCount)
        {
            throw new InvalidDataException(
                "The equipment-forge plan contains duplicate slots.");
        }

        var equipment = CreateOrderedMutation(
            command.Equipment,
            bySlot,
            lockedBag);
        if (equipment.Plan.Before.Stack != 1 ||
            equipment.Plan.After.IsEmpty ||
            equipment.Plan.After.Stack != 1 ||
            plan.Succeeded == (equipment.Plan.Before ==
                equipment.Plan.After))
        {
            throw new InvalidDataException(
                "The equipment-forge equipment mutation is invalid.");
        }

        var materialSelections =
            new[] { command.PrimaryMaterial }
                .Concat(command.OddsMaterials)
                .ToArray();
        var materials = materialSelections
            .Select(selection =>
            {
                var mutation = CreateOrderedMutation(
                    selection,
                    bySlot,
                    lockedBag);
                if (!ConsumesExactly(
                        mutation.Plan,
                        selection.Quantity))
                {
                    throw new InvalidDataException(
                        "An equipment-forge material mutation is invalid.");
                }

                return mutation;
            })
            .ToArray();
        return new OrderedForgePlan(equipment, materials);
    }

    private static OrderedPlanMutation CreateOrderedMutation(
        EquipmentForgeCommandSelection selection,
        IReadOnlyDictionary<int, ForgeSlotMutation> bySlot,
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
                "The locked inventory differs from the validated forge plan.");
        }

        return new OrderedPlanMutation(
            selection.Role,
            selection.Quantity,
            mutation);
    }

    private static bool ConsumesExactly(
        ForgeSlotMutation mutation,
        int quantity)
    {
        if (mutation.Before.IsEmpty ||
            quantity <= 0 ||
            mutation.Before.Stack < quantity)
        {
            return false;
        }

        var expected = mutation.Before.Stack == quantity
            ? CompactItemEntry.Empty
            : mutation.Before with
            {
                Stack = checked(
                    (short)(mutation.Before.Stack - quantity))
            };
        return mutation.After == expected;
    }

    private async Task<IReadOnlyList<InventoryMutation>>
        ApplyMutationsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int characterId,
            OrderedForgePlan orderedPlan,
            LockedKitBag lockedBag,
            CancellationToken cancellationToken)
    {
        var mutations =
            new List<InventoryMutation>(
                orderedPlan.Materials.Count + 1);
        if (orderedPlan.Equipment.Plan.Before !=
            orderedPlan.Equipment.Plan.After)
        {
            mutations.Add(await ApplyMutationAsync(
                connection,
                transaction,
                characterId,
                orderedPlan.Equipment,
                lockedBag,
                cancellationToken));
            await ReachAsync(
                PostgresEquipmentForgeCommandStage.EquipmentMutated,
                ordinal: 0,
                cancellationToken);
        }

        for (var index = 0;
             index < orderedPlan.Materials.Count;
             index++)
        {
            mutations.Add(await ApplyMutationAsync(
                connection,
                transaction,
                characterId,
                orderedPlan.Materials[index],
                lockedBag,
                cancellationToken));
            await ReachAsync(
                PostgresEquipmentForgeCommandStage.MaterialMutated,
                index,
                cancellationToken);
        }

        return mutations;
    }

    private async Task<InventoryMutation> ApplyMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        OrderedPlanMutation ordered,
        LockedKitBag lockedBag,
        CancellationToken cancellationToken)
    {
        var slot = checked((short)ordered.Plan.Slot);
        if (!lockedBag.Items.TryGetValue(slot, out var locked))
        {
            throw new InvalidDataException(
                "A locked equipment-forge source is missing.");
        }

        return ordered.Plan.After.IsEmpty
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
    }

    private sealed record OrderedPlanMutation(
        EquipmentForgeCommandItemRole Role,
        int Quantity,
        ForgeSlotMutation Plan);

    private sealed record OrderedForgePlan(
        OrderedPlanMutation Equipment,
        IReadOnlyList<OrderedPlanMutation> Materials);
}
