using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class CompactItemClassAttributeChecks
{
    public const string CheckName =
        "Class Suit compact-item compatibility and normalization";

    public static Task RunAsync()
    {
        CheckLegacyCanonicalFormIsStable();
        CheckExtendedRoundTrip();
        CheckLegacyClassAttributesNormalize();
        CheckInvalidLegacyShapeRemainsRejectable();
        CheckAuthoritativeProjectionCoverage();
        return Task.CompletedTask;
    }

    private static void CheckLegacyCanonicalFormIsStable()
    {
        var item = Equipment() with
        {
            Attribute1 = 10,
            Attribute2 = 40,
            Attribute3 = 60,
            Attribute4 = 80,
            Attribute5 = 130,
            AttributeLevel1 = 1,
            AttributeLevel2 = 2,
            AttributeLevel3 = 3,
            AttributeLevel4 = 4,
            AttributeLevel5 = 5
        };
        var legacy = item.ToCompactString();

        Check.Equal(
            30,
            legacy.Trim('[', ']').Split(',').Length,
            "an item without Class Suit attributes retains 30 native compact fields");
        Check.Equal(
            legacy,
            CompactItemEntry.Parse(legacy).ToCompactString(),
            "legacy compact serialization remains byte-for-byte canonical");
    }

    private static void CheckExtendedRoundTrip()
    {
        var item = Equipment() with
        {
            Attribute1 = 10,
            Attribute2 = 40,
            Attribute3 = 60,
            Attribute4 = 80,
            Attribute5 = 130,
            ClassAttribute1 = 200,
            ClassAttribute2 = 210
        };
        var compact = item.ToCompactString();
        var parsed = CompactItemEntry.Parse(compact);

        Check.Equal(
            32,
            compact.Trim('[', ']').Split(',').Length,
            "Class Suit attributes append exactly two compact fields");
        Check.Equal(200, parsed.ClassAttribute1 ?? -1, "first Class Suit field round-trips");
        Check.Equal(210, parsed.ClassAttribute2 ?? -1, "second Class Suit field round-trips");
        Check.Equal(compact, parsed.ToCompactString(), "extended compact form is canonical");
    }

    private static void CheckLegacyClassAttributesNormalize()
    {
        var legacy = (Equipment() with
        {
            Attribute1 = 40,
            Attribute2 = 200,
            Attribute3 = 60,
            Attribute4 = 210,
            AttributeLevel1 = 2,
            AttributeLevel2 = 1,
            AttributeLevel3 = 3,
            AttributeLevel4 = 1
        }).ToCompactString();
        var normalized = CompactItemEntry.Parse(legacy);

        Check.True(
            normalized.Attribute1 == 40 &&
            normalized.Attribute2 == 60 &&
            normalized.Attribute3 is null &&
            normalized.Attribute4 is null &&
            normalized.Attribute5 is null,
            "legacy Class Suit IDs are removed and ordinary fields compact");
        Check.True(
            normalized.AttributeLevel1 == 2 &&
            normalized.AttributeLevel2 == 3 &&
            normalized.AttributeLevel3 is null,
            "ordinary attribute levels remain paired during normalization");
        Check.True(
            normalized.ClassAttribute1 == 200 &&
            normalized.ClassAttribute2 == 210,
            "legacy Class Suit IDs preserve their first-seen order");
    }

    private static void CheckInvalidLegacyShapeRemainsRejectable()
    {
        var duplicateLegacy = (Equipment() with
        {
            Attribute1 = 200,
            Attribute2 = 200
        }).ToCompactString();
        var parsed = CompactItemEntry.Parse(duplicateLegacy);

        Check.True(
            parsed.Attribute1 == 200 &&
            parsed.Attribute2 == 200 &&
            parsed.ClassAttribute1 is null &&
            parsed.ClassAttribute2 is null,
            "duplicate hostile legacy state is preserved for atomic planner rejection");
    }

    private static void CheckAuthoritativeProjectionCoverage()
    {
        Check.True(
            PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias.Contains(
                "ci.class_attribute1",
                StringComparison.Ordinal) &&
            PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias.Contains(
                "ci.class_attribute2",
                StringComparison.Ordinal) &&
            PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias.Contains(
                "WHEN ci.class_attribute1 IS NULL",
                StringComparison.Ordinal),
            "authoritative compact projection carries class fields without changing native-only strings");
        Check.True(
            PostgresCharacterRuntimeItemProjectionSql.CalculatedStatsForCharacter.Contains(
                "(equipment.class_attribute1)",
                StringComparison.Ordinal) &&
            PostgresCharacterRuntimeItemProjectionSql.CalculatedStatsForCharacter.Contains(
                "(equipment.class_attribute2)",
                StringComparison.Ordinal),
            "authoritative stat projection consumes both Class Suit fields");
        Check.True(
            PostgresCharacterRuntimeItemProjectionSql.RankLateralJoinForCharacterAlias.Contains(
                "equipment.class_attribute1 IS NOT NULL",
                StringComparison.Ordinal) &&
            PostgresCharacterRuntimeItemProjectionSql.RankLateralJoinForCharacterAlias.Contains(
                "equipment.class_attribute2 IS NOT NULL",
                StringComparison.Ordinal),
            "authoritative equipment rank counts both Class Suit fields");
    }

    private static CompactItemEntry Equipment() =>
        CompactItemEntry.Empty with
        {
            Id = 1035,
            Quality = 20,
            Grade = 25,
            Bound = 1,
            Stack = 1
        };
}
