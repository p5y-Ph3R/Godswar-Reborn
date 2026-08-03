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
                .SequenceEqual(Enumerable.Range(480, 21)) &&
            ElementalAttributeCatalog.All
                .Select(static value => value.StoneItemId)
                .SequenceEqual(Enumerable.Range(16300, 21)
                    .Select(static value => checked((uint)value))),
            "attribute and stone IDs are consecutive one-to-one ranges");
        Check.Equal(
            "Fire Power Stone",
            ElementalAttributeCatalog.All[0].DisplayName,
            "first elemental stone name");
        Check.Equal(
            "Dark Penetration Stone",
            ElementalAttributeCatalog.All[^1].DisplayName,
            "last elemental stone name");

        Check.Equal(21, ElementalItemContentBaseline.Attributes.Count,
            "immutable elemental attribute policy count");
        for (var index = 0; index < 21; index++)
        {
            var policy = ElementalItemContentBaseline.Attributes[index];
            var firstDistribution = checked((short)(391 + (index * 2)));
            Check.True(
                policy.Id == 480 + index &&
                policy.Distribution.SequenceEqual(
                    [firstDistribution, checked((short)(firstDistribution + 1))]) &&
                policy.StatType == 29 + (index % 3) &&
                policy.Percent &&
                policy.MaxLevel == 25,
                $"elemental attribute {policy.Id} has its locked family and distribution pair");
        }
    }

    private static void CheckGradeValues()
    {
        Check.True(
            ElementalAttributeCatalog.TryGetAttribute(480, out var power) &&
            power.ValueAtGrade(1) == 40 &&
            power.ValueAtGrade(25) == 1_000,
            "Power progresses from 40 to 1000 basis points");
        Check.True(
            ElementalAttributeCatalog.TryGetAttribute(481, out var resistance) &&
            resistance.ValueAtGrade(1) == 40 &&
            resistance.ValueAtGrade(25) == 1_000,
            "Resistance progresses from 40 to 1000 basis points");
        Check.True(
            ElementalAttributeCatalog.TryGetAttribute(482, out var penetration) &&
            penetration.ValueAtGrade(1) == 20 &&
            penetration.ValueAtGrade(25) == 500,
            "Penetration progresses from 20 to 500 basis points");
        Check.Throws<ArgumentOutOfRangeException>(
            () => power.ValueAtGrade(0),
            "typed value projection rejects grade zero");
        Check.Throws<ArgumentOutOfRangeException>(
            () => penetration.ValueAtGrade(26),
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
            var raw = profile.RawFor(ElementKind.Fire);
            var effective = profile.EffectiveFor(ElementKind.Fire);
            Check.Equal(expectedCount, profile.CountFor(ElementKind.Fire),
                $"Fire display count at {count} equipped items");
            Check.Equal(expectedThreshold, profile.HighestThresholdFor(ElementKind.Fire),
                $"Fire set threshold at {count} equipped items");
            Check.Equal(count * 40, raw.PowerBasisPoints,
                $"Fire raw Power at {count} equipped items");
            Check.Equal(
                (count * 40) + (expectedCount >= 3 ? 200 : 0),
                effective.PowerBasisPoints,
                $"Fire effective Power at {count} equipped items");
            Check.Equal(
                expectedCount >= 6 ? 300 : 0,
                effective.ResistanceBasisPoints,
                $"Fire effective Resistance at {count} equipped items");
            Check.Equal(
                expectedCount >= 10 ? 200 : 0,
                effective.PenetrationBasisPoints,
                $"Fire effective Penetration at {count} equipped items");
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
            profile.RawFor(ElementKind.Fire).ResistanceBasisPoints == 1_000 &&
            profile.CountFor(ElementKind.Water) == 1 &&
            profile.RawFor(ElementKind.Water).PenetrationBasisPoints == 500,
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
                invalid.RawFor(ElementKind.Fire) == default,
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
                invalid.RawFor(ElementKind.Fire) == default,
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
            reloaded.ElementalEquipment.RawFor(ElementKind.Water)
                .PowerBasisPoints == 1_000,
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
