namespace Godswar.Server.State;

internal static partial class ClassSuitAttributePlanner
{
    private const int StandardClassAttributeSlots = 4;

    private static bool TryAddAttribute(
        CompactItemEntry equipment,
        int attributeId,
        ClassSuitTier tier,
        out CompactItemEntry updated,
        out ClassSuitAttributeStatus status,
        out string reason)
    {
        var (attributes, levels) = ReadAttributes(equipment);
        if (!HasValidShape(attributes, levels))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidAttributeState,
                "The weapon contains an invalid appended-attribute record.",
                out updated,
                out status,
                out reason);
        }

        if (attributes.Any(value =>
                value.HasValue &&
                AllClassAttributeIds.Contains(value.Value)))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.ClassAttributeAlreadyPresent,
                "The weapon already has a class-specific stat.",
                out updated,
                out status,
                out reason);
        }

        var availableSlots = tier is ClassSuitTier.TierIII or ClassSuitTier.TierIV
            ? attributes.Length
            : StandardClassAttributeSlots;
        var index = Array.FindIndex(
            attributes,
            0,
            availableSlots,
            static value => !value.HasValue);
        if (index < 0)
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.AttributeSlotsFull,
                "The weapon has no empty class-stat slot for its Class Suit tier.",
                out updated,
                out status,
                out reason);
        }

        attributes[index] = attributeId;
        levels[index] = 1;
        updated = WriteAttributes(equipment, attributes, levels);
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
        var (attributes, levels) = ReadAttributes(equipment);
        if (!HasValidShape(attributes, levels))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidAttributeState,
                "The weapon contains an invalid appended-attribute record.",
                out updated,
                out status,
                out reason);
        }

        var classIndexes = Enumerable.Range(0, attributes.Length)
            .Where(index => attributes[index].HasValue &&
                AllClassAttributeIds.Contains(attributes[index]!.Value))
            .ToArray();
        if (classIndexes.Length == 0)
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.ClassAttributeMissing,
                "The weapon has no class-specific stat to delete.",
                out updated,
                out status,
                out reason);
        }

        var allowedAttributes = ClassStones.Values
            .Where(value => IsProfessionAllowed(
                value.ProfessionGroup,
                profession))
            .Select(value => value.AttributeId)
            .ToHashSet();
        if (classIndexes.Length != 1 ||
            !allowedAttributes.Contains(attributes[classIndexes[0]]!.Value))
        {
            return FailAttribute(
                equipment,
                ClassSuitAttributeStatus.InvalidAttributeState,
                "The weapon contains conflicting class-specific stats.",
                out updated,
                out status,
                out reason);
        }

        var keptAttributes = new List<int?>(5);
        var keptLevels = new List<short?>(5);
        for (var index = 0; index < attributes.Length; index++)
        {
            if (index == classIndexes[0] || !attributes[index].HasValue)
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

        updated = WriteAttributes(
            equipment,
            keptAttributes.ToArray(),
            keptLevels.ToArray());
        status = ClassSuitAttributeStatus.Succeeded;
        reason = string.Empty;
        return true;
    }

    private static (int?[] Attributes, short?[] Levels) ReadAttributes(
        CompactItemEntry equipment) =>
        (
            [
                equipment.Attribute1,
                equipment.Attribute2,
                equipment.Attribute3,
                equipment.Attribute4,
                equipment.Attribute5
            ],
            [
                equipment.AttributeLevel1,
                equipment.AttributeLevel2,
                equipment.AttributeLevel3,
                equipment.AttributeLevel4,
                equipment.AttributeLevel5
            ]);

    private static bool HasValidShape(int?[] attributes, short?[] levels)
    {
        for (var index = 0; index < attributes.Length; index++)
        {
            if (attributes[index] is < 0 ||
                (!attributes[index].HasValue && levels[index].HasValue))
            {
                return false;
            }
        }
        return true;
    }

    private static CompactItemEntry WriteAttributes(
        CompactItemEntry equipment,
        int?[] attributes,
        short?[] levels) =>
        equipment with
        {
            Attribute1 = attributes[0],
            Attribute2 = attributes[1],
            Attribute3 = attributes[2],
            Attribute4 = attributes[3],
            Attribute5 = attributes[4],
            AttributeLevel1 = levels[0],
            AttributeLevel2 = levels[1],
            AttributeLevel3 = levels[2],
            AttributeLevel4 = levels[3],
            AttributeLevel5 = levels[4]
        };

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
