using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetInitialSavvyV3MigrationChecks
{
    private const string MigrationId =
        "20260811_070_pet_initial_savvy_policy_v3";
    private const string MigrationChecksum =
        "34639A81E5021FA2D21255A875DA929ED3221781CAA7AEFF10D11066AB4301E0";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var index = catalog
            .Select((migration, position) => (migration, position))
            .Single(entry => entry.migration.Id == MigrationId)
            .position;
        var migration = catalog[index];
        var sql = migration.Sql;

        Check.Equal(
            MigrationChecksum,
            migration.Checksum,
            "pet initial-Savvy V3 migration checksum is pinned");
        Check.Equal(
            "20260810_069_pet_growth_savvy_semantics_v2",
            catalog[index - 1].Id,
            "V3 Savvy migration follows corrected field semantics");

        CheckArchive(sql);
        CheckPreflight(sql);
        CheckValuePreservation(sql);
        CheckPostMigrationParity(sql);
        return Task.CompletedTask;
    }

    private static void CheckArchive(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.pet_initial_savvy_v3_legacy_archive",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (migration_id, pet_id_snapshot)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_initial_savvy_policy_version varchar(32) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_rarity_savvy_policy_version varchar(32) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_stat_rows jsonb NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "jsonb_array_length(old_stat_rows) = 6",
                StringComparison.Ordinal),
            "V3 migration keeps a complete recovery before-image");
    }

    private static void CheckPreflight(string sql)
    {
        Check.True(
            sql.Contains(
                "count(stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.birth_initial_savvy IS NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.rarity_added_savvy IS NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.base_growth_rate <= 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "sum(stat.birth_initial_savvy) IS DISTINCT FROM",
                StringComparison.Ordinal) &&
            sql.Contains(
                "sum(stat.rarity_added_savvy) IS DISTINCT FROM",
                StringComparison.Ordinal) &&
            sql.Contains(
                "without guessing",
                StringComparison.Ordinal),
            "V3 migration fails closed on incomplete baseline state");
    }

    private static void CheckValuePreservation(string sql)
    {
        Check.True(
            sql.Contains(
                "legacy-high-savvy-range-v1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision = archived.old_pet_revision + 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "GET DIAGNOSTICS updated_rows = ROW_COUNT",
                StringComparison.Ordinal) &&
            !sql.Contains(
                "UPDATE public.character_pet_stat_values",
                StringComparison.Ordinal),
            "V3 migration changes only provenance and the parent revision");
    }

    private static void CheckPostMigrationParity(string sql)
    {
        Check.True(
            sql.Contains(
                "pet.initial_savvy_baseline_total <>",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_initial_savvy_baseline_total",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet.rarity_added_savvy_baseline_total <>",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_rarity_savvy_baseline_total",
                StringComparison.Ordinal) &&
            sql.Contains(
                ") <> archived.old_stat_rows",
                StringComparison.Ordinal) &&
            sql.Contains(
                "failed parity validation",
                StringComparison.Ordinal),
            "V3 migration proves parent and six-stat parity after relabeling");
    }
}
