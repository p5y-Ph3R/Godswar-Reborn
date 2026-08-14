using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetScaledAddedValueMigrationIntegrationChecks
{
    private static async Task<Fixture> InsertFixtureAsync(
        NpgsqlDataSource dataSource,
        string token)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var petIds = new Dictionary<string, long>();
            await using (var command = new NpgsqlCommand(
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
                    user_id, species_id, name, sex, level, aptitude,
                    remaining_lifetime, completed_pet_merges,
                    initial_savvy_baseline_total,
                    initial_savvy_policy_version,
                    rarity_added_savvy_baseline_total,
                    rarity_added_savvy_policy_version,
                    initial_savvy_source_version, revision
                )
                SELECT
                    owner.id, 1, @petPrefix || fixture.kind,
                    0, 30, 1, 600, fixture.completed_merges,
                    60, 'project-v3', 60, 'project-v3',
                    'savvy-plus-growth-v2', fixture.revision
                FROM created_owner owner
                CROSS JOIN (VALUES
                    ('eligible', 0, 7::bigint),
                    ('blocked', 1, 17::bigint)
                ) fixture(kind, completed_merges, revision)
                RETURNING id, name;
                """,
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("username", $"m078_{token}");
                command.Parameters.AddWithValue("ownerName", $"M078{token}");
                command.Parameters.AddWithValue("petPrefix", $"M078{token}_");
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(1);
                    petIds[name.EndsWith(
                        "eligible",
                        StringComparison.Ordinal)
                        ? "eligible"
                        : "blocked"] = reader.GetInt64(0);
                }
            }
            Check.Equal(2, petIds.Count, "both V3 migration pets are seeded");

            foreach (var petId in petIds.Values)
            {
                await InsertStatsAsync(connection, transaction, petId);
            }
            await InsertStaleOwnerMergeBonusAsync(
                connection,
                transaction,
                petIds["eligible"]);
            await transaction.CommitAsync();
            return new Fixture(petIds["eligible"], petIds["blocked"]);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task InsertStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_stat_values (
                pet_id, stat_code, initial_savvy, added_savvy,
                base_growth_rate, growth_acceleration,
                birth_initial_savvy, rarity_added_savvy, revision)
            SELECT
                @petId, stat_code,
                10.00 + (0.01 * 29),
                0.01,
                0.01,
                stat_code::numeric / 100,
                10.00,
                10.00,
                100 + stat_code
            FROM generate_series(1, 6) stat(stat_code);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            6,
            await command.ExecuteNonQueryAsync(),
            $"pet {petId} receives six pre-V3 stat rows");
    }

    private static async Task InsertStaleOwnerMergeBonusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_pet_character_bonuses (
                pet_id, effect_code, effect_value, revision,
                balance_revision)
            VALUES (@petId, 0, 999, 7, NULL);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "eligible pet receives a stale owner-Merge bonus projection");
    }

    private static async Task<PetState> ReadPetStateAsync(
        NpgsqlDataSource dataSource,
        long petId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                pet.id, pet.level, pet.completed_pet_merges,
                pet.revision, pet.initial_savvy_source_version,
                stat.stat_code, stat.initial_savvy, stat.added_savvy,
                stat.base_growth_rate, stat.growth_acceleration,
                stat.birth_initial_savvy, stat.rarity_added_savvy,
                stat.revision
            FROM public.character_pets pet
            JOIN public.character_pet_stat_values stat
              ON stat.pet_id = pet.id
            WHERE pet.id = @petId
            ORDER BY stat.stat_code;
            """);
        command.Parameters.AddWithValue("petId", petId);
        PetState? state = null;
        var stats = new List<PetStatState>(6);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            state ??= new PetState(
                reader.GetInt64(0),
                reader.GetInt16(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetString(4),
                stats);
            stats.Add(new PetStatState(
                reader.GetInt16(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.GetInt64(12)));
        }
        return state ?? throw new InvalidDataException(
            $"Pet {petId} was not returned.");
    }

    private static async Task AssertArchiveAsync(
        NpgsqlDataSource dataSource,
        PetState before)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                stat_code, old_level, old_completed_pet_merges,
                old_initial_savvy, old_added_savvy,
                old_base_growth_rate, old_growth_acceleration,
                old_birth_initial_savvy, old_rarity_added_savvy,
                old_stat_revision, old_pet_revision, old_source_version
            FROM public.pet_scaled_added_value_v3_archive
            WHERE migration_id = @migrationId
              AND pet_id = @petId
            ORDER BY stat_code;
            """);
        command.Parameters.AddWithValue("migrationId", MigrationId);
        command.Parameters.AddWithValue("petId", before.PetId);
        var index = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var stat = before.Stats[index];
            Check.True(
                reader.GetInt16(0) == stat.StatCode &&
                reader.GetInt16(1) == before.Level &&
                reader.GetInt32(2) == before.CompletedPetMerges &&
                reader.GetDecimal(3) == stat.InitialSavvy &&
                reader.GetDecimal(4) == stat.AddedSavvy &&
                reader.GetDecimal(5) == stat.BaseGrowthRate &&
                reader.GetDecimal(6) == stat.GrowthAcceleration &&
                reader.GetDecimal(7) == stat.BirthInitialSavvy &&
                reader.GetDecimal(8) == stat.RarityAddedSavvy &&
                reader.GetInt64(9) == stat.Revision &&
                reader.GetInt64(10) == before.Revision &&
                reader.GetString(11) == "savvy-plus-growth-v2",
                $"archive preserves stat {stat.StatCode} exactly");
            index++;
        }
        Check.Equal(6, index, "archive contains all six before-images");
    }

    private sealed record Fixture(long EligiblePetId, long BlockedPetId);

    private sealed record PetState(
        long PetId,
        short Level,
        int CompletedPetMerges,
        long Revision,
        string SourceVersion,
        IReadOnlyList<PetStatState> Stats);

    private sealed record PetStatState(
        short StatCode,
        decimal InitialSavvy,
        decimal AddedSavvy,
        decimal BaseGrowthRate,
        decimal GrowthAcceleration,
        decimal BirthInitialSavvy,
        decimal RarityAddedSavvy,
        long Revision);
}
