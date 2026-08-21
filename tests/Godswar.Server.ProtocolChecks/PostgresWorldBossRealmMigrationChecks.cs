using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresWorldBossRealmMigrationChecks
{
    public const string CheckName =
        "PostgreSQL realm-scoped world-boss migration contract";

    private const string MigrationId =
        "20260820_095_realm_scoped_world_boss_control";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;

        Check.True(
            sql.Contains(
                "ADD COLUMN realm_id integer",
                StringComparison.Ordinal) &&
            sql.Contains(
                "SET realm_id = 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ALTER COLUMN realm_id SET NOT NULL",
                StringComparison.Ordinal),
            "legacy mutable control rows are assigned to Tempest before realm is required");
        Check.True(
            sql.Contains(
                "FOREIGN KEY (realm_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.server(id)",
                StringComparison.Ordinal),
            "world-boss controls retain durable realm catalog integrity");
        Check.True(
            sql.Contains(
                "PRIMARY KEY (realm_id, map_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE INDEX ix_faction_area_experience_control_active",
                StringComparison.Ordinal),
            "mutable control ownership and its active index are realm/map scoped");
        Check.True(
            !sql.Contains(
                "gameplay_world_boss_definitions",
                StringComparison.Ordinal),
            "shared authored world-boss content remains global");

        return Task.CompletedTask;
    }
}
