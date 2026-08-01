using Godswar.Server.Application.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<long> InsertPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        PetSpeciesContentDefinition species,
        short aptitude,
        int addedSavvyTotal,
        short sex,
        int lifetime,
        bool bound,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_pets (
                user_id, species_id, name, sex, level, experience,
                aptitude, rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version, rank,
                current_energy, maximum_energy, amity, satiety,
                remaining_lifetime, growth_revealed, bound,
                activity_state
            )
            VALUES (
                @characterId, @speciesId, @name, @sex, 1, 0,
                @aptitude, @addedSavvyTotal, @addedSavvyPolicy,
                'growth-x1-v1', 0,
                100, 100, 100, 100,
                @lifetime, false, @bound, 'owned'
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("speciesId", species.SpeciesId);
        command.Parameters.AddWithValue("name", species.DisplayName);
        command.Parameters.AddWithValue("sex", sex);
        command.Parameters.AddWithValue("aptitude", aptitude);
        command.Parameters.AddWithValue(
            "addedSavvyTotal",
            addedSavvyTotal);
        command.Parameters.AddWithValue(
            "addedSavvyPolicy",
            _petContent.Settings.AddedSavvyPolicyVersion);
        command.Parameters.AddWithValue("lifetime", lifetime);
        command.Parameters.AddWithValue("bound", bound);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task InsertPetStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        PetSavvy growth,
        PetSavvy added,
        CancellationToken cancellationToken)
    {
        var growthValues = new[]
        {
            growth.Agility, growth.Strength, growth.Accuracy,
            growth.Technique, growth.Wisdom, growth.Luck
        };
        var addedValues = new[]
        {
            added.Agility, added.Strength, added.Accuracy,
            added.Technique, added.Wisdom, added.Luck
        };
        for (short index = 0; index < growthValues.Length; index++)
        {
            await using var command = CreateCommand(
                """
                INSERT INTO public.character_pet_stat_values (
                    pet_id, stat_code, initial_savvy, added_savvy,
                    base_growth_rate, growth_acceleration,
                    birth_initial_savvy, rarity_added_savvy
                )
                VALUES (
                    @petId, @statCode, @growth, @added,
                    @growth, 0, @growth, @added
                );
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("petId", petId);
            command.Parameters.AddWithValue(
                "statCode",
                checked((short)(index + 1)));
            command.Parameters.AddWithValue(
                "growth",
                growthValues[index]);
            command.Parameters.AddWithValue(
                "added",
                addedValues[index]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task InsertPetSkillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        int skillId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_pet_skills (
                pet_id, skill_id, slot_index,
                skill_rank, skill_experience, is_active
            )
            VALUES (@petId, @skillId, 0, 1, 0, true);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("skillId", skillId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PetSavvy ToPetSavvy(PetContentStatVector value) =>
        new(
            value.Agility,
            value.Strength,
            value.Accuracy,
            value.Technique,
            value.Wisdom,
            value.Luck);
}
