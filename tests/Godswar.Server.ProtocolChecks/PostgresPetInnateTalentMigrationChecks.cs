using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetInnateTalentMigrationChecks
{
    private const string MigrationId =
        "20260811_072_pet_quality_innate_talents";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var index = catalog
            .Select((migration, position) => (migration, position))
            .Single(entry => entry.migration.Id == MigrationId)
            .position;
        var sql = catalog[index].Sql;

        Check.Equal(
            "20260811_071_pet_phoenix_growth_activation",
            catalog[index - 1].Id,
            "innate-talent reconciliation follows Phoenix Growth activation");
        CheckContainsPublicationAuthority(sql);
        CheckContainsDurableReconciliation(sql);
        CheckContainsLegacyItemNeutralization(sql);
        return Task.CompletedTask;
    }

    private static void CheckContainsPublicationAuthority(string sql)
    {
        Check.True(
            sql.Contains(
                "ADD COLUMN innate_talent_mask smallint",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ck_pet_content_aptitude_innate_talents",
                StringComparison.Ordinal) &&
            sql.Contains("WHEN aptitude >= 14 THEN 31", StringComparison.Ordinal) &&
            sql.Contains("WHEN aptitude >= 10 THEN 26", StringComparison.Ordinal) &&
            sql.Contains(
                "innate_talent_mask IS NULL OR",
                StringComparison.Ordinal) &&
            !sql.Contains("NOT VALID", StringComparison.Ordinal),
            "migration publishes the exact aptitude-derived content rule while preserving sealed predecessors");
    }

    private static void CheckContainsDurableReconciliation(string sql)
    {
        Check.True(
            sql.Contains(
                "character_pet_talent_reconciliation_072",
                StringComparison.Ordinal) &&
            sql.Contains(
                "has_owner_merge_talent_before",
                StringComparison.Ordinal) &&
            sql.Contains(
                "contributes_to_character_before",
                StringComparison.Ordinal) &&
            sql.Contains(
                "talent_mask_after IN (0, 26, 31)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "has_owner_merge_talent =",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision = pet.revision + 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ck_character_pets_quality_innate_talents",
                StringComparison.Ordinal) &&
            sql.Contains(
                "failed parity validation",
                StringComparison.Ordinal) &&
            !sql.Contains("native_genius", StringComparison.OrdinalIgnoreCase),
            "migration archives and reconciles pets without trusting compatibility-only NativeGenius");
    }

    private static void CheckContainsLegacyItemNeutralization(string sql)
    {
        Check.True(
            sql.Contains(
                "stats = stats - 'Use' - 'ItemType' - 'Values'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE id BETWEEN 10110 AND 10114",
                StringComparison.Ordinal),
            "legacy talent-stick records are made non-activatable");
    }
}
