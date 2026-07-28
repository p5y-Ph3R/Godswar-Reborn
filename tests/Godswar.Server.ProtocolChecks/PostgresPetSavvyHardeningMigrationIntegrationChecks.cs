using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class
    PostgresPetSavvyHardeningMigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string RequiredMigrationId =
        "20260729_020_pet_savvy_semantics";
    private const string MigrationId =
        "20260729_021_pet_savvy_semantics_hardening";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-savvy hardening integration ({ConnectionStringVariable} is not set)");
            return;
        }

        if (!await IsMigrationAppliedAsync(
                connectionString,
                RequiredMigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-savvy hardening integration ({RequiredMigrationId} is required)");
            return;
        }

        if (await IsMigrationAppliedAsync(connectionString, MigrationId))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-savvy hardening integration ({MigrationId} is already applied)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            var petId = await ReadCorrectedPetIdAsync(
                connection,
                transaction);
            Check.True(
                petId > 0,
                "integration database contains a migration-020 corrected pet");

            var migration = PostgresSchemaMigrationCatalog.All.Single(
                candidate => candidate.Id == MigrationId);
            await CheckEqualAllocationPreflightAsync(
                connection,
                transaction,
                migration,
                petId);
            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);
            await CheckValidatedConstraintsAsync(
                connection,
                transaction);

            await CheckRejectedAsync(
                connection,
                transaction,
                "UPDATE character_pets SET rarity_added_savvy_policy_version = NULL WHERE id = @petId;",
                petId,
                "partial pet savvy provenance is rejected");
            await CheckRejectedAsync(
                connection,
                transaction,
                "UPDATE character_pet_stat_values SET birth_initial_savvy = base_growth_rate + 0.01 WHERE pet_id = @petId AND stat_code = 1;",
                petId,
                "birth basic savvy cannot diverge from base growth");
            await CheckRejectedAsync(
                connection,
                transaction,
                "UPDATE character_pet_stat_values SET initial_savvy = birth_initial_savvy - 0.01 WHERE pet_id = @petId AND stat_code = 1;",
                petId,
                "current basic savvy cannot fall below its birth baseline");
            await CheckRejectedAsync(
                connection,
                transaction,
                "UPDATE character_pet_stat_values SET added_savvy = rarity_added_savvy - 0.01 WHERE pet_id = @petId AND stat_code = 1;",
                petId,
                "current added savvy cannot fall below its rarity baseline");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString,
        string migrationId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.schema_migrations
                WHERE migration_id = @migrationId
            );
            """,
            connection);
        command.Parameters.AddWithValue("migrationId", migrationId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadCorrectedPetIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id
            FROM public.character_pets
            WHERE rarity_added_savvy_policy_version = 'project-v2'
              AND initial_savvy_source_version = 'growth-x1-v1'
            ORDER BY id
            LIMIT 1;
            """,
            connection,
            transaction);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private static async Task CheckValidatedConstraintsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_constraint
            WHERE convalidated
              AND conname IN (
                  'ck_character_pets_savvy_provenance',
                  'ck_pet_stat_growth_x1_birth_baseline',
                  'ck_pet_stat_initial_savvy_progression',
                  'ck_pet_stat_added_savvy_progression'
              );
            """,
            connection,
            transaction);
        Check.Equal(
            4L,
            Convert.ToInt64(await command.ExecuteScalarAsync()),
            "all pet-savvy hardening constraints are validated");
    }

    private static async Task CheckEqualAllocationPreflightAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresSchemaMigration migration,
        long petId)
    {
        const string savepoint = "pet_savvy_equal_preflight";
        await ExecuteAsync(
            connection,
            transaction,
            $"SAVEPOINT {savepoint};");
        var rejected = false;
        try
        {
            await using (var command = new NpgsqlCommand(
                """
                UPDATE character_pet_stat_values
                SET rarity_added_savvy = (
                    SELECT min(rarity_added_savvy)
                    FROM character_pet_stat_values
                    WHERE pet_id = @petId
                )
                WHERE pet_id = @petId;
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("petId", petId);
                await command.ExecuteNonQueryAsync();
            }

            await ExecuteAsync(
                connection,
                transaction,
                migration.Sql);
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.RaiseException)
        {
            rejected = true;
        }
        finally
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"ROLLBACK TO SAVEPOINT {savepoint};");
            await ExecuteAsync(
                connection,
                transaction,
                $"RELEASE SAVEPOINT {savepoint};");
        }

        Check.True(
            rejected,
            "migration preflight rejects an all-equal rarity allocation");
    }

    private static async Task CheckRejectedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        long petId,
        string description)
    {
        const string savepoint = "pet_savvy_hardening_case";
        await ExecuteAsync(
            connection,
            transaction,
            $"SAVEPOINT {savepoint};");
        var rejected = false;
        try
        {
            await using var command =
                new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("petId", petId);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
            when (exception.SqlState ==
                  PostgresErrorCodes.CheckViolation)
        {
            rejected = true;
        }
        finally
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"ROLLBACK TO SAVEPOINT {savepoint};");
            await ExecuteAsync(
                connection,
                transaction,
                $"RELEASE SAVEPOINT {savepoint};");
        }

        Check.True(rejected, description);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command =
            new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
