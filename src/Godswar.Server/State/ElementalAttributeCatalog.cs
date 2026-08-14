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

// Compatibility identity: the numeric order and member names are published in
// immutable V8/V9 item content. Gameplay code uses ElementalAttributeRole.
internal enum ElementalStatFamily : byte
{
    Power,
    Resistance,
    Penetration
}

internal enum ElementalEffectKind : byte
{
    Burn = 0,
    Drench = 1,
    Shock = 2,
    Fracture = 3,
    Gale = 4,
    Dazzle = 5,
    Wither = 6
}

// Numeric values are part of the durable attribute-ID calculation. Keep the
// historical 0/1/2 order even though the player-facing order is potency,
// application chance, then resistance.
internal enum ElementalAttributeRole : byte
{
    EffectPotency = 0,
    EffectResistance = 1,
    // A compatibility role with effect-specific trigger semantics. Burn,
    // Drench, Shock, Fracture, Dazzle, and Wither roll only after an
    // authoritative attack commits. Gale rolls after accepted movement and
    // activates a self movement-speed boost after accepted movement.
    ApplicationChance = 2
}

internal readonly record struct ElementalAttributeDefinition(
    int AttributeId,
    ElementKind Element,
    ElementalStatFamily Family,
    string DisplayName)
{
    // Compatibility projection for content code that still starts from an
    // attribute. Three family-specific attributes intentionally point to the
    // same canonical elemental stone.
    public uint StoneItemId =>
        ElementalAttributeCatalog.StoneItemIdFor(Element);

    public ElementalEffectKind Effect =>
        ElementalAttributeCatalog.EffectFor(Element).Effect;

    public ElementalAttributeRole Role =>
        ElementalAttributeCatalog.RoleForFamily(Family);

    public int ValueAtGrade(short grade)
    {
        if (grade is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grade),
                "Elemental attribute grade must be between 1 and 25.");
        }

        var basisPointsPerGrade =
            Role == ElementalAttributeRole.ApplicationChance
            ? 20
            : 40;
        return checked(basisPointsPerGrade * grade);
    }
}

internal readonly record struct ElementalEffectDefinition(
    ElementKind Element,
    ElementalEffectKind Effect,
    string ElementDisplayName,
    string EffectDisplayName,
    string PotencyDisplayName,
    string ResistanceDisplayName,
    string ApplicationDisplayName)
{
    public string AttributeDisplayName(ElementalAttributeRole role) =>
        role switch
        {
            ElementalAttributeRole.EffectPotency => PotencyDisplayName,
            ElementalAttributeRole.EffectResistance => ResistanceDisplayName,
            ElementalAttributeRole.ApplicationChance => ApplicationDisplayName,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
}

internal sealed record ElementalStoneDefinition(
    uint ItemId,
    ElementKind Element,
    string DisplayName,
    IReadOnlyList<int> AttributeIds);

internal readonly record struct ElementalEffectTotals(
    int EffectPotencyBasisPoints,
    int EffectResistanceBasisPoints,
    int ApplicationChanceBasisPoints)
{
    public ElementalEffectTotals Add(
        ElementalAttributeRole role,
        int value) =>
        role switch
        {
            ElementalAttributeRole.EffectPotency => this with
            {
                EffectPotencyBasisPoints = checked(
                    EffectPotencyBasisPoints + value)
            },
            ElementalAttributeRole.EffectResistance => this with
            {
                EffectResistanceBasisPoints = checked(
                    EffectResistanceBasisPoints + value)
            },
            ElementalAttributeRole.ApplicationChance => this with
            {
                ApplicationChanceBasisPoints = checked(
                    ApplicationChanceBasisPoints + value)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
}

internal readonly record struct ElementalEquippedItem(
    int SlotIndex,
    CompactItemEntry Item);

internal sealed record ElementalEquipmentProfile(
    IReadOnlyDictionary<ElementKind, ElementalEffectTotals> RawEffects,
    IReadOnlyDictionary<ElementKind, int> EquippedSetCounts,
    IReadOnlyDictionary<
        ElementKind,
        IReadOnlyList<ElementalResonanceTierDefinition>> ActiveResonanceTiers)
{
    public ElementalEffectTotals EffectsFor(ElementKind element) =>
        RawEffects.TryGetValue(element, out var value) ? value : default;

    public int CountFor(ElementKind element) =>
        EquippedSetCounts.TryGetValue(element, out var value) ? value : 0;

    public IReadOnlyList<ElementalResonanceTierDefinition> ResonanceFor(
        ElementKind element) =>
        ActiveResonanceTiers.TryGetValue(element, out var value)
            ? value
            : Array.Empty<ElementalResonanceTierDefinition>();

    public int HighestThresholdFor(ElementKind element) =>
        ResonanceFor(element).LastOrDefault()?.RequiredPieces ?? 0;
}

internal static class ElementalAttributeCatalog
{
    // The typed, deterministic policy/state-machine implementation is live.
    // Resolver, movement, and recovery adapters still opt in explicitly; this
    // flag is not player-target admission or packet-handler wiring.
    public const bool GameplayExecutionEnabled = true;

    public const int MinimumAttributeId = 480;
    public const int MaximumAttributeId = 500;
    public const uint MinimumStoneItemId = 16300;
    public const uint MaximumStoneItemId = 16318;
    public const uint StoneItemIdStride = 3;

    public static IReadOnlyList<ElementalEffectDefinition> Effects { get; } =
        BuildEffects();

    public static IReadOnlyList<ElementalAttributeDefinition> All { get; } =
        BuildDefinitions();

    public static IReadOnlyList<ElementalStoneDefinition> Stones { get; } =
        BuildStones();

    private static readonly IReadOnlyDictionary<int, ElementalAttributeDefinition>
        ByAttributeId = All.ToDictionary(static value => value.AttributeId);

    private static readonly IReadOnlyDictionary<
        (ElementKind Element, ElementalStatFamily Family),
        ElementalAttributeDefinition> ByElementAndFamily =
        All.ToDictionary(static value => (value.Element, value.Family));

    private static readonly IReadOnlyDictionary<
        (ElementKind Element, ElementalAttributeRole Role),
        ElementalAttributeDefinition> ByElementAndRole =
        All.ToDictionary(static value => (value.Element, value.Role));

    private static readonly IReadOnlyDictionary<
        ElementKind,
        ElementalEffectDefinition> EffectByElement =
        Effects.ToDictionary(static value => value.Element);

    private static readonly IReadOnlyDictionary<uint, ElementalStoneDefinition>
        ByStoneItemId = Stones.ToDictionary(static value => value.ItemId);

    public static bool TryGetAttribute(
        int attributeId,
        out ElementalAttributeDefinition definition) =>
        ByAttributeId.TryGetValue(attributeId, out definition);

    public static bool TryGetAttribute(
        ElementKind element,
        ElementalStatFamily family,
        out ElementalAttributeDefinition definition) =>
        ByElementAndFamily.TryGetValue((element, family), out definition);

    public static bool TryGetAttribute(
        ElementKind element,
        ElementalAttributeRole role,
        out ElementalAttributeDefinition definition) =>
        ByElementAndRole.TryGetValue((element, role), out definition);

    public static ElementalEffectDefinition EffectFor(ElementKind element) =>
        EffectByElement[element];

    public static bool TryGetStone(
        uint stoneItemId,
        out ElementalStoneDefinition definition) =>
        ByStoneItemId.TryGetValue(stoneItemId, out definition!);

    public static bool TryResolveStoneForEquipmentSlot(
        uint stoneItemId,
        short equipmentSlot,
        out ElementalAttributeDefinition definition)
    {
        definition = default;
        return TryGetStone(stoneItemId, out var stone) &&
            TryGetFamilyForEquipmentSlot(equipmentSlot, out var family) &&
            TryGetAttribute(stone.Element, family, out definition) &&
            stone.AttributeIds.Contains(definition.AttributeId);
    }

    public static bool TryGetFamilyForEquipmentSlot(
        int equipmentSlot,
        out ElementalStatFamily family)
    {
        family = equipmentSlot switch
        {
            EquipmentSlots.Weapon => ElementalStatFamily.Power,
            EquipmentSlots.Head or
            EquipmentSlots.Glove or
            EquipmentSlots.Ring1 or
            EquipmentSlots.Ring2 => ElementalStatFamily.Penetration,
            EquipmentSlots.Amulet or
            EquipmentSlots.Armor or
            EquipmentSlots.Cuff or
            EquipmentSlots.Girdle or
            EquipmentSlots.Shoes or
            EquipmentSlots.Leggings or
            EquipmentSlots.Shield => ElementalStatFamily.Resistance,
            _ => default
        };
        return equipmentSlot is >= EquipmentSlots.Head and <= EquipmentSlots.Shield;
    }

    public static bool TryGetRoleForEquipmentSlot(
        int equipmentSlot,
        out ElementalAttributeRole role)
    {
        if (TryGetFamilyForEquipmentSlot(equipmentSlot, out var family))
        {
            role = RoleForFamily(family);
            return true;
        }

        role = default;
        return false;
    }

    public static ElementalAttributeRole RoleForFamily(
        ElementalStatFamily family) =>
        family switch
        {
            ElementalStatFamily.Power => ElementalAttributeRole.EffectPotency,
            ElementalStatFamily.Resistance => ElementalAttributeRole.EffectResistance,
            ElementalStatFamily.Penetration => ElementalAttributeRole.ApplicationChance,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    public static uint StoneItemIdFor(ElementKind element) =>
        MinimumStoneItemId + checked((uint)((int)element * StoneItemIdStride));

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
        IEnumerable<ElementalEquippedItem> equippedItems)
    {
        ArgumentNullException.ThrowIfNull(equippedItems);
        var stats = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => default(ElementalEffectTotals));
        var counts = Enum.GetValues<ElementKind>()
            .ToDictionary(static value => value, static _ => 0);

        foreach (var equipped in equippedItems)
        {
            var item = equipped.Item;
            if (item.IsEmpty ||
                !ClassSuitConversionCatalog.IsTierThreeOrFourItem(item.Id) ||
                item.Grade is < 1 or > 25 ||
                !HasCanonicalDedicatedAttributeShape(item) ||
                !AttributesMatchEquippedSlot(equipped))
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

        var activeResonances = Enum.GetValues<ElementKind>()
            .ToDictionary(
                static value => value,
                value => ElementalResonanceCatalog.ActiveFor(
                    value,
                    counts[value]));
        return new ElementalEquipmentProfile(stats, counts, activeResonances);
    }

    private static bool AttributesMatchEquippedSlot(
        ElementalEquippedItem equipped)
    {
        if (!TryGetFamilyForEquipmentSlot(
                equipped.SlotIndex,
                out var expectedFamily))
        {
            return false;
        }

        return MatchesFamily(
                equipped.Item.ElementalAttribute1,
                expectedFamily) &&
            MatchesFamily(
                equipped.Item.ElementalAttribute2,
                expectedFamily);
    }

    private static bool MatchesFamily(
        int? attributeId,
        ElementalStatFamily expectedFamily) =>
        !attributeId.HasValue ||
        TryGetAttribute(attributeId.Value, out var definition) &&
        definition.Family == expectedFamily;

    private static void Add(
        int? attributeId,
        short grade,
        IDictionary<ElementKind, ElementalEffectTotals> stats,
        ISet<ElementKind> countedElements)
    {
        if (!attributeId.HasValue ||
            !TryGetAttribute(attributeId.Value, out var definition))
        {
            return;
        }

        stats[definition.Element] = stats[definition.Element].Add(
            definition.Role,
            definition.ValueAtGrade(grade));
        countedElements.Add(definition.Element);
    }

    private static IReadOnlyList<ElementalAttributeDefinition>
        BuildDefinitions()
    {
        var definitions = new List<ElementalAttributeDefinition>(21);
        foreach (var effect in Effects)
        {
            foreach (var family in Enum.GetValues<ElementalStatFamily>())
            {
                var ordinal = ((int)effect.Element * 3) + (int)family;
                var role = RoleForFamily(family);
                definitions.Add(new ElementalAttributeDefinition(
                    MinimumAttributeId + ordinal,
                    effect.Element,
                    family,
                    effect.AttributeDisplayName(role)));
            }
        }

        return definitions;
    }

    private static IReadOnlyList<ElementalEffectDefinition> BuildEffects() =>
    [
        new(
            ElementKind.Fire,
            ElementalEffectKind.Burn,
            "Fire",
            "Burn",
            "[Burn] Damage over time",
            "[Burn] Damage resistance",
            "[Burn] On-hit chance"),
        new(
            ElementKind.Water,
            ElementalEffectKind.Drench,
            "Water",
            "Drench",
            "[Drench] Movement slow",
            "[Drench] Slow resistance",
            "[Drench] Slow chance"),
        new(
            ElementKind.Lightning,
            ElementalEffectKind.Shock,
            "Lightning",
            "Shock",
            "[Shock] Paralyze duration",
            "[Shock] Paralyze resistance",
            "[Shock] Paralyze chance"),
        new(
            ElementKind.Earth,
            ElementalEffectKind.Fracture,
            "Earth",
            "Fracture",
            "[Fracture] Defense reduction",
            "[Fracture] Defense-break resistance",
            "[Fracture] Defense-break chance"),
        new(
            ElementKind.Wind,
            ElementalEffectKind.Gale,
            "Wind",
            "Gale",
            "[Gale] Movement speed",
            "[Gale] Slow resistance",
            "[Gale] Movement activation chance"),
        new(
            ElementKind.Light,
            ElementalEffectKind.Dazzle,
            "Light",
            "Dazzle",
            "[Dazzle] Accuracy reduction",
            "[Dazzle] Accuracy-loss resistance",
            "[Dazzle] Accuracy-reduction chance"),
        new(
            ElementKind.Dark,
            ElementalEffectKind.Wither,
            "Dark",
            "Wither",
            "[Wither] Healing reduction",
            "[Wither] Healing-reduction resistance",
            "[Wither] Healing-suppression chance")
    ];

    private static IReadOnlyList<ElementalStoneDefinition> BuildStones()
    {
        var names = new Dictionary<ElementKind, string>
        {
            [ElementKind.Fire] = "Prometheus Stone",
            [ElementKind.Water] = "Poseidon Stone",
            [ElementKind.Lightning] = "Zeus Stone",
            [ElementKind.Earth] = "Gaia Stone",
            [ElementKind.Wind] = "Aeolus Stone",
            [ElementKind.Light] = "Apollo Stone",
            [ElementKind.Dark] = "Hades Stone"
        };
        return Enum.GetValues<ElementKind>()
            .Select(element => new ElementalStoneDefinition(
                StoneItemIdFor(element),
                element,
                names[element],
                Array.AsReadOnly(All
                    .Where(value => value.Element == element)
                    .OrderBy(static value => value.Family)
                    .Select(static value => value.AttributeId)
                    .ToArray())))
            .ToArray();
    }

    private static bool IsDedicatedAttribute(int? attributeId) =>
        IsClassSuitAttribute(attributeId) ||
        IsElementalAttribute(attributeId);

    private static bool IsClassSuitAttribute(int? attributeId) =>
        attributeId is
            200 or 201 or 210 or 211 or
            220 or 221 or 230 or 231;
}
