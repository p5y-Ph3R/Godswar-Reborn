using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetMagicJadeAppearanceMigrationChecks
{
    public const string CheckName =
        "PostgreSQL Magic Jade appearance-group migration";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var position = migrations
            .Select((migration, index) => (migration, index))
            .Single(static value => value.migration.Id ==
                "20260812_085_pet_magic_jade_appearance_groups");
        var sql = position.migration.Sql;
        Check.True(
            migrations[position.index - 1].Id ==
                "20260812_084_pet_merge_savvy_lookup_content" &&
            sql.Contains("magic_jade_item_id = 11049 + species_id",
                StringComparison.Ordinal) &&
            sql.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal) &&
            sql.Contains("(revision, magic_jade_item_id)",
                StringComparison.Ordinal) &&
            sql.Contains("pet_content_magic_jade_appearance_groups",
                StringComparison.Ordinal) &&
            sql.Contains("current_pet_magic_jade_appearance_groups",
                StringComparison.Ordinal) &&
            sql.Contains("pet_content_merge_rank_species_factors",
                StringComparison.Ordinal) &&
            sql.Contains("pet_content_merge_savvy_lookup",
                StringComparison.Ordinal) &&
            sql.Contains(
                "five_spirits.minimum_percent + 50) / 100.0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "five_spirits.maximum_percent + 50) / 100.0",
                StringComparison.Ordinal) &&
            sql.Contains("appearance_provenance", StringComparison.Ordinal) &&
            sql.Contains("merge_policy_provenance", StringComparison.Ordinal),
            "migration 085 exposes unique versioned Magic Jade cap groups");
        var commandMigration = migrations.Single(static migration =>
            migration.Id == "20260812_086_pet_appearance_change");
        Check.True(
            migrations[position.index + 1] == commandMigration &&
            commandMigration.Sql.Contains(
                "'change_appearance'",
                StringComparison.Ordinal) &&
            commandMigration.Sql.Contains(
                "'pet_appearance_change'",
                StringComparison.Ordinal) &&
            commandMigration.Sql.Contains(
                "ck_pet_operation_audit_operation_v6",
                StringComparison.Ordinal),
            "migration 086 admits durable Magic Jade command evidence");
        return Task.CompletedTask;
    }
}
