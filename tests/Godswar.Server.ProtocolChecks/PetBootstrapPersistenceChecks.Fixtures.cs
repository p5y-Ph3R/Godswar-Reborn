using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetBootstrapPersistenceChecks
{
    private static async Task<long> InsertPetFixtureAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await using var insertPet = new NpgsqlCommand(
            """
            INSERT INTO character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                experience,
                aptitude,
                rank,
                completed_rebirths,
                rebirths_remaining,
                completed_pet_merges,
                has_soul_contract,
                has_owner_merge_talent,
                current_energy,
                maximum_energy,
                amity,
                satiety,
                remaining_lifetime,
                available_stat_points,
                growth_revealed,
                bound,
                activity_state,
                is_carried,
                is_summoned,
                contributes_to_character,
                revision,
                created_at,
                updated_at
            )
            VALUES (
                @characterId,
                37,
                'Godly Fixture',
                1,
                80,
                123456789,
                14,
                25.5,
                3,
                2,
                7,
                true,
                true,
                90,
                100,
                321,
                88,
                6543,
                9,
                true,
                true,
                'owned',
                true,
                true,
                true,
                12,
                TIMESTAMPTZ '2026-07-28 01:02:03+00',
                TIMESTAMPTZ '2026-07-28 04:05:06+00'
            )
            RETURNING id;
            """,
            connection,
            transaction);
        insertPet.Parameters.AddWithValue("characterId", characterId);
        var petId = (long)(await insertPet.ExecuteScalarAsync()
                           ?? throw new InvalidOperationException(
                               "Pet fixture insert returned no ID."));

        await InsertStatValuesAsync(connection, transaction, petId);
        await InsertCharacterBonusesAsync(connection, transaction, petId);
        await InsertSkillsAsync(connection, transaction, petId);
        await transaction.CommitAsync();
        return petId;
    }

    private static async Task<long> InsertAdditionalPetFixtureAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                aptitude,
                activity_state
            )
            VALUES (
                @characterId,
                37,
                'Second Fixture',
                0,
                1,
                1,
                'owned'
            )
            RETURNING id;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "Second pet fixture insert returned no ID."));
    }

    private static async Task SetPetActivityStateAsync(
        string connectionString,
        long petId,
        string activityState)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE character_pets
            SET activity_state = @activityState,
                revision = revision + 1,
                updated_at = now()
            WHERE id = @petId;
            """,
            connection);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "activityState",
            activityState);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "pet activity fixture update");
    }

    private static async Task InsertStatValuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_pet_stat_values (
                pet_id,
                stat_code,
                initial_savvy,
                added_savvy,
                base_growth_rate,
                growth_acceleration,
                revision
            )
            SELECT
                @petId,
                code,
                code + 0.1,
                code + 10.2,
                code + 30.4,
                code + 20.3,
                code
            FROM generate_series(1, 6) AS code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertCharacterBonusesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_pet_character_bonuses (
                pet_id,
                effect_code,
                effect_value,
                revision
            )
            VALUES
                (@petId, 0, 11.25, 41),
                (@petId, 38, 22.5, 42);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertSkillsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_pet_skills (
                pet_id,
                skill_id,
                slot_index,
                skill_rank,
                skill_experience,
                is_active,
                revision
            )
            VALUES
                (@petId, 5001, 0, 4, 444, true, 31),
                (@petId, 5002, 5, 2, 55, false, 32);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteFixtureAsync(
        string connectionString,
        int accountId,
        string username,
        int? characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        if (characterId.HasValue)
        {
            await using (var deleteAudit = new NpgsqlCommand(
                """
                DELETE FROM pet_operation_audit
                WHERE user_id_snapshot = @characterId;
                """,
                connection,
                transaction))
            {
                deleteAudit.Parameters.AddWithValue(
                    "characterId",
                    characterId.Value);
                await deleteAudit.ExecuteNonQueryAsync();
            }

            await using var deleteCharacter = new NpgsqlCommand(
                """
                DELETE FROM character_base
                WHERE id = @characterId
                  AND account_id = @accountId;
                """,
                connection,
                transaction);
            deleteCharacter.Parameters.AddWithValue(
                "characterId",
                characterId.Value);
            deleteCharacter.Parameters.AddWithValue("accountId", accountId);
            await deleteCharacter.ExecuteNonQueryAsync();
        }

        await using var deleteAccount = new NpgsqlCommand(
            """
            DELETE FROM accounts
            WHERE id = @accountId
              AND username = @username;
            """,
            connection,
            transaction);
        deleteAccount.Parameters.AddWithValue("accountId", accountId);
        deleteAccount.Parameters.AddWithValue("username", username);
        await deleteAccount.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
