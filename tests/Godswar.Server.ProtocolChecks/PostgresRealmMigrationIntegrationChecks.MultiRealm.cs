using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresRealmMigrationIntegrationChecks
{
    private const string MultiRealmMigrationId =
        "20260820_094_multi_realm_character_authority";

    private static async Task AssertAppliedMultiRealmAuthorityAsync(
        NpgsqlDataSource dataSource,
        RealmFixture fixture)
    {
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM public.schema_migrations
                WHERE migration_id =
                    '20260820_094_multi_realm_character_authority';
                """),
            "multi-realm authority migration is recorded once");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM public.server
                WHERE id = 1
                  AND name = 'Tempest'
                  AND enabled
                  AND display_order = 1
                  AND game_port = 7000
                  AND recommended;
                """),
            "Tempest is the enabled recommended catalog realm");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM public.server
                WHERE id = 2
                  AND name = 'Dwargon'
                  AND identifier = 'DWG3jcIzqGgKvOf1dbYZKC8cS'
                  AND ip_address = '0.0.0.0'
                  AND server_limit = 250
                  AND NOT enabled
                  AND display_order = 2
                  AND game_port = 7000
                  AND NOT recommended;
                """),
            "Dwargon remains a disabled non-routable draft");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM public.account_realm membership
                JOIN public.accounts account_row
                  ON account_row.id = membership.account_id
                WHERE membership.account_id = @accountId
                  AND membership.realm_id = 1
                  AND membership.character_lifecycle_version =
                      account_row.character_lifecycle_version
                  AND membership.character_slot_limit = 1;
                """,
                ("accountId", fixture.AccountId)),
            "existing account lifecycle authority is backfilled to Tempest");
        Check.True(
            await IsRealmColumnDefaultNullAsync(dataSource),
            "new character writes must choose a realm explicitly");
        Check.Equal(
            0,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM pg_constraint
                WHERE conrelid = 'public.character_base'::regclass
                  AND conname = 'ck_character_base_tempest_realm';
                """),
            "the former Tempest-only character constraint is removed");
        Check.Equal(
            1,
            await ReadInt32Async(
                dataSource,
                """
                SELECT count(*)::integer
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = 'character_base'
                  AND indexname =
                      'ux_character_base_active_account_realm_slot';
                """),
            "active character slots are unique within each account realm");

        await AssertRealmScopedSlotsAndGlobalNamesAsync(dataSource, fixture);
    }

    private static async Task AssertRealmScopedSlotsAndGlobalNamesAsync(
        NpgsqlDataSource dataSource,
        RealmFixture fixture)
    {
        var accountId = await InsertAccountAsync(
            dataSource,
            $"b18a_multi_{fixture.Token}");
        try
        {
            _ = await InsertCharacterAsync(
                dataSource,
                accountId,
                $"B18AT{fixture.Token}",
                "1");
            _ = await InsertCharacterAsync(
                dataSource,
                accountId,
                $"B18AW{fixture.Token}",
                "2");
            Check.Equal(
                2,
                await ReadInt32Async(
                    dataSource,
                    """
                    SELECT count(*)::integer
                    FROM public.character_base
                    WHERE account_id = @accountId
                      AND character_slot = 0
                      AND lifecycle_state = 'active';
                    """,
                    ("accountId", accountId)),
                "one account can own slot zero in both realms");

            await AssertInsertRejectedAsync(
                dataSource,
                fixture.AccountId,
                $"B18AT{fixture.Token}",
                "2",
                PostgresErrorCodes.UniqueViolation,
                "character names remain globally unique across realms");
        }
        finally
        {
            await DeleteAccountAsync(dataSource, accountId);
        }
    }

    private static async Task<bool> IsRealmColumnDefaultNullAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT column_default IS NULL
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'character_base'
              AND column_name = 'server_id';
            """);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ReadInt32Async(
        NpgsqlDataSource dataSource,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(
                parameter.Name,
                parameter.Value);
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
