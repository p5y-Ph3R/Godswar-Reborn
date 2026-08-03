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
        if (!HasValidOrdinaryAttributeShape(equipment) ||
            !TryReadClassAttributes(
                equipment,
                profession,
                out var classAttributes))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidAttributeState,
                "The gear contains an invalid ordinary or class-specific attribute record.",
                out updated,
                out status,
                out reason);
        }

        if (classAttributes.Contains(attributeId))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.ClassAttributeAlreadyPresent,
                "The gear already has this class-specific stat.",
                out updated,
                out status,
                out reason);
        }

        if (!equipment.ClassAttribute1.HasValue)
        {
            updated = equipment with { ClassAttribute1 = attributeId };
        }
        else if (!equipment.ClassAttribute2.HasValue)
        {
            updated = equipment with { ClassAttribute2 = attributeId };
        }
        else
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.AttributeSlotsFull,
                "The gear already has the maximum of two class-specific stats.",
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
        if (!HasValidOrdinaryAttributeShape(equipment) ||
            !TryReadClassAttributes(
                equipment,
                profession,
                out var classAttributes))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidAttributeState,
                "The gear contains an invalid ordinary or class-specific attribute record.",
                out updated,
                out status,
                out reason);
        }

        if (classAttributes.Count == 0)
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.ClassAttributeMissing,
                "The gear has no class-specific stat to delete.",
                out updated,
                out status,
                out reason);
        }

        // Delete the most recently populated canonical slot first. This keeps
        // deletion deterministic even though the legacy dialogue does not
        // submit a particular class stone with the request.
        if (equipment.ClassAttribute2.HasValue)
        {
            updated = equipment with { ClassAttribute2 = null };
        }
        else
        {
            updated = equipment with { ClassAttribute1 = null };
        }

        status = ClassSuitAttributeStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static bool HasValidOrdinaryAttributeShape(
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
        for (var index = 0; index < attributes.Length; index++)
        {
            if (attributes[index] is < 0 ||
                (!attributes[index].HasValue && levels[index].HasValue) ||
                (attributes[index].HasValue &&
                 AllClassAttributeIds.Contains(attributes[index]!.Value)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReadClassAttributes(
        CompactItemEntry equipment,
        byte profession,
        out IReadOnlyList<int> attributes)
    {
        var values = new[]
        {
            equipment.ClassAttribute1,
            equipment.ClassAttribute2
        };
        if (!values[0].HasValue && values[1].HasValue)
        {
            attributes = [];
            return false;
        }

        var populated = values
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .ToArray();
        if (populated.Length != populated.Distinct().Count())
        {
            attributes = [];
            return false;
        }

        var allowedAttributes = ClassStones.Values
            .Where(value => IsProfessionAllowed(
                value.ProfessionGroup,
                profession))
            .Select(static value => value.AttributeId)
            .ToHashSet();
        if (populated.Any(value =>
                value < 0 ||
                !AllClassAttributeIds.Contains(value) ||
                !allowedAttributes.Contains(value)))
        {
            attributes = [];
            return false;
        }

        attributes = populated;
        return true;
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
