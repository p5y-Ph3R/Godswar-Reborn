using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetPhoenixGrowthMigrationIntegrationChecks
{
    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var relation = new NpgsqlCommand(
            "SELECT to_regclass('public.schema_migrations') IS NOT NULL;",
            connection))
        {
            if (!Convert.ToBoolean(await relation.ExecuteScalarAsync()))
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.schema_migrations " +
            "WHERE migration_id = @migrationId);",
            connection);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> RelationExistsAsync(
        string connectionString,
        string relation)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@relation) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("relation", relation);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task AssertMigrationMetadataAsync(
        NpgsqlDataSource dataSource,
        long expectedCount)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM public.schema_migrations
            WHERE migration_id = @migrationId;
            """);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        Check.Equal(
            expectedCount,
            Convert.ToInt64(await command.ExecuteScalarAsync()),
            "Phoenix migration metadata is recorded exactly once");
    }

    private static async Task<long> CountArchiveRowsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<Fixture> fixtures)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM public.pet_phoenix_growth_activation_archive
            WHERE migration_id = @migrationId
              AND pet_id = ANY(@petIds);
            """);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        command.Parameters.AddWithValue(
            "petIds",
            fixtures.Select(static value => value.PetId).ToArray());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task CleanupFixturesAsync(
        NpgsqlDataSource dataSource,
        string token,
        IReadOnlyList<Fixture> fixtures)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var archive = new NpgsqlCommand(
                """
                DELETE FROM public.pet_phoenix_growth_activation_archive
                WHERE migration_id = @migrationId
                  AND pet_id = ANY(@petIds);
                """,
                connection,
                transaction))
            {
                archive.Parameters.AddWithValue("migrationId", MigrationId);
                archive.Parameters.AddWithValue(
                    "petIds",
                    fixtures.Select(static value => value.PetId).ToArray());
                Check.Equal(
                    6,
                    await archive.ExecuteNonQueryAsync(),
                    "only converted fixture archive rows are cleaned");
            }

            await using (var account = new NpgsqlCommand(
                "DELETE FROM public.accounts WHERE username = @username;",
                connection,
                transaction))
            {
                account.Parameters.AddWithValue("username", $"m071_{token}");
                Check.Equal(
                    1,
                    await account.ExecuteNonQueryAsync(),
                    "fixture owner cascade is cleaned");
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<long> CountFixturesAsync(
        string connectionString,
        string token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM public.character_pets WHERE name LIKE @name;",
            connection);
        command.Parameters.AddWithValue("name", $"M071{token}_%");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
