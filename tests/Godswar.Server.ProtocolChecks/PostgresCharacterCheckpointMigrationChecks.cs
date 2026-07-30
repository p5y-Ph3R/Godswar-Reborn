using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresCharacterCheckpointMigrationChecks
{
    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate =>
                candidate.Id ==
                "20260730_030_character_checkpoint_versions");

        Check.True(
            migration.Sql.Contains(
                "position_revision bigint NOT NULL DEFAULT 0",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "checkpoint_owner_id uuid",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "checkpoint_owner_generation bigint",
                StringComparison.Ordinal),
            "checkpoint migration adds one position revision and one owner fence");
        Check.True(
            migration.Sql.Contains(
                "ck_character_base_position_revision",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (position_revision >= 0)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ck_character_base_vitals_revision",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (vitals_revision >= 0)",
                StringComparison.Ordinal),
            "checkpoint migration bounds both durable facet revisions");
        Check.True(
            migration.Sql.Contains(
                "ck_character_base_checkpoint_owner_generation",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (checkpoint_owner_generation >= 0)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ck_character_base_checkpoint_owner_pair",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "checkpoint_owner_generation > 0",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "00000000-0000-0000-0000-000000000000",
                StringComparison.Ordinal),
            "checkpoint migration rejects invalid active owner identities");
        Check.True(
            !migration.Sql.Contains(
                "CREATE INDEX",
                StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains(
                "CREATE TABLE",
                StringComparison.OrdinalIgnoreCase),
            "checkpoint migration keeps character_base as the one authority");

        return Task.CompletedTask;
    }
}
