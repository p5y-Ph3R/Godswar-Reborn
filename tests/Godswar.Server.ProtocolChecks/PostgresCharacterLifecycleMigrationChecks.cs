using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresCharacterLifecycleMigrationChecks
{
    private const string MigrationId =
        "20260730_031_character_lifecycle_foundation";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);

        Check.True(
            migration.Sql.Contains(
                "HAVING count(*) > 1",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "Cannot enable SingleCharacterV1",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ERRCODE = '23505'",
                StringComparison.Ordinal),
            "lifecycle migration fails closed on pre-existing active-slot conflicts");
        Check.True(
            migration.Sql.Contains(
                "character_lifecycle_version bigint",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (character_lifecycle_version >= 0)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "SET character_lifecycle_version = 1",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "WHERE character_row.account_id = account_row.id",
                StringComparison.Ordinal),
            "account-slot aggregate version starts at zero or backfills to one");
        Check.True(
            migration.Sql.Contains(
                "character_slot smallint NOT NULL DEFAULT 0",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (character_slot = 0)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "lifecycle_version bigint NOT NULL DEFAULT 1",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CHECK (lifecycle_version >= 1)",
                StringComparison.Ordinal),
            "lifecycle migration fixes the legacy client to one versioned slot");
        Check.True(
            migration.Sql.Contains(
                "lifecycle_state IN ('active', 'deleted')",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "lifecycle_state = 'active'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "lifecycle_state = 'deleted'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "deleted_at < restore_until",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "restore_until <= purge_after",
                StringComparison.Ordinal),
            "lifecycle timestamps form a bounded active/deleted state machine");
        Check.True(
            migration.Sql.Contains(
                "ck_character_base_deleted_owner",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "checkpoint_owner_id IS NULL",
                StringComparison.Ordinal),
            "deleted characters cannot retain a live checkpoint owner");
        Check.True(
            migration.Sql.Contains(
                "CREATE UNIQUE INDEX ux_character_base_active_account_slot",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ON public.character_base (account_id, character_slot)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "WHERE lifecycle_state = 'active'",
                StringComparison.Ordinal),
            "only an active character occupies the account slot");
        Check.True(
            migration.Sql.Contains(
                "ix_character_base_deleted_account_slot",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ix_character_base_purge_due",
                StringComparison.Ordinal),
            "restore lookup and controlled purge each have bounded partial indexes");
        Check.True(
            !migration.Sql.Contains(
                "CREATE TABLE",
                StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains(
                "command_inbox",
                StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains(
                "command_audit",
                StringComparison.OrdinalIgnoreCase),
            "B11 reuses the B08 durable inbox and audit authorities");
        Check.True(
            PostgresCharacterLifecycleMigrationIntegrationChecks
                .IsDisposableDatabaseName(
                    "godswar_b03_0123456789_lifecycle_preflight") &&
            PostgresCharacterLifecycleMigrationIntegrationChecks
                .IsDisposableDatabaseName(
                    "godswar_b11_deadbeef_pre031") &&
            !PostgresCharacterLifecycleMigrationIntegrationChecks
                .IsDisposableDatabaseName("postgres") &&
            !PostgresCharacterLifecycleMigrationIntegrationChecks
                .IsDisposableDatabaseName(
                    "godswar_b03_0123456789_smoke_24") &&
            !PostgresCharacterLifecycleMigrationIntegrationChecks
                .IsDisposableDatabaseName("godswar_b11_"),
            "lifecycle migration integration accepts only bounded disposable databases");

        return Task.CompletedTask;
    }
}
