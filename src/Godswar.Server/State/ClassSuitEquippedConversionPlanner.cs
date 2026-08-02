using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal sealed record ClassSuitEquippedConversionRequest(
    ClassSuitConversionOperation Operation,
    int EquipmentSlot,
    CompactItemEntry ExpectedEquipment,
    ClassSuitSlotSelection? Insignia = null);

internal static partial class ClassSuitConversionPlanner
{
    public static ClassSuitConversionResult CreateForEquippedGear(
        IItemTemplateCatalog templates,
        string kitBag,
        byte profession,
        int playerLevel,
        CompactItemEntry authoritativeEquipment,
        ClassSuitEquippedConversionRequest? request)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var originalKitBag = kitBag ?? string.Empty;
        var normalizedKitBag = string.IsNullOrWhiteSpace(kitBag)
            ? GameDefaults.EmptyKitBag
            : kitBag;

        if (request is null)
        {
            return Reject(
                ClassSuitConversionStatus.RequestMissing,
                null,
                originalKitBag,
                authoritativeEquipment,
                "Class Suit conversion request was missing.");
        }
        if (!Enum.IsDefined(request.Operation))
        {
            return Reject(
                ClassSuitConversionStatus.UnsupportedOperation,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                "Class Suit conversion operation is not supported.");
        }
        if (profession > 3)
        {
            return Reject(
                ClassSuitConversionStatus.InvalidProfession,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                "The authoritative character profession is invalid.");
        }

        var isReverse = request.Operation ==
            ClassSuitConversionOperation.ConvertToCommon;
        if (isReverse != (request.Insignia is null))
        {
            return Reject(
                ClassSuitConversionStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                isReverse
                    ? "Converting to common equipment does not accept an insignia input."
                    : "Select the required Promotional Insignia stack.");
        }

        if (request.EquipmentSlot is < EquipmentSlots.Head or
                > EquipmentSlots.Mount ||
            request.Insignia is { KitBagSlot: < 0 or >= KitBagSlotCount })
        {
            return Reject(
                ClassSuitConversionStatus.InvalidKitBagSlot,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                "A Class Suit selection used an invalid equipment or kit-bag slot.");
        }

        var before = Enumerable.Range(0, KitBagSlotCount)
            .Select(slot => KitBagSlots.GetItem(normalizedKitBag, slot))
            .ToArray();
        if (authoritativeEquipment.IsEmpty ||
            request.Insignia is { } selectedInsignia &&
            before[selectedInsignia.KitBagSlot].IsEmpty)
        {
            return Reject(
                ClassSuitConversionStatus.SelectionMissing,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                "A selected Class Suit item is no longer available.");
        }
        if (authoritativeEquipment != request.ExpectedEquipment ||
            request.Insignia is { } expectedInsignia &&
            before[expectedInsignia.KitBagSlot] !=
                expectedInsignia.ExpectedItem)
        {
            return Reject(
                ClassSuitConversionStatus.StaleSelection,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                "A selected Class Suit item changed after it was staged.");
        }
        if (authoritativeEquipment.Stack != 1 ||
            !templates.TryGet(
                authoritativeEquipment.Id,
                out var sourceTemplate) ||
            !EquipmentSlots.IsEquipmentKind(sourceTemplate.Kind) ||
            EquipmentSlots.ResolveSlotForItem(
                templates,
                authoritativeEquipment.Id,
                request.EquipmentSlot) != request.EquipmentSlot)
        {
            return Reject(
                ClassSuitConversionStatus.InvalidEquipment,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                "Only genuine equipment in its authoritative equipped slot can be converted.");
        }

        var working = before.ToArray();
        var materials = new List<ClassSuitMaterialChange>();
        var plannerRequest = new ClassSuitConversionRequest(
            request.Operation,
            new ClassSuitSlotSelection(
                request.EquipmentSlot,
                request.ExpectedEquipment),
            request.Insignia);
        CompactItemEntry equipmentAfter;
        ClassSuitConversionStatus failureStatus;
        string failureReason;
        var planned = isReverse
            ? TryPlanReverse(
                templates,
                working,
                authoritativeEquipment,
                sourceTemplate,
                profession,
                materials,
                out equipmentAfter,
                out failureStatus,
                out failureReason)
            : TryPlanForward(
                templates,
                working,
                plannerRequest,
                authoritativeEquipment,
                sourceTemplate,
                profession,
                playerLevel,
                materials,
                out equipmentAfter,
                out failureStatus,
                out failureReason);
        if (!planned)
        {
            return Reject(
                failureStatus,
                request.Operation,
                originalKitBag,
                authoritativeEquipment,
                failureReason);
        }

        var updatedKitBag = normalizedKitBag;
        var mutations = new List<ClassSuitSlotMutation>();
        for (var slot = 0; slot < KitBagSlotCount; slot++)
        {
            if (before[slot] == working[slot])
            {
                continue;
            }

            updatedKitBag = working[slot].IsEmpty
                ? KitBagSlots.ClearSlot(updatedKitBag, slot)
                : KitBagSlots.SetSlot(
                    updatedKitBag,
                    slot,
                    working[slot].ToCompactString());
            mutations.Add(new ClassSuitSlotMutation(
                slot,
                before[slot],
                working[slot]));
        }

        return new ClassSuitConversionResult(
            ClassSuitConversionStatus.Succeeded,
            request.Operation,
            originalKitBag,
            updatedKitBag,
            authoritativeEquipment,
            equipmentAfter,
            mutations,
            materials);
    }
}
