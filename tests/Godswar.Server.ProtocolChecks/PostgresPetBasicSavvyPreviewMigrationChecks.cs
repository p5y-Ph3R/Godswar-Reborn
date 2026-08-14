using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetBasicSavvyPreviewMigrationChecks
{
    public const string CheckName =
        "PostgreSQL Fairy Basic-Savvy preview migration safety";
    private const string MigrationId =
        "20260812_080_pet_basic_savvy_preview";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var migration = migrations.Single(value => value.Id == MigrationId);
        var previous = migrations.Single(value =>
            value.Id == "20260812_079_pet_growth_preview");
        var migrationIndex = migrations
            .Select((value, index) => (value, index))
            .Single(entry => entry.value == migration).index;
        var previousIndex = migrations
            .Select((value, index) => (value, index))
            .Single(entry => entry.value == previous).index;
        Check.Equal(
            previousIndex + 1,
            migrationIndex,
            "Fairy preview migration follows Phoenix preview migration");

        var sql = migration.Sql;
        Check.True(
            sql.Contains(
                "DROP CONSTRAINT ck_pet_stat_savvy_progression",
                StringComparison.Ordinal) &&
            sql.Contains(
                "birth_total IS DISTINCT FROM baseline_total",
                StringComparison.Ordinal) &&
            sql.Contains("birth_count <> 6", StringComparison.Ordinal) &&
            sql.Contains("basic_count <> 6", StringComparison.Ordinal) &&
            sql.Contains(
                "DEFERRABLE INITIALLY DEFERRED",
                StringComparison.Ordinal) &&
            sql.Contains(
                "OLD.pet_id IS DISTINCT FROM NEW.pet_id",
                StringComparison.Ordinal),
            "migration replaces the per-stat hatch floor with null-safe aggregate provenance guards");

        Check.True(
            sql.Contains(
                "CREATE TABLE public.character_pet_basic_savvy_previews",
                StringComparison.Ordinal) &&
            sql.Contains(
                "user_id integer PRIMARY KEY",
                StringComparison.Ordinal) &&
            sql.Contains(
                "expected_stat_revisions bigint[] NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "expected_basic_total numeric(18,6) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet_basic_savvy_array_has_hundredths",
                StringComparison.Ordinal) &&
            sql.Contains(
                "pet_basic_savvy_array_total",
                StringComparison.Ordinal) &&
            sql.Contains(
                "policy_version = 'fairy-basic-savvy-v1'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "primary_focus <> secondary_focus",
                StringComparison.Ordinal),
            "preview rows bind six revisions, exact totals, hundredths, and auditable policy metadata");

        Check.True(
            sql.Contains("'reset_basic_savvy'", StringComparison.Ordinal) &&
            sql.Contains(
                "'pet_basic_savvy_reset'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE OR REPLACE VIEW public.pet_durable_command_evidence",
                StringComparison.Ordinal),
            "migration admits the dedicated pet audit operation and durable evidence family");

        Check.True(
            !sql.Contains(
                "SET birth_initial_savvy",
                StringComparison.Ordinal) &&
            !sql.Contains(
                "SET rarity_added_savvy",
                StringComparison.Ordinal),
            "migration never rewrites immutable hatch or rarity provenance");
        return Task.CompletedTask;
    }
}
