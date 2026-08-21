using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresMultiRealmMigrationChecks
{
    public const string CheckName =
        "PostgreSQL multi-realm character authority migration contract";

    private const string MigrationId =
        "20260820_094_multi_realm_character_authority";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;

        Check.Equal(
            2,
            RealmId.Dwargon.Value,
            "runtime Dwargon identity matches the seeded realm row");
        Check.True(
            sql.Contains("ADD COLUMN enabled boolean", StringComparison.Ordinal) &&
            sql.Contains("ADD COLUMN display_order integer", StringComparison.Ordinal) &&
            sql.Contains("ADD COLUMN game_port integer", StringComparison.Ordinal) &&
            sql.Contains("DEFAULT 7000", StringComparison.Ordinal) &&
            sql.Contains("ADD COLUMN recommended boolean", StringComparison.Ordinal),
            "realm catalog exposes generic lifecycle and routing metadata");
        Check.True(
            sql.Contains("2,\n    'Dwargon'", StringComparison.Ordinal) &&
            sql.Contains("'DWG3jcIzqGgKvOf1dbYZKC8cS'", StringComparison.Ordinal) &&
            sql.Contains("'0.0.0.0'", StringComparison.Ordinal) &&
            sql.Contains("250,\n    false,\n    2,\n    7000,\n    false", StringComparison.Ordinal),
            "Dwargon is a disabled draft with an opaque legacy token and no local endpoint");
        Check.True(
            sql.Contains("CREATE TABLE public.account_realm", StringComparison.Ordinal) &&
            sql.Contains("PRIMARY KEY (account_id, realm_id)", StringComparison.Ordinal) &&
            sql.Contains("character_lifecycle_version bigint", StringComparison.Ordinal) &&
            sql.Contains("character_slot_limit smallint", StringComparison.Ordinal) &&
            sql.Contains("account_row.character_lifecycle_version", StringComparison.Ordinal),
            "account-to-realm membership owns independent lifecycle versions and backfills Tempest");
        Check.True(
            sql.Contains("DROP CONSTRAINT ck_character_base_tempest_realm", StringComparison.Ordinal) &&
            sql.Contains("ALTER COLUMN server_id DROP DEFAULT", StringComparison.Ordinal) &&
            sql.Contains("ux_character_base_active_account_realm_slot", StringComparison.Ordinal) &&
            sql.Contains("account_id,\n        server_id,\n        character_slot", StringComparison.Ordinal),
            "character slots become explicitly realm-scoped");
        Check.True(
            !sql.Contains("ux_character_base_name", StringComparison.Ordinal),
            "global character-name uniqueness remains unchanged");
        Check.True(
            sql.Contains("'character_lifecycle_v1'", StringComparison.Ordinal) &&
            sql.Contains("'character_lifecycle_v2'", StringComparison.Ordinal) &&
            sql.Contains("'account_character_slot'", StringComparison.Ordinal) &&
            sql.Contains("'account_realm_character_slot'", StringComparison.Ordinal),
            "outbox baseline authorization preserves Tempest history and scopes new realms");

        return Task.CompletedTask;
    }
}
