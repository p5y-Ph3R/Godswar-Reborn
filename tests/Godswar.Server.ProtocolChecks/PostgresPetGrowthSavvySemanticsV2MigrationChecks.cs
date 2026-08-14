using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetGrowthSavvySemanticsV2MigrationChecks
{
    private const string MigrationId =
        "20260810_069_pet_growth_savvy_semantics_v2";
    private const string MigrationChecksum =
        "EB4534046BE85DDEB550C92F187B2120FC2282B4737E9E1DA37459A17C8BBB12";

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
            "pet growth/Savvy v2 migration checksum is pinned");
        Check.Equal(
            69,
            index,
            "pet growth/Savvy v2 precedes the V3 Savvy policy migration");
        Check.Equal(
            "20260810_068_pet_point_reset_dialogue",
            catalog[index - 1].Id,
            "pet growth/Savvy v2 follows migration 068");
        Check.Equal(
            "20260811_070_pet_initial_savvy_policy_v3",
            catalog[index + 1].Id,
            "the V3 Savvy policy follows corrected field semantics");

        CheckArchive(sql);
        CheckStrictPreflight(sql);
        CheckProgressionPreservation(sql);
        CheckProvenanceAndConstraints(sql);
        CheckPostMigrationParity(sql);
        return Task.CompletedTask;
    }

    private static void CheckArchive(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.pet_growth_savvy_semantics_v2_archive",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (migration_id, pet_id_snapshot, stat_code)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_initial_savvy numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_added_savvy numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_birth_initial_savvy numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_rarity_added_savvy numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_initial_savvy_source_version varchar(32) NOT NULL",
                StringComparison.Ordinal),
            "migration archives all values needed for forward recovery");
    }

    private static void CheckStrictPreflight(string sql)
    {
        Check.True(
            sql.Contains(
                "initial_savvy_source_version <>",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet.rarity_added_savvy_baseline_total IS NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet.rarity_added_savvy_policy_version <>",
                StringComparison.Ordinal) &&
            sql.Contains(
                "count(stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "count(DISTINCT stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.birth_initial_savvy IS DISTINCT FROM",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.initial_savvy <",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.added_savvy <",
                StringComparison.Ordinal) &&
            sql.Contains(
                "sum(stat.rarity_added_savvy) <>",
                StringComparison.Ordinal) &&
            sql.Contains(
                "cannot be reconciled without guessing",
                StringComparison.Ordinal) &&
            !sql.Contains(
                "count(DISTINCT stat.rarity_added_savvy) < 2",
                StringComparison.Ordinal),
            "migration rejects incomplete, negative, or ambiguous legacy state");
    }

    private static void CheckProgressionPreservation(string sql)
    {
        Check.True(
            sql.Contains(
                "initial_savvy =",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_rarity_added_savvy +",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_initial_savvy -",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_birth_initial_savvy",
                StringComparison.Ordinal) &&
            sql.Contains(
                "added_savvy =",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_base_growth_rate +",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_added_savvy -",
                StringComparison.Ordinal) &&
            sql.Contains(
                "birth_initial_savvy =",
                StringComparison.Ordinal) &&
            Count(
                sql,
                "archived.old_rarity_added_savvy") >= 4 &&
            sql.Contains(
                "revision = archived.old_stat_revision + 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision = archived.old_pet_revision + 1",
                StringComparison.Ordinal) &&
            Count(sql, "GET DIAGNOSTICS updated_rows = ROW_COUNT") == 2,
            "migration preserves both progression deltas and advances each revision once");
    }

    private static void CheckProvenanceAndConstraints(string sql)
    {
        Check.True(
            sql.Contains(
                "initial_savvy_baseline_total =",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_rarity_savvy_baseline_total",
                StringComparison.Ordinal) &&
            sql.Contains(
                "initial_savvy_policy_version =",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.old_rarity_savvy_policy_version",
                StringComparison.Ordinal) &&
            Count(sql, "savvy-plus-growth-v2") >= 3 &&
            sql.Contains(
                "DROP CONSTRAINT ck_pet_stat_growth_x1_birth_baseline",
                StringComparison.Ordinal) &&
            sql.Contains(
                "DROP CONSTRAINT ck_pet_stat_added_savvy_progression",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD CONSTRAINT ck_pet_stat_savvy_birth_baseline",
                StringComparison.Ordinal) &&
            sql.Contains(
                "birth_initial_savvy = rarity_added_savvy",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD CONSTRAINT ck_pet_stat_added_value_progression",
                StringComparison.Ordinal) &&
            sql.Contains(
                "added_savvy >= base_growth_rate",
                StringComparison.Ordinal),
            "migration installs the corrected Savvy and Added-value invariants");
    }

    private static void CheckPostMigrationParity(string sql)
    {
        Check.True(
            sql.Contains(
                "The obsolete growth-x1-v1 provenance remains",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.revision <> archived.old_stat_revision + 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet.revision <> archived.old_pet_revision + 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "failed parity validation",
                StringComparison.Ordinal) &&
            sql.Contains(
                "VALIDATE CONSTRAINT ck_pet_stat_savvy_birth_baseline",
                StringComparison.Ordinal) &&
            sql.Contains(
                "VALIDATE CONSTRAINT ck_pet_stat_added_value_progression",
                StringComparison.Ordinal),
            "migration validates exact archive parity before constraints become trusted");
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(
                   fragment,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }
}
