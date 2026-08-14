using System.Text.RegularExpressions;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetRankContentMigrationIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL pet-rank legacy migration integration";
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260812_081_pet_rank_content";
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b12_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await using (var databaseCommand = dataSource.CreateCommand(
                         "SELECT current_database();"))
        {
            var database = await databaseCommand.ExecuteScalarAsync()
                as string ?? string.Empty;
            if (!DisposableDatabasePattern.IsMatch(database))
            {
                Console.WriteLine(
                    $"SKIP {CheckName} requires a disposable B03/B12 " +
                    $"database; received '{database}'");
                return;
            }
        }

        var migrations = PostgresSchemaMigrationCatalog.All;
        var migrationIndex = migrations
            .Select((migration, index) => (migration, index))
            .Single(value => value.migration.Id == MigrationId)
            .index;
        await new PostgresSchemaMigrationRunner(dataSource).InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            migrations.Take(migrationIndex).ToArray());
        var migration = migrations[migrationIndex];

        await AssertLegacyPetPreservedAsync(dataSource, migration);
        await AssertInvalidLegacyRankRejectedAsync(
            dataSource,
            migration,
            655.36m,
            "rank above the native wire maximum");
        await AssertInvalidLegacyRankRejectedAsync(
            dataSource,
            migration,
            1.001m,
            "rank with fractional hundredths");
    }

    private static async Task AssertLegacyPetPreservedAsync(
        NpgsqlDataSource dataSource,
        PostgresSchemaMigration migration)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var petId = await InsertLegacyPetAsync(
            connection,
            transaction,
            rank: 100.94m);
        await ExecuteMigrationAsync(connection, transaction, migration);

        await using var command = new NpgsqlCommand(
            """
            SELECT rank, birth_rank, hatch_rank_roll,
                   hatch_rank_outcome_order,
                   hatch_rank_content_revision
            FROM public.character_pets
            WHERE id = @petId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Check.True(
                await reader.ReadAsync() &&
                reader.GetDecimal(0) == 100.94m &&
                reader.IsDBNull(1) &&
                reader.IsDBNull(2) &&
                reader.IsDBNull(3) &&
                reader.IsDBNull(4),
                "migration preserves a legacy rank and does not invent hatch provenance");
        }
        await transaction.RollbackAsync();
    }

    private static async Task AssertInvalidLegacyRankRejectedAsync(
        NpgsqlDataSource dataSource,
        PostgresSchemaMigration migration,
        decimal rank,
        string description)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        _ = await InsertLegacyPetAsync(
            connection,
            transaction,
            rank);
        try
        {
            await ExecuteMigrationAsync(connection, transaction, migration);
            throw new InvalidOperationException(
                $"Migration accepted {description}.");
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.RaiseException &&
                  exception.MessageText.Contains(
                      "rank outside native UInt16 hundredths",
                      StringComparison.Ordinal))
        {
            // Expected: operators must reconcile invalid legacy rank before
            // migration 081 can be applied.
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<long> InsertLegacyPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        decimal rank)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var command = new NpgsqlCommand(
            """
            WITH account AS (
                INSERT INTO public.accounts (username)
                VALUES (@username)
                RETURNING id
            ), character_row AS (
                INSERT INTO public.character_base (account_id, name)
                SELECT id, @characterName
                FROM account
                RETURNING id
            )
            INSERT INTO public.character_pets (
                user_id, species_id, name, sex, rank,
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version
            )
            SELECT id, 1, @petName, 0, @rank,
                   60, @savvyPolicy, 60, @savvyPolicy, @savvySource
            FROM character_row
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", $"rank_{token}");
        command.Parameters.AddWithValue(
            "characterName",
            $"Rank{token}");
        command.Parameters.AddWithValue("petName", $"Pet{token}");
        command.Parameters.AddWithValue("rank", rank);
        command.Parameters.AddWithValue(
            "savvyPolicy",
            PetInitialSavvyPolicy.Version);
        command.Parameters.AddWithValue(
            "savvySource",
            PetSavvyRuntimeSemantics.SourceVersion);
        var petId = Convert.ToInt64(await command.ExecuteScalarAsync());
        await using (var stats = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id, stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration,
                birth_initial_savvy, rarity_added_savvy
            )
            SELECT @petId, stat_code, 10, 0.01, 0.01, 0, 10, 10
            FROM generate_series(1, 6) stat(stat_code);
            """,
            connection,
            transaction))
        {
            stats.Parameters.AddWithValue("petId", petId);
            Check.Equal(
                6,
                await stats.ExecuteNonQueryAsync(),
                "legacy rank fixture receives valid six-stat provenance");
        }
        await using var settleDeferredConstraints = new NpgsqlCommand(
            "SET CONSTRAINTS ALL IMMEDIATE;",
            connection,
            transaction);
        await settleDeferredConstraints.ExecuteNonQueryAsync();
        return petId;
    }

    private static async Task ExecuteMigrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresSchemaMigration migration)
    {
        await using var command = new NpgsqlCommand(
            migration.Sql,
            connection,
            transaction);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }
}
