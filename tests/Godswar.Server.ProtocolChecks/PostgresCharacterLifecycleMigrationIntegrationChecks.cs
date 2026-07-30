using System.Text.RegularExpressions;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleMigrationIntegrationChecks
{
    internal const string CheckName =
        "PostgreSQL character lifecycle migration";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_lifecycle_preflight|b11_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadDatabaseNameAsync(dataSource);
        if (!IsDisposableDatabaseName(databaseName))
        {
            throw new InvalidOperationException(
                "Character lifecycle migration checks require a " +
                $"bounded disposable database; received '{databaseName}'.");
        }
        await PrepareSchemaAsync(dataSource);
        var fixture = await CreateFixtureAsync(dataSource);
        try
        {
            await AssertSchemaAsync(dataSource);
            await AssertActiveSlotAndTombstonesAsync(
                dataSource,
                fixture);
            await AssertLifecycleConstraintsAsync(
                dataSource,
                fixture);
        }
        finally
        {
            await DeleteFixtureAsync(dataSource, fixture);
        }
    }

    internal static bool IsDisposableDatabaseName(string databaseName) =>
        DisposableDatabasePattern.IsMatch(databaseName);

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "PostgreSQL returned no database name.");
    }

    private static async Task AssertSchemaAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                index_row.indisunique,
                pg_get_expr(
                    index_row.indpred,
                    index_row.indrelid)
            FROM pg_index index_row
            JOIN pg_class index_class
              ON index_class.oid = index_row.indexrelid
            WHERE index_class.relname =
                'ux_character_base_active_account_slot';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "active account-slot index exists");
        Check.True(
            reader.GetBoolean(0),
            "active account-slot index is unique");
        Check.True(
            reader.GetString(1).Contains(
                "lifecycle_state",
                StringComparison.Ordinal) &&
            reader.GetString(1).Contains(
                "active",
                StringComparison.Ordinal),
            "account-slot uniqueness is limited to active characters");
        Check.True(
            !await reader.ReadAsync(),
            "active account-slot index exists exactly once");
    }

    private static async Task AssertActiveSlotAndTombstonesAsync(
        NpgsqlDataSource dataSource,
        LifecycleFixture fixture)
    {
        var original = await InsertActiveAsync(
            dataSource,
            fixture.AccountId,
            $"B11A{fixture.Token}");
        var opening = await ReadLifecycleAsync(
            dataSource,
            original);
        Check.Equal(
            (short)0,
            opening.Slot,
            "new character occupies native slot zero");
        Check.Equal(
            "active",
            opening.State,
            "new character starts active");
        Check.Equal(
            1L,
            opening.Version,
            "new character starts at lifecycle version one");
        Check.True(
            opening.DeletedAt is null &&
            opening.RestoreUntil is null &&
            opening.PurgeAfter is null,
            "active character has no deletion timestamps");

        await AssertUniqueViolationAsync(
            async () =>
            {
                _ = await InsertActiveAsync(
                    dataSource,
                    fixture.AccountId,
                    $"B11B{fixture.Token}");
            },
            "second active character cannot occupy native slot zero");

        await MarkDeletedAsync(dataSource, original);
        var replacement = await InsertActiveAsync(
            dataSource,
            fixture.AccountId,
            $"B11C{fixture.Token}");
        await AssertUniqueViolationAsync(
            () => RestoreAsync(dataSource, original),
            "restore cannot displace an active replacement");

        await MarkDeletedAsync(dataSource, replacement);
        await RestoreAsync(dataSource, original);
        var restored = await ReadLifecycleAsync(
            dataSource,
            original);
        Check.Equal(
            "active",
            restored.State,
            "restore returns the original character to active");
        Check.Equal(
            5L,
            restored.Version,
            "replacement, delete, and restore use one account-slot version");

        _ = await InsertDeletedAsync(
            dataSource,
            fixture.AccountId,
            $"B11D{fixture.Token}");
        var deletedCount = await ReadInt32Async(
            dataSource,
            """
            SELECT count(*)::integer
            FROM public.character_base
            WHERE account_id = @accountId
              AND character_slot = 0
              AND lifecycle_state = 'deleted';
            """,
            fixture.AccountId);
        Check.Equal(
            2,
            deletedCount,
            "multiple tombstones can coexist outside the active slot");
        Check.Equal(
            6L,
            await ReadInt64Async(
                dataSource,
                """
                SELECT character_lifecycle_version
                FROM public.accounts
                WHERE id = @accountId;
                """,
                fixture.AccountId),
            "account-slot lifecycle version never resets across replacements");

        await AssertUniqueViolationAsync(
            async () =>
            {
                _ = await InsertActiveAsync(
                    dataSource,
                    fixture.OtherAccountId,
                    $"B11C{fixture.Token}");
            },
            "a tombstoned name remains reserved until controlled purge");
    }
}
