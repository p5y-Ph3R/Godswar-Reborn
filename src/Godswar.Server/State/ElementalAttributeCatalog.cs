namespace Godswar.Server.State;

internal enum ElementKind : byte
{
    Fire,
    Water,
    Lightning,
    Earth,
    Wind,
    Light,
    Dark
}

internal enum ElementalStatFamily : byte
{
    Power,
    Resistance,
    Penetration
}

internal readonly record struct ElementalAttributeDefinition(
    int AttributeId,
    uint StoneItemId,
    ElementKind Element,
    ElementalStatFamily Family,
    string DisplayName)
{
    public int ValueAtGrade(short grade)
    {
        if (grade is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grade),
                "Elemental attribute grade must be between 1 and 25.");
        }

        var basisPointsPerGrade = Family == ElementalStatFamily.Penetration
            ? 20
            : 40;
        return checked(basisPointsPerGrade * grade);
    }
}

internal readonly record struct ElementalStatTotals(
    int PowerBasisPoints,
    int ResistanceBasisPoints,
    int PenetrationBasisPoints)
{
    public ElementalStatTotals Add(
        ElementalStatFamily family,
        int value) =>
        family switch
        {
            ElementalStatFamily.Power => this with
            {
                PowerBasisPoints = checked(PowerBasisPoints + value)
            },
            ElementalStatFamily.Resistance => this with
            {
                ResistanceBasisPoints = checked(
                    ResistanceBasisPoints + value)
            },
            ElementalStatFamily.Penetration => this with
            {
                PenetrationBasisPoints = checked(
                    PenetrationBasisPoints + value)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
}

internal sealed record ElementalEquipmentProfile(
    IReadOnlyDictionary<ElementKind, ElementalStatTotals> RawStats,
    IReadOnlyDictionary<ElementKind, ElementalStatTotals> EffectiveStats,
    IReadOnlyDictionary<ElementKind, int> EquippedSetCounts)
{
    public ElementalStatTotals RawFor(ElementKind element) =>
        RawStats.TryGetValue(element, out var value) ? value : default;

    public ElementalStatTotals EffectiveFor(ElementKind element) =>
        EffectiveStats.TryGetValue(element, out var value) ? value : default;

    public int CountFor(ElementKind element) =>
        EquippedSetCounts.TryGetValue(element, out var value) ? value : 0;

    public int HighestThresholdFor(ElementKind element)
    {
        var count = CountFor(element);
        return count >= 10 ? 10 : count >= 6 ? 6 : count >= 3 ? 3 : 0;
    }
}

internal static class ElementalAttributeCatalog
{
    public const int MinimumAttributeId = 480;
    public const int MaximumAttributeId = 500;
    public const uint MinimumStoneItemId = 16300;
    public const uint MaximumStoneItemId = 16320;

    public static IReadOnlyList<ElementalAttributeDefinition> All { get; } =
        BuildDefinitions();

    private static readonly IReadOnlyDictionary<int, ElementalAttributeDefinition>
        ByAttributeId = All.ToDictionary(static value => value.AttributeId);

    private static readonly IReadOnlyDictionary<uint, ElementalAttributeDefinition>
        ByStoneItemId = All.ToDictionary(static value => value.StoneItemId);

    public static bool TryGetAttribute(
        int attributeId,
        out ElementalAttributeDefinition definition) =>
        ByAttributeId.TryGetValue(attributeId, out definition);

    public static bool TryGetStone(
        uint stoneItemId,
        out ElementalAttributeDefinition definition) =>
        ByStoneItemId.TryGetValue(stoneItemId, out definition);

    public static bool IsElementalAttribute(int? attributeId) =>
        attributeId.HasValue && ByAttributeId.ContainsKey(attributeId.Value);

    public static bool HasValidPair(int? first, int? second)
    {
        if (!first.HasValue && second.HasValue)
        {
            return false;
        }

        if (!first.HasValue)
        {
            return true;
        }

        if (!TryGetAttribute(first.Value, out var firstDefinition))
        {
            return false;
        }

        return !second.HasValue ||
            TryGetAttribute(second.Value, out var secondDefinition) &&
            firstDefinition.Element != secondDefinition.Element;
    }

    public static bool HasCanonicalDedicatedAttributeShape(
        CompactItemEntry item)
    {
        if (item.ClassAttribute2.HasValue ||
            item.ClassAttribute1.HasValue &&
            !IsClassSuitAttribute(item.ClassAttribute1) ||
            !HasValidPair(
                item.ElementalAttribute1,
                item.ElementalAttribute2))
        {
            return false;
        }

        return !IsDedicatedAttribute(item.Attribute1) &&
            !IsDedicatedAttribute(item.Attribute2) &&
            !IsDedicatedAttribute(item.Attribute3) &&
            !IsDedicatedAttribute(item.Attribute4) &&
            !IsDedicatedAttribute(item.Attribute5);
    }

    public static ElementalEquipmentProfile CalculateEquippedProfile(
        IEnumerable<CompactItemEntry> equippedItems)
    {
        ArgumentNullException.ThrowIfNull(equippedItems);
        var stats = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => default(ElementalStatTotals));
        var counts = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => 0);

        foreach (var item in equippedItems)
        {
            if (item.IsEmpty ||
                !ClassSuitConversionCatalog.IsTierThreeOrFourItem(item.Id) ||
                item.Grade is < 1 or > 25 ||
                !HasCanonicalDedicatedAttributeShape(item))
            {
                continue;
            }

            var countedElements = new HashSet<ElementKind>();
            Add(item.ElementalAttribute1, item.Grade, stats, countedElements);
            Add(item.ElementalAttribute2, item.Grade, stats, countedElements);
            foreach (var element in countedElements)
            {
                counts[element] = Math.Min(10, checked(counts[element] + 1));
            }
        }

        var effective = Enum.GetValues<ElementKind>()
            .ToDictionary(
                static value => value,
                value => ApplySetBonuses(stats[value], counts[value]));
        return new ElementalEquipmentProfile(stats, effective, counts);
    }

    private static void Add(
        int? attributeId,
        short grade,
        IDictionary<ElementKind, ElementalStatTotals> stats,
        ISet<ElementKind> countedElements)
    {
        if (!attributeId.HasValue ||
            !TryGetAttribute(attributeId.Value, out var definition))
        {
            return;
        }

        stats[definition.Element] = stats[definition.Element].Add(
            definition.Family,
            definition.ValueAtGrade(grade));
        countedElements.Add(definition.Element);
    }

    private static ElementalStatTotals ApplySetBonuses(
        ElementalStatTotals raw,
        int equippedCount)
    {
        var effective = raw;
        if (equippedCount >= 3)
        {
            effective = effective.Add(ElementalStatFamily.Power, 200);
        }
        if (equippedCount >= 6)
        {
            effective = effective.Add(ElementalStatFamily.Resistance, 300);
        }
        if (equippedCount >= 10)
        {
            effective = effective.Add(ElementalStatFamily.Penetration, 200);
        }
        return effective;
    }

    private static IReadOnlyList<ElementalAttributeDefinition>
        BuildDefinitions()
    {
        var definitions = new List<ElementalAttributeDefinition>(21);
        foreach (var element in Enum.GetValues<ElementKind>())
        {
            foreach (var family in Enum.GetValues<ElementalStatFamily>())
            {
                var ordinal = ((int)element * 3) + (int)family;
                definitions.Add(new ElementalAttributeDefinition(
                    MinimumAttributeId + ordinal,
                    MinimumStoneItemId + checked((uint)ordinal),
                    element,
                    family,
                    $"{element} {family} Stone"));
            }
        }

        return definitions;
    }

    private static bool IsDedicatedAttribute(int? attributeId) =>
        IsClassSuitAttribute(attributeId) ||
        IsElementalAttribute(attributeId);

    private static bool IsClassSuitAttribute(int? attributeId) =>
        attributeId is
            200 or 201 or 210 or 211 or
            220 or 221 or 230 or 231;
}
