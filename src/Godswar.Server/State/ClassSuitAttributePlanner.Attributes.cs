namespace Godswar.Server.State;

internal static partial class ClassSuitAttributePlanner
{
    private static bool TryAddAttribute(
        CompactItemEntry equipment,
        int attributeId,
        byte profession,
        out CompactItemEntry updated,
        out ClassSuitAttributeStatus status,
        out string reason)
    {
        if (!HasValidAttributeShape(equipment, profession))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidAttributeState,
                "The gear contains an invalid ordinary, class-specific, or elemental attribute record.",
                out updated,
                out status,
                out reason);
        }

        if (ElementalAttributeCatalog.TryGetAttribute(
                attributeId,
                out var elemental))
        {
            return TryAddElementalAttribute(
                equipment,
                elemental,
                out updated,
                out status,
                out reason);
        }

        if (!AllClassAttributeIds.Contains(attributeId))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidClassStone,
                "The selected stone does not contain a supported Class Suit attribute.",
                out updated,
                out status,
                out reason);
        }

        if (equipment.ClassAttribute1.HasValue)
        {
            return FailAttribute(
                equipment,
                equipment.ClassAttribute1 == attributeId
                    ? ClassSuitAttributeStatus.ClassAttributeAlreadyPresent
                    : ClassSuitAttributeStatus.AttributeSlotsFull,
                equipment.ClassAttribute1 == attributeId
                    ? "The gear already has this class-specific stat."
                    : "The gear already has its one class-specific stat.",
                out updated,
                out status,
                out reason);
        }

        updated = equipment with { ClassAttribute1 = attributeId };
        status = ClassSuitAttributeStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool TryAddElementalAttribute(
        CompactItemEntry equipment,
        ElementalAttributeDefinition definition,
        out CompactItemEntry updated,
        out ClassSuitAttributeStatus status,
        out string reason)
    {
        var existingIds = new[]
        {
            equipment.ElementalAttribute1,
            equipment.ElementalAttribute2
        };
        if (existingIds.Contains(definition.AttributeId))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.ElementalAttributeAlreadyPresent,
                "The gear already has this elemental stat.",
                out updated,
                out status,
                out reason);
        }

        foreach (var existingId in existingIds.Where(static value => value.HasValue))
        {
            if (ElementalAttributeCatalog.TryGetAttribute(
                    existingId!.Value,
                    out var existing) &&
                existing.Element == definition.Element)
            {
                return FailAttribute(
                    equipment,
                    ClassSuitAttributeStatus.ElementAlreadyPresent,
                    $"The gear already contains a {definition.Element} attribute.",
                    out updated,
                    out status,
                    out reason);
            }
        }

        if (!equipment.ElementalAttribute1.HasValue)
        {
            updated = equipment with
            {
                ElementalAttribute1 = definition.AttributeId
            };
        }
        else if (!equipment.ElementalAttribute2.HasValue)
        {
            updated = equipment with
            {
                ElementalAttribute2 = definition.AttributeId
            };
        }
        else
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.ElementalSlotsFull,
                "The gear already has the maximum of two elemental stats.",
                out updated,
                out status,
                out reason);
        }

        status = ClassSuitAttributeStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool TryDeleteAttribute(
        CompactItemEntry equipment,
        byte profession,
        out CompactItemEntry updated,
        out ClassSuitAttributeStatus status,
        out string reason)
    {
        if (!HasValidAttributeShape(equipment, profession))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidAttributeState,
                "The gear contains an invalid ordinary, class-specific, or elemental attribute record.",
                out updated,
                out status,
                out reason);
        }

        // The legacy dialog does not identify a target stone. Delete newest
        // dedicated slots first, then the profession-specific slot.
        if (equipment.ElementalAttribute2.HasValue)
        {
            updated = equipment with { ElementalAttribute2 = null };
        }
        else if (equipment.ElementalAttribute1.HasValue)
        {
            updated = equipment with { ElementalAttribute1 = null };
        }
        else if (equipment.ClassAttribute1.HasValue)
        {
            updated = equipment with { ClassAttribute1 = null };
        }
        else
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.ClassAttributeMissing,
                "The gear has no Class Suit stat to delete.",
                out updated,
                out status,
                out reason);
        }

        status = ClassSuitAttributeStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool HasValidAttributeShape(
        CompactItemEntry equipment,
        byte profession)
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
        for (var index = 0; index < attributes.Length; index++)
        {
            if (attributes[index] is < 0 ||
                (!attributes[index].HasValue && levels[index].HasValue) ||
                (attributes[index].HasValue &&
                 (AllClassAttributeIds.Contains(attributes[index]!.Value) ||
                  ElementalAttributeCatalog.IsElementalAttribute(
                      attributes[index]))))
            {
                return false;
            }
        }

        return HasValidClassAttribute(equipment, profession) &&
            ElementalAttributeCatalog.HasValidPair(
                equipment.ElementalAttribute1,
                equipment.ElementalAttribute2);
    }

    private static bool HasValidClassAttribute(
        CompactItemEntry equipment,
        byte profession)
    {
        if (equipment.ClassAttribute2.HasValue)
        {
            return false;
        }

        if (!equipment.ClassAttribute1.HasValue)
        {
            return true;
        }

        var value = equipment.ClassAttribute1.Value;
        return value >= 0 && IsClassAttributeAllowed(value, profession);
    }

    private static bool FailAttribute(
        CompactItemEntry equipment,
        ClassSuitAttributeStatus failureStatus,
        string failureReason,
        out CompactItemEntry updated,
        out ClassSuitAttributeStatus status,
        out string reason)
    {
        updated = equipment;
        status = failureStatus;
        reason = failureReason;
        return false;
    }
}
