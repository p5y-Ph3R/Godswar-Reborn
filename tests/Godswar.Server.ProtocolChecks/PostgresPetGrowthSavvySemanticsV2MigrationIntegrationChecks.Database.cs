using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetGrowthSavvySemanticsV2MigrationIntegrationChecks
{
    private static async Task<long> InsertLegacyFixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string fixtureName,
        string fixtureUsername,
        string fixtureOwnerName)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH created_account AS (
                INSERT INTO public.accounts (username)
                VALUES (@username)
                RETURNING id
            ),
            created_owner AS (
                INSERT INTO public.character_base (account_id, name)
                SELECT id, @ownerName
                FROM created_account
                RETURNING id
            )
            INSERT INTO public.character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                aptitude,
                remaining_lifetime,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                revision
            )
            SELECT
                owner.id,
                1,
                @name,
                0,
                2,
                6,
                600,
                879,
                aptitude.added_savvy_policy_version,
                'growth-x1-v1',
                7
            FROM created_owner owner
            JOIN public.pet_aptitude_templates aptitude
              ON aptitude.aptitude = 6
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("name", fixtureName);
        command.Parameters.AddWithValue("username", fixtureUsername);
        command.Parameters.AddWithValue("ownerName", fixtureOwnerName);
        var petId = Convert.ToInt64(
            await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException(
                "Migration-069 fixture needs one character owner."));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id,
                stat_code,
                initial_savvy,
                added_savvy,
                base_growth_rate,
                growth_acceleration,
                birth_initial_savvy,
                rarity_added_savvy,
                revision
            )
            VALUES
                (@petId, 1, 7.38, 151.50, 0.38, 0.20, 0.38, 146.50, 11),
                (@petId, 2, 0.41, 146.50, 0.41, 0.00, 0.41, 146.50, 11),
                (@petId, 3, 0.40, 146.50, 0.40, 0.00, 0.40, 146.50, 11),
                (@petId, 4, 0.47, 146.50, 0.47, 0.00, 0.47, 146.50, 11),
                (@petId, 5, 0.44, 146.50, 0.44, 0.00, 0.44, 146.50, 11),
                (@petId, 6, 0.45, 146.50, 0.45, 0.00, 0.45, 146.50, 11);
            """,
            ("petId", petId));
        return petId;
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        string connectionString,
        string migrationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var relationCommand = new NpgsqlCommand(
            "SELECT to_regclass('public.schema_migrations') IS NOT NULL;",
            connection))
        {
            if (!Convert.ToBoolean(
                    await relationCommand.ExecuteScalarAsync()))
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM public.schema_migrations WHERE migration_id = @id);",
            connection);
        command.Parameters.AddWithValue("id", migrationId);
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

    private static async Task<long> CountFixturePetsAsync(
        string connectionString,
        string name)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM public.character_pets WHERE name = @name;",
            connection);
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed record MigratedStat(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedValue,
        decimal BaseGrowth,
        decimal GrowthAcceleration,
        decimal BirthSavvy,
        decimal LegacyRaritySavvy,
        long Revision);
}
