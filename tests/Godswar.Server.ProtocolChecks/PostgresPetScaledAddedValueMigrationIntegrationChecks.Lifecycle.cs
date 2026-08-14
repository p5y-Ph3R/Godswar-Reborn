using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetScaledAddedValueMigrationIntegrationChecks
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
        NpgsqlDataSource dataSource,
        string relation)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT to_regclass(@relation) IS NOT NULL;");
        command.Parameters.AddWithValue("relation", relation);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task DeletePetAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM public.character_pets WHERE id = @petId;");
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "blocked historical-Merge fixture is removed before retry");
    }

    private static async Task<long> CountArchiveRowsAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM public.pet_scaled_added_value_v3_archive
            WHERE migration_id = @migrationId
              AND pet_id = @petId;
            """);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        command.Parameters.AddWithValue("petId", petId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountOwnerMergeBonusRowsAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM public.character_pet_character_bonuses
            WHERE pet_id = @petId;
            """);
        command.Parameters.AddWithValue("petId", petId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task CleanupFixtureAsync(
        NpgsqlDataSource dataSource,
        Fixture fixture,
        string token)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var archive = new NpgsqlCommand(
                """
                DELETE FROM public.pet_scaled_added_value_v3_archive
                WHERE migration_id = @migrationId
                  AND pet_id = @petId;
                """,
                connection,
                transaction))
            {
                archive.Parameters.AddWithValue("migrationId", MigrationId);
                archive.Parameters.AddWithValue("petId", fixture.EligiblePetId);
                Check.Equal(
                    6,
                    await archive.ExecuteNonQueryAsync(),
                    "fixture archive rows are cleaned");
            }
            await using (var account = new NpgsqlCommand(
                "DELETE FROM public.accounts WHERE username = @username;",
                connection,
                transaction))
            {
                account.Parameters.AddWithValue("username", $"m078_{token}");
                Check.Equal(
                    1,
                    await account.ExecuteNonQueryAsync(),
                    "V3 fixture owner cascade is cleaned");
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
