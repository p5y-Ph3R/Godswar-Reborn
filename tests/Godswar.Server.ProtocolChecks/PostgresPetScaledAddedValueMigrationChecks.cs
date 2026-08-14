using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetScaledAddedValueMigrationChecks
{
    private const string MigrationId =
        "20260811_078_pet_scaled_added_value_v3";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var migration = migrations.Single(value => value.Id == MigrationId);
        var previous = migrations.Single(value =>
            value.Id == "20260811_077_pet_durable_evidence_v3");
        var migrationIndex = migrations
            .Select((value, index) => (value, index))
            .Single(entry => entry.value == migration)
            .index;
        var previousIndex = migrations
            .Select((value, index) => (value, index))
            .Single(entry => entry.value == previous)
            .index;
        Check.True(
            migrationIndex == previousIndex + 1,
            "scaled Added-value V3 is appended after the applied pet migrations");

        var sql = migration.Sql;
        Check.True(
            sql.Contains(
                "pet_scaled_added_value_v3_archive",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_initial_savvy numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_growth_acceleration numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "old_pet_revision bigint NOT NULL",
                StringComparison.Ordinal),
            "migration archives complete stat and parent before-images");

        Check.True(
            sql.Contains(
                "pet.completed_pet_merges > 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "historical Merge gains that cannot be reconstructed safely",
                StringComparison.Ordinal),
            "migration fails closed instead of guessing historical Merge gains");

        Check.True(
            sql.Contains(
                "DELETE FROM public.character_pet_character_bonuses bonus",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE bonus.pet_id = affected.pet_id",
                StringComparison.Ordinal) &&
            sql.Contains(
                "retained a stale owner-Merge bonus projection",
                StringComparison.Ordinal),
            "migration invalidates and verifies derived owner-Merge bonuses");

        Check.True(
            sql.Contains(
                "initial_savvy = archived.old_birth_initial_savvy",
                StringComparison.Ordinal) &&
            Count(sql, "archived.old_base_growth_rate +") >= 2 &&
            Count(sql, "archived.old_growth_acceleration") >= 4 &&
            Count(sql, ") * archived.old_level") >= 2,
            "migration restores Basic and scales effective Growth by pet level");

        Check.True(
            sql.Contains(
                "'basic-plus-scaled-growth-v3'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet.revision <> archived.old_pet_revision + 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stat.revision <> archived.old_stat_revision + 1",
                StringComparison.Ordinal),
            "migration records V3 provenance and validates exact revisions");

        Check.True(
            sql.Contains(
                "DROP CONSTRAINT ck_character_pets_savvy_provenance",
                StringComparison.Ordinal) &&
            sql.Contains(
                "DROP CONSTRAINT ck_pet_stat_added_value_progression",
                StringComparison.Ordinal) &&
            sql.Contains(
                "added_savvy >=",
                StringComparison.Ordinal) &&
            sql.Contains(
                "base_growth_rate + growth_acceleration",
                StringComparison.Ordinal) &&
            sql.Contains(
                "VALIDATE CONSTRAINT ck_pet_stat_added_value_progression",
                StringComparison.Ordinal),
            "migration replaces and validates the Savvy constraints");

        Check.True(
            sql.Contains(
                "Obsolete savvy-plus-growth-v2 provenance remains",
                StringComparison.Ordinal) &&
            sql.Contains(
                "failed scaled Added-value V3 parity validation",
                StringComparison.Ordinal) &&
            sql.Contains(
                "has invalid scaled Added-value V3 state",
                StringComparison.Ordinal),
            "migration validates complete conversion and archive parity");
        return Task.CompletedTask;
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
