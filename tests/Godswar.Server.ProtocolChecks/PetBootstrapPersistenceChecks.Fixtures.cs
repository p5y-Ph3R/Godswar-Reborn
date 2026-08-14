using Godswar.Server.State;
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
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                rank,
                completed_rebirths,
                rebirths_remaining,
                completed_pet_merges,
                has_soul_contract,
                has_owner_merge_talent,
                talent_mask,
                opened_skill_slots,
                available_skill_slots,
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
                birth_rank,
                hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision,
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
                3600,
                @initialSavvyPolicy,
                3600,
                @initialSavvyPolicy,
                @initialSavvySource,
                25.5,
                3,
                2,
                7,
                true,
                true,
                31,
                4,
                7,
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
                (SELECT step.rank
                 FROM public.pet_content_publication publication
                 JOIN public.pet_content_hatch_rank_steps step
                   ON step.revision = publication.revision
                  AND step.aptitude = 14
                  AND step.outcome_order = 0
                 WHERE publication.family = 'pets'),
                0,
                0,
                (SELECT revision
                 FROM public.pet_content_publication
                 WHERE family = 'pets'),
                TIMESTAMPTZ '2026-07-28 01:02:03+00',
                TIMESTAMPTZ '2026-07-28 04:05:06+00'
            )
            RETURNING id;
            """,
            connection,
            transaction);
        insertPet.Parameters.AddWithValue("characterId", characterId);
        insertPet.Parameters.AddWithValue(
            "initialSavvyPolicy",
            PetInitialSavvyPolicy.Version);
        insertPet.Parameters.AddWithValue(
            "initialSavvySource",
            PetSavvyRuntimeSemantics.SourceVersion);
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
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_pets (
                user_id,
                species_id,
                name,
                sex,
                level,
                aptitude,
                initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                activity_state,
                birth_rank,
                hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision
            )
            VALUES (
                @characterId,
                37,
                'Second Fixture',
                0,
                1,
                1,
                30,
                @initialSavvyPolicy,
                30,
                @initialSavvyPolicy,
                @initialSavvySource,
                'owned',
                (SELECT step.rank
                 FROM public.pet_content_publication publication
                 JOIN public.pet_content_hatch_rank_steps step
                   ON step.revision = publication.revision
                  AND step.aptitude = 1
                  AND step.outcome_order = 0
                 WHERE publication.family = 'pets'),
                0,
                0,
                (SELECT revision
                 FROM public.pet_content_publication
                 WHERE family = 'pets')
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "initialSavvyPolicy",
            PetInitialSavvyPolicy.Version);
        command.Parameters.AddWithValue(
            "initialSavvySource",
            PetSavvyRuntimeSemantics.SourceVersion);
        var petId = (long)(await command.ExecuteScalarAsync()
                           ?? throw new InvalidOperationException(
                               "Second pet fixture insert returned no ID."));
        await InsertWeakStatValuesAsync(
            connection,
            transaction,
            petId);
        await transaction.CommitAsync();
        return petId;
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
                birth_initial_savvy,
                rarity_added_savvy,
                growth_acceleration,
                revision
            )
            SELECT
                @petId,
                code,
                600 + code,
                (30 + code * 1.1) * 80,
                9.7 + code * 0.1,
                600,
                600,
                code + 20.3,
                code
            FROM generate_series(1, 6) AS code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertWeakStatValuesAsync(
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
                birth_initial_savvy,
                rarity_added_savvy,
                growth_acceleration,
                revision
            )
            SELECT
                @petId,
                code,
                5,
                0.01,
                0.01,
                5,
                5,
                0,
                0
            FROM generate_series(1, 6) AS code;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        Check.Equal(
            6,
            await command.ExecuteNonQueryAsync(),
            "weak pet fixture inserts six current-provenance stat rows");
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
