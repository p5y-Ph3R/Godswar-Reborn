namespace Godswar.Server.State;

internal sealed record ForgeSlotMutation(
    int Slot,
    CompactItemEntry Before,
    CompactItemEntry After);

internal sealed record ForgePersistencePlan(
    EquipmentForgeCalculation Calculation,
    bool Succeeded,
    int UpdatedSilver,
    string UpdatedKitBag,
    IReadOnlyList<ForgeSlotMutation> Mutations);

internal static class ForgePersistencePlanner
{
    private const int KitBagSlotCount = 96;

    public static bool TryCreate(
        string kitBag,
        int silver,
        ForgeTransactionRequest? request,
        int roll,
        out ForgePersistencePlan? plan,
        out ForgeTransactionStatus rejectionStatus,
        out string rejectionReason)
    {
        plan = null;
        rejectionStatus = ForgeTransactionStatus.InvalidSelection;
        rejectionReason = string.Empty;

        if (request is null)
        {
            rejectionReason = "Forge request was missing.";
            return false;
        }

        var oddsSelections = request.OddsMaterials;
        if (oddsSelections.Count > KitBagSlotCount - 2 ||
            oddsSelections.Any(selection => selection is null))
        {
            rejectionReason = "Forge request contained too many odds-crystal selections.";
            return false;
        }

        if (roll is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(roll), "Forge roll must be between 0 and 99.");
        }

        if (!IsValidSlot(request.Equipment.KitBagSlot) ||
            !IsValidSlot(request.PrimaryMaterial.KitBagSlot) ||
            oddsSelections.Any(selection => !IsValidSlot(selection.KitBagSlot)))
        {
            rejectionReason = "One or more forge selections used an invalid kit-bag slot.";
            return false;
        }

        var selectedSlots = new HashSet<int>
        {
            request.Equipment.KitBagSlot,
            request.PrimaryMaterial.KitBagSlot
        };
        if (selectedSlots.Count != 2 ||
            oddsSelections.Any(selection => !selectedSlots.Add(selection.KitBagSlot)))
        {
            rejectionReason = "Forge selections must use distinct kit-bag slots.";
            return false;
        }

        var totalOddsQuantity = 0;
        foreach (var oddsSelection in oddsSelections)
        {
            if (oddsSelection.Quantity is < 1 or > EquipmentForgeCalculator.MaximumOddsQuantity)
            {
                rejectionReason = "Forge selection quantities were outside the supported range.";
                return false;
            }

            if (totalOddsQuantity >
                EquipmentForgeCalculator.MaximumOddsQuantity - oddsSelection.Quantity)
            {
                rejectionReason = "Forge selection quantities were outside the supported range.";
                return false;
            }

            totalOddsQuantity += oddsSelection.Quantity;
        }

        if (request.Equipment.Quantity != 1 ||
            request.PrimaryMaterial.Quantity != 1 ||
            totalOddsQuantity > EquipmentForgeCalculator.MaximumOddsQuantity)
        {
            rejectionReason = "Forge selection quantities were outside the supported range.";
            return false;
        }

        var equipment = KitBagSlots.GetItem(kitBag, request.Equipment.KitBagSlot);
        var primaryMaterial = KitBagSlots.GetItem(kitBag, request.PrimaryMaterial.KitBagSlot);
        var oddsMaterials = oddsSelections
            .Select(selection => KitBagSlots.GetItem(kitBag, selection.KitBagSlot))
            .ToArray();

        if (equipment.IsEmpty || primaryMaterial.IsEmpty ||
            oddsMaterials.Any(item => item.IsEmpty))
        {
            rejectionStatus = ForgeTransactionStatus.StaleSelection;
            rejectionReason = "A selected forge item is no longer in its staged kit-bag slot.";
            return false;
        }

        if (equipment != request.Equipment.ExpectedItem ||
            primaryMaterial != request.PrimaryMaterial.ExpectedItem ||
            oddsSelections.Where((selection, index) =>
                    oddsMaterials[index] != selection.ExpectedItem)
                .Any())
        {
            rejectionStatus = ForgeTransactionStatus.StaleSelection;
            rejectionReason = "A selected forge item changed after it was staged.";
            return false;
        }

        if (primaryMaterial.Stack < request.PrimaryMaterial.Quantity ||
            oddsSelections.Where((selection, index) =>
                    oddsMaterials[index].Stack < selection.Quantity)
                .Any())
        {
            rejectionStatus = ForgeTransactionStatus.InsufficientMaterials;
            rejectionReason = "A selected forge-material stack is too small.";
            return false;
        }

        if (oddsMaterials.Length > 1 &&
            oddsMaterials.Skip(1).Any(item => item.Id != oddsMaterials[0].Id))
        {
            rejectionStatus = ForgeTransactionStatus.InvalidSelection;
            rejectionReason = "Odds-crystal selections must use the same material ID.";
            return false;
        }

        EquipmentForgeMaterialSelection? combinedOddsMaterial = null;
        if (oddsMaterials.Length > 0)
        {
            combinedOddsMaterial = new EquipmentForgeMaterialSelection(
                // Every source stack was independently revalidated above, so
                // the aggregate view only needs to expose the reserved total
                // to the probability calculator.
                oddsMaterials[0] with { Stack = checked((short)totalOddsQuantity) },
                totalOddsQuantity);
        }

        var calculationRequest = new EquipmentForgeRequest(
            equipment,
            new EquipmentForgeMaterialSelection(primaryMaterial, request.PrimaryMaterial.Quantity),
            combinedOddsMaterial);
        if (!EquipmentForgeCalculator.TryCalculate(
                calculationRequest,
                out var calculation,
                out var validationError))
        {
            rejectionStatus = ForgeTransactionStatus.InvalidForge;
            rejectionReason = validationError.ToString();
            return false;
        }

        if (calculation!.SilverCost < 0)
        {
            rejectionStatus = ForgeTransactionStatus.InvalidForge;
            rejectionReason = "The selected forge rule has an invalid silver cost.";
            return false;
        }

        if (silver < calculation.SilverCost)
        {
            rejectionStatus = ForgeTransactionStatus.InsufficientSilver;
            rejectionReason = "The character does not have enough silver for this forge attempt.";
            return false;
        }

        var succeeded = roll < calculation.SuccessProbability;
        var equipmentAfter = succeeded
            ? calculation.SuccessEquipment
            : calculation.FailureEquipment;
        var primaryAfter = Consume(primaryMaterial, request.PrimaryMaterial.Quantity);
        var mutations = new List<ForgeSlotMutation>(2 + oddsSelections.Count)
        {
            new(request.Equipment.KitBagSlot, equipment, equipmentAfter),
            new(request.PrimaryMaterial.KitBagSlot, primaryMaterial, primaryAfter)
        };

        var updatedKitBag = KitBagSlots.SetSlot(
            kitBag,
            request.Equipment.KitBagSlot,
            equipmentAfter.ToCompactString());
        updatedKitBag = SetOrClear(
            updatedKitBag,
            request.PrimaryMaterial.KitBagSlot,
            primaryAfter);

        for (var index = 0; index < oddsSelections.Count; index++)
        {
            var oddsSelection = oddsSelections[index];
            var oddsMaterial = oddsMaterials[index];
            var oddsAfter = Consume(oddsMaterial, oddsSelection.Quantity);
            mutations.Add(new ForgeSlotMutation(
                oddsSelection.KitBagSlot,
                oddsMaterial,
                oddsAfter));
            updatedKitBag = SetOrClear(
                updatedKitBag,
                oddsSelection.KitBagSlot,
                oddsAfter);
        }

        plan = new ForgePersistencePlan(
            calculation,
            succeeded,
            checked(silver - calculation.SilverCost),
            updatedKitBag,
            mutations);
        rejectionReason = string.Empty;
        return true;
    }

    private static bool IsValidSlot(int slot)
    {
        return slot is >= 0 and < KitBagSlotCount;
    }

    private static CompactItemEntry Consume(CompactItemEntry item, int quantity)
    {
        var remaining = item.Stack - quantity;
        return remaining == 0
            ? CompactItemEntry.Empty
            : item with { Stack = checked((short)remaining) };
    }

    private static string SetOrClear(string kitBag, int slot, CompactItemEntry item)
    {
        return item.IsEmpty
            ? KitBagSlots.ClearSlot(kitBag, slot)
            : KitBagSlots.SetSlot(kitBag, slot, item.ToCompactString());
    }
}
