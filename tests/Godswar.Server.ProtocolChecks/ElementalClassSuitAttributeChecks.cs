using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ElementalClassSuitAttributeChecks
{
    public const string CheckName =
        "Elemental Class Suit content and runtime profile";

    public static Task RunAsync()
    {
        CheckLockedCatalog();
        CheckGradeValues();
        CheckSetThresholds();
        ElementalResonanceContractChecks.Run();
        CheckDualElementAndInvalidGrade();
        CheckRuntimeRefreshAndRegularSlotBoundary();
        return Task.CompletedTask;
    }

    private static void CheckLockedCatalog()
    {
        Check.Equal(21, ElementalAttributeCatalog.All.Count,
            "elemental attribute count");
        Check.True(
            ElementalAttributeCatalog.All
                .Select(static value => value.AttributeId)
                .SequenceEqual(Enumerable.Range(480, 21)),
            "family-specific attribute IDs remain the durable 480 through 500 range");
        Check.Equal(
            "[Burn] Damage over time",
            ElementalAttributeCatalog.All[0].DisplayName,
            "first applied elemental attribute name");
        Check.Equal(
            "[Wither] Healing-suppression chance",
            ElementalAttributeCatalog.All[^1].DisplayName,
            "last applied elemental attribute name");

        CheckEffectIdentitiesAndLabels();

        var stoneNames = new[]
        {
            "Prometheus Stone",
            "Poseidon Stone",
            "Zeus Stone",
            "Gaia Stone",
            "Aeolus Stone",
            "Apollo Stone",
            "Hades Stone"
        };
        Check.Equal(7, ElementalAttributeCatalog.Stones.Count,
            "canonical Greek elemental stone count");
        for (var element = 0; element < stoneNames.Length; element++)
        {
            var stone = ElementalAttributeCatalog.Stones[element];
            var expectedItemId = checked((uint)(16300 + (element * 3)));
            var expectedAttributeIds = Enumerable.Range(
                480 + (element * 3),
                3);
            Check.True(
                stone.ItemId == expectedItemId &&
                stone.Element == (ElementKind)element &&
                stone.DisplayName == stoneNames[element] &&
                stone.AttributeIds.SequenceEqual(expectedAttributeIds) &&
                ElementalAttributeCatalog.TryGetStone(
                    expectedItemId,
                    out var byItemId) &&
                byItemId == stone,
                $"canonical {stoneNames[element]} identity and three-family mapping");

            var alias = stone.DisplayName.Replace(
                " ",
                string.Empty,
                StringComparison.Ordinal);
            Check.True(
                TestItemContent.Catalog.Materials.TryResolveDeveloper(
                    alias,
                    out var byAlias) &&
                byAlias.ItemId == stone.ItemId &&
                TestItemContent.Catalog.Materials.TryResolveDeveloper(
                    stone.ItemId,
                    out var byId) &&
                byId == byAlias,
                $"developer grant resolves canonical alias {alias}");
        }

        foreach (var retiredItemId in new uint[]
                 {
                     16301, 16302, 16304, 16305, 16307, 16308,
                     16310, 16311, 16313, 16314, 16316, 16317,
                     16319, 16320
                 })
        {
            Check.True(
                !ElementalAttributeCatalog.TryGetStone(
                    retiredItemId,
                    out _),
                $"retired family-specific item {retiredItemId} is not an active stone");
        }

        CheckSlotDerivedRoles();

        Check.True(
            (byte)ElementalStatFamily.Power == 0 &&
            (byte)ElementalStatFamily.Resistance == 1 &&
            (byte)ElementalStatFamily.Penetration == 2 &&
            Enum.GetNames<ElementalStatFamily>().SequenceEqual(
                ["Power", "Resistance", "Penetration"]),
            "V8/V9 family names and numeric positions remain immutable");

        Check.Equal(21, ElementalItemContentBaseline.Attributes.Count,
            "immutable elemental attribute policy count");
        for (var index = 0; index < 21; index++)
        {
            var policy = ElementalItemContentBaseline.Attributes[index];
            var firstDistribution = checked((short)(391 + (index * 2)));
            var legacyElement = (ElementKind)(index / 3);
            var legacyFamily = (ElementalStatFamily)(index % 3);
            Check.True(
                policy.Id == 480 + index &&
                policy.NameKey == $"{legacyElement}{legacyFamily}Per" &&
                policy.Distribution.SequenceEqual(
                    [firstDistribution, checked((short)(firstDistribution + 1))]) &&
                policy.StatType == 29 + (index % 3) &&
                policy.Percent &&
                policy.MaxLevel == 25,
                $"elemental attribute {policy.Id} preserves its immutable compatibility identity");
        }
    }

    private static void CheckEffectIdentitiesAndLabels()
    {
        var expected = new[]
        {
            (
                ElementKind.Fire,
                ElementalEffectKind.Burn,
                "Burn",
                "[Burn] Damage over time",
                "[Burn] Damage resistance",
                "[Burn] On-hit chance"),
            (
                ElementKind.Water,
                ElementalEffectKind.Drench,
                "Drench",
                "[Drench] Movement slow",
                "[Drench] Slow resistance",
                "[Drench] Slow chance"),
            (
                ElementKind.Lightning,
                ElementalEffectKind.Shock,
                "Shock",
                "[Shock] Paralyze duration",
                "[Shock] Paralyze resistance",
                "[Shock] Paralyze chance"),
            (
                ElementKind.Earth,
                ElementalEffectKind.Fracture,
                "Fracture",
                "[Fracture] Defense reduction",
                "[Fracture] Defense-break resistance",
                "[Fracture] Defense-break chance"),
            (
                ElementKind.Wind,
                ElementalEffectKind.Gale,
                "Gale",
                "[Gale] Movement speed",
                "[Gale] Slow resistance",
                "[Gale] Movement activation chance"),
            (
                ElementKind.Light,
                ElementalEffectKind.Dazzle,
                "Dazzle",
                "[Dazzle] Accuracy reduction",
                "[Dazzle] Accuracy-loss resistance",
                "[Dazzle] Accuracy-reduction chance"),
            (
                ElementKind.Dark,
                ElementalEffectKind.Wither,
                "Wither",
                "[Wither] Healing reduction",
                "[Wither] Healing-reduction resistance",
                "[Wither] Healing-suppression chance")
        };
        Check.Equal(7, ElementalAttributeCatalog.Effects.Count,
            "elemental effect identity count");
        foreach (var (
                     element,
                     effect,
                     label,
                     potencyLabel,
                     resistanceLabel,
                     applicationLabel) in expected)
        {
            var identity = ElementalAttributeCatalog.EffectFor(element);
            Check.True(
                identity.Effect == effect &&
                identity.EffectDisplayName == label,
                $"{element} owns the {label} effect identity");
            Check.True(
                ElementalAttributeCatalog.TryGetAttribute(
                    element,
                    ElementalAttributeRole.EffectPotency,
                    out var potency) &&
                potency.Effect == effect &&
                potency.DisplayName == potencyLabel &&
                ElementalAttributeCatalog.TryGetAttribute(
                    element,
                    ElementalAttributeRole.EffectResistance,
                    out var resistance) &&
                resistance.DisplayName == resistanceLabel &&
                ElementalAttributeCatalog.TryGetAttribute(
                    element,
                    ElementalAttributeRole.ApplicationChance,
                    out var chance) &&
                chance.DisplayName == applicationLabel &&
                !potency.DisplayName.StartsWith(
                    element.ToString(),
                    StringComparison.Ordinal) &&
                !resistance.DisplayName.StartsWith(
                    element.ToString(),
                    StringComparison.Ordinal) &&
                !chance.DisplayName.StartsWith(
                    element.ToString(),
                    StringComparison.Ordinal),
                $"{element} exposes exact role labels without a raw element prefix");
        }

        Check.Equal(
            (byte)3,
            (byte)ElementalEffectKind.Fracture,
            "Fracture retains Earth's prior runtime effect ordinal");
    }

    private static void CheckSlotDerivedRoles()
    {
        var expected = new[]
        {
            (EquipmentSlots.Head, ElementalStatFamily.Penetration, ElementalAttributeRole.ApplicationChance),
            (EquipmentSlots.Amulet, ElementalStatFamily.Resistance, ElementalAttributeRole.EffectResistance),
            (EquipmentSlots.Glove, ElementalStatFamily.Penetration, ElementalAttributeRole.ApplicationChance),
            (EquipmentSlots.Armor, ElementalStatFamily.Resistance, ElementalAttributeRole.EffectResistance),
            (EquipmentSlots.Cuff, ElementalStatFamily.Resistance, ElementalAttributeRole.EffectResistance),
            (EquipmentSlots.Girdle, ElementalStatFamily.Resistance, ElementalAttributeRole.EffectResistance),
            (EquipmentSlots.Shoes, ElementalStatFamily.Resistance, ElementalAttributeRole.EffectResistance),
            (EquipmentSlots.Leggings, ElementalStatFamily.Resistance, ElementalAttributeRole.EffectResistance),
            (EquipmentSlots.Ring1, ElementalStatFamily.Penetration, ElementalAttributeRole.ApplicationChance),
            (EquipmentSlots.Ring2, ElementalStatFamily.Penetration, ElementalAttributeRole.ApplicationChance),
            (EquipmentSlots.Weapon, ElementalStatFamily.Power, ElementalAttributeRole.EffectPotency),
            (EquipmentSlots.Shield, ElementalStatFamily.Resistance, ElementalAttributeRole.EffectResistance)
        };
        foreach (var (slot, family, role) in expected)
        {
            Check.True(
                ElementalAttributeCatalog.TryGetFamilyForEquipmentSlot(
                    slot,
                    out var actualFamily) &&
                actualFamily == family &&
                ElementalAttributeCatalog.TryGetRoleForEquipmentSlot(
                    slot,
                    out var actualRole) &&
                actualRole == role &&
                ElementalAttributeCatalog.TryResolveStoneForEquipmentSlot(
                    16300,
                    checked((short)slot),
                    out var attribute) &&
                attribute.AttributeId == 480 + (int)family,
                $"equipment slot {slot} preserves {family} and resolves semantic role {role}");
        }

        Check.True(
            !ElementalAttributeCatalog.TryGetFamilyForEquipmentSlot(
                EquipmentSlots.Stylish,
                out _) &&
            !ElementalAttributeCatalog.TryGetRoleForEquipmentSlot(
                EquipmentSlots.Stylish,
                out _) &&
            !ElementalAttributeCatalog.TryResolveStoneForEquipmentSlot(
                16300,
                EquipmentSlots.Stylish,
                out _),
            "non-combat equipment slots cannot resolve an elemental role");
    }

    private static void CheckGradeValues()
    {
        Check.True(
            ElementalAttributeCatalog.TryGetAttribute(480, out var potency) &&
            potency.Role == ElementalAttributeRole.EffectPotency &&
            potency.ValueAtGrade(1) == 40 &&
            potency.ValueAtGrade(25) == 1_000,
            "effect potency progresses from 40 to 1000 basis points");
        Check.True(
            ElementalAttributeCatalog.TryGetAttribute(481, out var resistance) &&
            resistance.Role == ElementalAttributeRole.EffectResistance &&
            resistance.ValueAtGrade(1) == 40 &&
            resistance.ValueAtGrade(25) == 1_000,
            "effect resistance progresses from 40 to 1000 basis points");
        Check.True(
            ElementalAttributeCatalog.TryGetAttribute(482, out var chance) &&
            chance.Role == ElementalAttributeRole.ApplicationChance &&
            chance.ValueAtGrade(1) == 20 &&
            chance.ValueAtGrade(25) == 500,
            "application chance progresses from 20 to 500 basis points");
        Check.Throws<ArgumentOutOfRangeException>(
            () => potency.ValueAtGrade(0),
            "typed value projection rejects grade zero");
        Check.Throws<ArgumentOutOfRangeException>(
            () => chance.ValueAtGrade(26),
            "typed value projection rejects grade above 25");
    }

    private static void CheckSetThresholds()
    {
        foreach (var count in new[] { 0, 2, 3, 5, 6, 9, 10, 12 })
        {
            var profile = ElementalAttributeCatalog.CalculateEquippedProfile(
                Enumerable.Range(0, count)
                    .Select(static _ => ElementalGear(480, grade: 1)));
            var expectedCount = Math.Min(count, 10);
            var expectedThreshold = expectedCount >= 10
                ? 10
                : expectedCount >= 6
                    ? 6
                    : expectedCount >= 3 ? 3 : 0;
            var effects = profile.EffectsFor(ElementKind.Fire);
            var active = profile.ResonanceFor(ElementKind.Fire);
            Check.Equal(expectedCount, profile.CountFor(ElementKind.Fire),
                $"Fire display count at {count} equipped items");
            Check.Equal(expectedThreshold, profile.HighestThresholdFor(ElementKind.Fire),
                $"Fire set threshold at {count} equipped items");
            Check.Equal(count * 40, effects.EffectPotencyBasisPoints,
                $"Fire Burn potency at {count} equipped items");
            Check.True(
                active.Select(static value => value.RequiredPieces)
                    .SequenceEqual(expectedThreshold switch
                    {
                        10 => [3, 6, 10],
                        6 => [3, 6],
                        3 => [3],
                        _ => Array.Empty<int>()
                    }),
                $"Fire exposes cumulative resonance tiers at {count} items");
            Check.True(
                effects.EffectResistanceBasisPoints == 0 &&
                effects.ApplicationChanceBasisPoints == 0,
                $"resonance does not mutate Fire effect totals at {count} items");
        }
    }

    private static void CheckDualElementAndInvalidGrade()
    {
        var dual = ElementalGear(481, 25) with
        {
            ElementalAttribute2 = 485
        };
        var profile = ElementalAttributeCatalog.CalculateEquippedProfile([dual]);
        Check.True(
            profile.CountFor(ElementKind.Fire) == 1 &&
            profile.EffectsFor(ElementKind.Fire).EffectResistanceBasisPoints == 1_000 &&
            profile.CountFor(ElementKind.Water) == 1 &&
            profile.EffectsFor(ElementKind.Water).ApplicationChanceBasisPoints == 500,
            "one dual-element item contributes once to each matching set");
        Check.True(
            !ElementalAttributeCatalog.HasValidPair(480, 481) &&
            ElementalAttributeCatalog.HasValidPair(480, 483),
            "same-element families are rejected while different elements are valid");

        foreach (var grade in new short[] { 0, 26 })
        {
            var invalid = ElementalAttributeCatalog.CalculateEquippedProfile(
                [ElementalGear(480, grade)]);
            Check.True(
                invalid.CountFor(ElementKind.Fire) == 0 &&
                invalid.EffectsFor(ElementKind.Fire) == default,
                $"arbitrary runtime projection fails closed for grade {grade}");
        }

        foreach (var corrupt in new[]
                 {
                     ElementalGear(480, 25) with { ClassAttribute2 = 201 },
                     ElementalGear(480, 25) with { Attribute1 = 483 },
                     ElementalGear(480, 25) with
                     {
                         ElementalAttribute2 = 481
                     }
                 })
        {
            var invalid = ElementalAttributeCatalog.CalculateEquippedProfile(
                [corrupt]);
            Check.True(
                invalid.CountFor(ElementKind.Fire) == 0 &&
                invalid.EffectsFor(ElementKind.Fire) == default,
                "non-canonical dedicated attribute state fails closed");
        }
    }

    private static void CheckRuntimeRefreshAndRegularSlotBoundary()
    {
        var equipment = GameDefaults.DefaultEquipment(profession: 0);
        equipment = EquipmentSlots.SetSlot(
            equipment,
            profession: 0,
            EquipmentSlots.Weapon,
            ElementalGear(480, 25).ToCompactString());
        equipment = EquipmentSlots.SetSlot(
            equipment,
            profession: 0,
            EquipmentSlots.Stylish,
            ElementalGear(483, 25).ToCompactString());
        var character = new GameCharacter
        {
            Profession = 0,
            Equipment = equipment
        };
        Check.True(
            character.ElementalEquipment.CountFor(ElementKind.Fire) == 1 &&
            character.ElementalEquipment.CountFor(ElementKind.Water) == 0,
            "only authoritative regular slots 0 through 11 affect the profile");

        var changed = EquipmentSlots.SetSlot(
            equipment,
            profession: 0,
            EquipmentSlots.Weapon,
            ElementalGear(483, 25).ToCompactString());
        character.Equipment = changed;
        Check.True(
            character.ElementalEquipment.CountFor(ElementKind.Fire) == 0 &&
            character.ElementalEquipment.CountFor(ElementKind.Water) == 1,
            "equipment assignment refreshes the typed elemental profile");

        var reloaded = new GameCharacter
        {
            Profession = 0,
            Equipment = changed
        };
        Check.True(
            reloaded.ElementalEquipment.CountFor(ElementKind.Fire) == 0 &&
            reloaded.ElementalEquipment.CountFor(ElementKind.Water) == 1 &&
            reloaded.ElementalEquipment.EffectsFor(ElementKind.Water)
                .EffectPotencyBasisPoints == 1_000,
            "login/reload hydration deterministically rebuilds the profile");
    }

    private static CompactItemEntry ElementalGear(
        int attributeId,
        short grade) =>
        CompactItemEntry.Empty with
        {
            Id = 1034,
            Quality = 20,
            Grade = grade,
            Bound = 1,
            Stack = 1,
            ElementalAttribute1 = attributeId
        };
}
