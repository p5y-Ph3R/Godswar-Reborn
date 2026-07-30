using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresRealmMigrationChecks
{
    public const string CheckName =
        "PostgreSQL Tempest realm migration contract";

    private const string MigrationId =
        "20260731_035_tempest_realm_authority";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;

        Check.Equal(
            1,
            RealmId.Tempest.Value,
            "runtime Tempest identity matches the legacy server row");
        Check.True(
            sql.Contains(
                "FROM public.server realm",
                StringComparison.Ordinal) &&
            sql.Contains(
                "realm.id = 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "realm.name = 'Tempest'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'KAL3jcIzqGgKvOf1dbYZKC8cS'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "USING ERRCODE = '23514'",
                StringComparison.Ordinal),
            "migration verifies the exact historical Tempest identity");
        Check.True(
            sql.Contains(
                "character_row.server_id IS NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "character_row.server_id <> 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "realm-scoped lifecycle contract",
                StringComparison.Ordinal),
            "migration rejects unsupported pre-existing character realms");
        Check.True(
            sql.Contains(
                "constraint_row.contype = 'f'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "constraint_row.convalidated",
                StringComparison.Ordinal) &&
            sql.Contains(
                "source_column.attname = 'server_id'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "target_column.attname = 'id'",
                StringComparison.Ordinal),
            "migration requires the validated character-to-realm foreign key");
        Check.True(
            sql.Contains(
                "SET server_id = 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE server_id IS NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ALTER COLUMN server_id SET DEFAULT 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ALTER COLUMN server_id SET NOT NULL",
                StringComparison.Ordinal),
            "legacy unassigned characters become Tempest before realm is required");
        Check.True(
            sql.Contains(
                "ck_character_base_tempest_realm",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (server_id = 1)",
                StringComparison.Ordinal),
            "single-realm runtime cannot silently create another realm character");
        Check.True(
            sql.Contains(
                "CREATE INDEX IF NOT EXISTS ix_character_base_server",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ON public.character_base (server_id)",
                StringComparison.Ordinal),
            "realm-owned character lookups retain their bounded index");
        Check.True(
            !sql.Contains(
                "CREATE TABLE",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "DROP INDEX ux_character_base_name",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "ux_character_base_active_account_slot",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "character_items",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "character_pets",
                StringComparison.OrdinalIgnoreCase),
            "B18A neither duplicates realm ownership nor changes lifecycle uniqueness");

        return Task.CompletedTask;
    }
}
