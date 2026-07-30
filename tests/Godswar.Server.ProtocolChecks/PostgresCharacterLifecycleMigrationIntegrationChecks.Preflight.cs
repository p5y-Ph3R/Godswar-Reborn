using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleMigrationIntegrationChecks
{
    private const string MigrationId =
        "20260730_031_character_lifecycle_foundation";

    private static async Task PrepareSchemaAsync(
        NpgsqlDataSource dataSource)
    {
        var runner = new PostgresSchemaMigrationRunner(dataSource);
        if (await IsMigrationAppliedAsync(dataSource))
        {
            return;
        }

        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(31)
                .ToArray());
        var accountId = await InsertDuplicateFixtureAsync(
            dataSource);
        try
        {
            await AssertDuplicatePreflightFailsAsync(
                runner,
                dataSource);
            await DeleteOneDuplicateAsync(
                dataSource,
                accountId);
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All);
            await AssertExistingAccountBackfillAsync(
                dataSource,
                accountId);
        }
        finally
        {
            await DeleteAccountAsync(dataSource, accountId);
        }
    }

    private static async Task<int> InsertDuplicateFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        await using var command = dataSource.CreateCommand("""
            WITH account_row AS (
                INSERT INTO public.accounts (username, password)
                VALUES (@username, '')
                RETURNING id
            )
            INSERT INTO public.character_base (
                account_id,
                name
            )
            SELECT
                account_row.id,
                character_name
            FROM account_row
            CROSS JOIN (
                VALUES
                    (@firstName),
                    (@secondName)
            ) names(character_name)
            RETURNING account_id;
            """);
        command.Parameters.AddWithValue(
            "username",
            $"b11_preflight_{token}");
        command.Parameters.AddWithValue(
            "firstName",
            $"B11P{token}A");
        command.Parameters.AddWithValue(
            "secondName",
            $"B11P{token}B");
        var accountIds = new List<int>(2);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            accountIds.Add(reader.GetInt32(0));
        }

        Check.Equal(
            2,
            accountIds.Count,
            "preflight fixture creates two legacy character rows");
        Check.Equal(
            accountIds[0],
            accountIds[1],
            "preflight fixture rows share one legacy account");
        return accountIds[0];
    }

    private static async Task AssertDuplicatePreflightFailsAsync(
        PostgresSchemaMigrationRunner runner,
        NpgsqlDataSource dataSource)
    {
        try
        {
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All);
            throw new InvalidOperationException(
                "Duplicate legacy characters bypassed B11 preflight.");
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.UniqueViolation &&
                  exception.MessageText.Contains(
                      "SingleCharacterV1",
                      StringComparison.Ordinal))
        {
        }

        Check.True(
            !await IsMigrationAppliedAsync(dataSource),
            "failed lifecycle migration leaves no history row");
        Check.True(
            !await ColumnExistsAsync(
                dataSource,
                "accounts",
                "character_lifecycle_version"),
            "failed lifecycle migration rolls back account columns");
        Check.True(
            !await ColumnExistsAsync(
                dataSource,
                "character_base",
                "character_slot"),
            "failed lifecycle migration rolls back character columns");
    }

    private static async Task DeleteOneDuplicateAsync(
        NpgsqlDataSource dataSource,
        int accountId)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.character_base
            WHERE id = (
                SELECT max(id)
                FROM public.character_base
                WHERE account_id = @accountId
            );
            """);
        command.Parameters.AddWithValue(
            "accountId",
            accountId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "operator remediation removes one duplicate row");
    }

    private static async Task AssertExistingAccountBackfillAsync(
        NpgsqlDataSource dataSource,
        int accountId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                account_row.character_lifecycle_version,
                character_row.character_slot,
                character_row.lifecycle_state,
                character_row.lifecycle_version
            FROM public.accounts account_row
            JOIN public.character_base character_row
              ON character_row.account_id = account_row.id
            WHERE account_row.id = @accountId;
            """);
        command.Parameters.AddWithValue(
            "accountId",
            accountId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "remediated legacy character survives migration");
        Check.Equal(
            1L,
            reader.GetInt64(0),
            "existing account lifecycle aggregate backfills to one");
        Check.Equal(
            (short)0,
            reader.GetInt16(1),
            "existing character backfills to native slot zero");
        Check.Equal(
            "active",
            reader.GetString(2),
            "existing character backfills active");
        Check.Equal(
            1L,
            reader.GetInt64(3),
            "existing character lifecycle version backfills to one");
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        NpgsqlDataSource dataSource)
    {
        await using (var relation = dataSource.CreateCommand(
                         """
                         SELECT to_regclass(
                             'public.schema_migrations') IS NOT NULL;
                         """))
        {
            if (!Convert.ToBoolean(
                    await relation.ExecuteScalarAsync()))
            {
                return false;
            }
        }

        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM public.schema_migrations
                WHERE migration_id = @migrationId
            );
            """);
        command.Parameters.AddWithValue(
            "migrationId",
            MigrationId);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync());
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlDataSource dataSource,
        string tableName,
        string columnName)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName
            );
            """);
        command.Parameters.AddWithValue(
            "tableName",
            tableName);
        command.Parameters.AddWithValue(
            "columnName",
            columnName);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync());
    }

    private static async Task DeleteAccountAsync(
        NpgsqlDataSource dataSource,
        int accountId)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM public.accounts
            WHERE id = @accountId;
            """);
        command.Parameters.AddWithValue(
            "accountId",
            accountId);
        _ = await command.ExecuteNonQueryAsync();
    }
}
