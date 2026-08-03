using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static partial class ClassSuitConversionPlanner
{
    private static readonly IReadOnlySet<int> ClassSpecificAttributeIds =
        new HashSet<int>
        {
            200, 201,
            210, 211,
            220, 221,
            230, 231
        };

    private static bool TryPlanForward(
        IItemTemplateCatalog templates,
        CompactItemEntry[] working,
        ClassSuitConversionRequest request,
        CompactItemEntry equipment,
        ItemTemplateDefinition sourceTemplate,
        byte profession,
        int playerLevel,
        List<ClassSuitMaterialChange> materials,
        out CompactItemEntry equipmentAfter,
        out ClassSuitConversionStatus status,
        out string reason)
    {
        var targetTier = TargetTierFor(request.Operation);
        if (!ClassSuitConversionCatalog.TryResolveForward(
                profession,
                equipment.Id,
                targetTier,
                out var rule))
        {
            return FailSource(
                equipment,
                profession,
                isReverse: false,
                out equipmentAfter,
                out status,
                out reason);
        }

        if (HasLegacyOrdinaryClassAttributes(equipment) ||
            (rule.SourceTier != ClassSuitTier.TierIII &&
             (equipment.ClassAttribute1.HasValue ||
              equipment.ClassAttribute2.HasValue)))
        {
            return Fail(
                equipment,
                ClassSuitConversionStatus.InvalidEquipment,
                "Class Suit attributes are valid only on Class Suit III/IV gear; repair the persisted item before upgrading it.",
                out equipmentAfter,
                out status,
                out reason);
        }

        if (!TryValidateTarget(
                templates,
                sourceTemplate,
                rule.TargetItemId,
                profession,
                playerLevel,
                out var targetStatus,
                out var targetReason))
        {
            return Fail(
                equipment,
                targetStatus,
                targetReason,
                out equipmentAfter,
                out status,
                out reason);
        }

        var insigniaSelection = request.Insignia!;
        var insignia = working[insigniaSelection.KitBagSlot];
        if (insignia.Id != rule.InsigniaItemId)
        {
            return Fail(
                equipment,
                ClassSuitConversionStatus.InvalidInsignia,
                $"Promotional Insignia {(int)targetTier} is required for this conversion.",
                out equipmentAfter,
                out status,
                out reason);
        }

        if (insignia.Stack < rule.InsigniaQuantity)
        {
            return Fail(
                equipment,
                ClassSuitConversionStatus.InsufficientInsignia,
                $"This equipment requires {rule.InsigniaQuantity} matching Promotional Insignia(s).",
                out equipmentAfter,
                out status,
                out reason);
        }

        var resultingBound = Math.Max(equipment.Bound, insignia.Bound);
        equipmentAfter = equipment with
        {
            Id = rule.TargetItemId,
            Bound = resultingBound,
            // Only the Tier III -> Tier IV path may carry canonical Class Suit
            // attributes forward. Earlier tiers are rejected above if they
            // contain such state, so conversion never silently drops value.
            ClassAttribute1 = targetTier == ClassSuitTier.TierIV
                ? equipment.ClassAttribute1
                : null,
            ClassAttribute2 = targetTier == ClassSuitTier.TierIV
                ? equipment.ClassAttribute2
                : null
        };
        working[insigniaSelection.KitBagSlot] = Consume(
            insignia,
            rule.InsigniaQuantity);
        materials.Add(new ClassSuitMaterialChange(
            rule.InsigniaItemId,
            rule.InsigniaQuantity,
            insignia.Bound,
            ClassSuitMaterialDirection.Consumed));
        status = ClassSuitConversionStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool TryPlanReverse(
        IItemTemplateCatalog templates,
        CompactItemEntry[] working,
        CompactItemEntry equipment,
        ItemTemplateDefinition sourceTemplate,
        byte profession,
        List<ClassSuitMaterialChange> materials,
        out CompactItemEntry equipmentAfter,
        out ClassSuitConversionStatus status,
        out string reason)
    {
        if (!ClassSuitConversionCatalog.TryResolveReverse(
                profession,
                equipment.Id,
                out var rule))
        {
            return FailSource(
                equipment,
                profession,
                isReverse: true,
                out equipmentAfter,
                out status,
                out reason);
        }

        // A reverse conversion cannot raise the level requirement, but the
        // destination still must be a real same-slot equipment template.
        if (!TryValidateTarget(
                templates,
                sourceTemplate,
                rule.CommonItemId,
                profession,
                playerLevel: int.MaxValue,
                out var targetStatus,
                out var targetReason))
        {
            return Fail(
                equipment,
                targetStatus,
                targetReason,
                out equipmentAfter,
                out status,
                out reason);
        }

        equipmentAfter = StripClassSpecificAttributes(
            equipment with
            {
                Id = rule.CommonItemId,
                Bound = equipment.Bound
            });
        foreach (var refund in rule.Refunds)
        {
            if (!TryAddMaterial(
                    working,
                    refund.ItemId,
                    refund.Quantity,
                    equipment.Bound))
            {
                return Fail(
                    equipment,
                    ClassSuitConversionStatus.InsufficientCapacity,
                    "The returned Promotional Insignias do not fit in the kit bag.",
                    out equipmentAfter,
                    out status,
                    out reason);
            }

            materials.Add(new ClassSuitMaterialChange(
                refund.ItemId,
                refund.Quantity,
                equipment.Bound,
                ClassSuitMaterialDirection.Granted));
        }

        status = ClassSuitConversionStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool TryValidateTarget(
        IItemTemplateCatalog templates,
        ItemTemplateDefinition sourceTemplate,
        uint targetItemId,
        byte profession,
        int playerLevel,
        out ClassSuitConversionStatus status,
        out string reason)
    {
        if (!templates.TryGet(targetItemId, out var targetTemplate) ||
            !EquipmentSlots.IsEquipmentKind(targetTemplate.Kind) ||
            !EquipmentSlots.IsEquipmentSlot(targetTemplate.EquipmentSlot) ||
            targetTemplate.EquipmentSlot != sourceTemplate.EquipmentSlot ||
            targetTemplate.ClassIds.Count == 0 ||
            !targetTemplate.ClassIds.Contains((short)profession) ||
            !targetTemplate.MinLevel.HasValue)
        {
            status = ClassSuitConversionStatus.ContentMismatch;
            reason = "The pinned item catalog does not contain a compatible Class Suit conversion target.";
            return false;
        }

        if (playerLevel < targetTemplate.MinLevel.Value)
        {
            status = ClassSuitConversionStatus.PlayerLevelTooLow;
            reason = $"Character level {targetTemplate.MinLevel.Value} is required for this Class Suit.";
            return false;
        }

        status = ClassSuitConversionStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool FailSource(
        CompactItemEntry equipment,
        byte profession,
        bool isReverse,
        out CompactItemEntry equipmentAfter,
        out ClassSuitConversionStatus status,
        out string reason)
    {
        var belongsToDifferentProfession = Enumerable.Range(0, 4)
            .Where(value => value != profession)
            .Any(value => ClassSuitConversionCatalog.TryResolveSuit(
                checked((byte)value),
                equipment.Id,
                out _,
                out _));
        if (belongsToDifferentProfession)
        {
            return Fail(
                equipment,
                ClassSuitConversionStatus.ProfessionMismatch,
                "This Class Suit belongs to a different profession.",
                out equipmentAfter,
                out status,
                out reason);
        }

        return Fail(
            equipment,
            ClassSuitConversionStatus.UnsupportedSource,
            isReverse
                ? "Only Class Suit I through IV equipment can be converted to common equipment."
                : "The selected equipment is not the required source for this Class Suit tier.",
            out equipmentAfter,
            out status,
            out reason);
    }

    private static ClassSuitTier TargetTierFor(
        ClassSuitConversionOperation operation) => operation switch
    {
        ClassSuitConversionOperation.ExchangeTierI => ClassSuitTier.TierI,
        ClassSuitConversionOperation.UpgradeTierII => ClassSuitTier.TierII,
        ClassSuitConversionOperation.UpgradeTierIII => ClassSuitTier.TierIII,
        ClassSuitConversionOperation.UpgradeTierIV => ClassSuitTier.TierIV,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static CompactItemEntry StripClassSpecificAttributes(
        CompactItemEntry equipment)
    {
        var attributes = new[]
        {
            equipment.Attribute1,
            equipment.Attribute2,
            equipment.Attribute3,
            equipment.Attribute4,
            equipment.Attribute5
        };
        var levels = new[]
        {
            equipment.AttributeLevel1,
            equipment.AttributeLevel2,
            equipment.AttributeLevel3,
            equipment.AttributeLevel4,
            equipment.AttributeLevel5
        };
        var keptAttributes = new List<int?>(5);
        var keptLevels = new List<short?>(5);
        for (var index = 0; index < attributes.Length; index++)
        {
            if (!attributes[index].HasValue ||
                ClassSpecificAttributeIds.Contains(attributes[index]!.Value))
            {
                continue;
            }

            keptAttributes.Add(attributes[index]);
            keptLevels.Add(levels[index]);
        }

        while (keptAttributes.Count < 5)
        {
            keptAttributes.Add(null);
            keptLevels.Add(null);
        }

        return equipment with
        {
            Attribute1 = keptAttributes[0],
            Attribute2 = keptAttributes[1],
            Attribute3 = keptAttributes[2],
            Attribute4 = keptAttributes[3],
            Attribute5 = keptAttributes[4],
            AttributeLevel1 = keptLevels[0],
            AttributeLevel2 = keptLevels[1],
            AttributeLevel3 = keptLevels[2],
            AttributeLevel4 = keptLevels[3],
            AttributeLevel5 = keptLevels[4],
            ClassAttribute1 = null,
            ClassAttribute2 = null
        };
    }

    private static bool HasLegacyOrdinaryClassAttributes(
        CompactItemEntry equipment) =>
        new[]
        {
            equipment.Attribute1,
            equipment.Attribute2,
            equipment.Attribute3,
            equipment.Attribute4,
            equipment.Attribute5
        }.Any(value =>
            value.HasValue &&
            ClassSpecificAttributeIds.Contains(value.Value));

    private static bool TryAddMaterial(
        CompactItemEntry[] working,
        uint itemId,
        int quantity,
        short bound)
    {
        var remaining = quantity;
        for (var slot = 0; slot < working.Length && remaining > 0; slot++)
        {
            var item = working[slot];
            if (item.Id != itemId ||
                item.Bound != bound ||
                item.Stack >= InsigniaStackCap)
            {
                continue;
            }

            var added = Math.Min(remaining, InsigniaStackCap - item.Stack);
            working[slot] = item with
            {
                Stack = checked((short)(item.Stack + added))
            };
            remaining -= added;
        }

        for (var slot = 0; slot < working.Length && remaining > 0; slot++)
        {
            if (!working[slot].IsEmpty)
            {
                continue;
            }

            var added = Math.Min(remaining, InsigniaStackCap);
            working[slot] = CompactItemEntry.Empty with
            {
                Id = itemId,
                Quality = 1,
                Grade = 1,
                Bound = bound,
                Stack = checked((short)added)
            };
            remaining -= added;
        }

        return remaining == 0;
    }

    private static CompactItemEntry Consume(
        CompactItemEntry item,
        int quantity)
    {
        var remaining = item.Stack - quantity;
        return remaining == 0
            ? CompactItemEntry.Empty
            : item with { Stack = checked((short)remaining) };
    }

    private static bool Fail(
        CompactItemEntry equipment,
        ClassSuitConversionStatus failureStatus,
        string failureReason,
        out CompactItemEntry equipmentAfter,
        out ClassSuitConversionStatus status,
        out string reason)
    {
        equipmentAfter = equipment;
        status = failureStatus;
        reason = failureReason;
        return false;
    }

    private static ClassSuitConversionResult Reject(
        ClassSuitConversionStatus status,
        ClassSuitConversionOperation? operation,
        string originalKitBag,
        CompactItemEntry equipment,
        string reason) =>
        new(
            status,
            operation,
            originalKitBag,
            originalKitBag,
            equipment,
            equipment,
            [],
            [],
            reason);
}
