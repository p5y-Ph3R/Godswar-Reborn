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
        PetHatchRankEvidence hatchRank,
        int initialSavvyTotal,
        short sex,
        int lifetime,
        bool bound,
        short talentMask,
        short initialSkillSlots,
        bool isCarried,
        bool isSummoned,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_pets (
                user_id, species_id, name, sex, level, experience,
                aptitude, initial_savvy_baseline_total,
                initial_savvy_policy_version,
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version, rank, birth_rank,
                hatch_rank_roll, hatch_rank_outcome_order,
                hatch_rank_content_revision,
                current_energy, maximum_energy, amity, satiety,
                remaining_lifetime, growth_revealed, bound,
                activity_state, talent_mask, has_owner_merge_talent,
                opened_skill_slots, available_skill_slots,
                is_carried, is_summoned
            )
            VALUES (
                @characterId, @speciesId, @name, @sex, 1, 0,
                @aptitude, @initialSavvyTotal, @initialSavvyPolicy,
                @initialSavvyTotal, @initialSavvyPolicy,
                @initialSavvySource, @rank, @rank,
                @rankRoll, @rankOutcomeOrder, @rankContentRevision,
                100, 100, 100, 100,
                @lifetime, false, @bound, 'owned', @talentMask,
                @hasMergeTalent, @initialSkillSlots, @initialSkillSlots,
                @isCarried, @isSummoned
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
            "initialSavvyTotal",
            initialSavvyTotal);
        command.Parameters.AddWithValue(
            "initialSavvyPolicy",
            _petContent.Settings.InitialSavvyPolicyVersion);
        command.Parameters.AddWithValue(
            "initialSavvySource",
            PetSavvyRuntimeSemantics.SourceVersion);
        command.Parameters.AddWithValue("rank", hatchRank.Rank);
        command.Parameters.AddWithValue("rankRoll", hatchRank.Roll);
        command.Parameters.AddWithValue(
            "rankOutcomeOrder",
            hatchRank.OutcomeOrder);
        command.Parameters.AddWithValue(
            "rankContentRevision",
            hatchRank.ContentRevision);
        command.Parameters.AddWithValue("lifetime", lifetime);
        command.Parameters.AddWithValue("bound", bound);
        command.Parameters.AddWithValue("talentMask", talentMask);
        command.Parameters.AddWithValue(
            "hasMergeTalent",
            (talentMask & PetTalentCatalog.Merge.MaskBit) != 0);
        command.Parameters.AddWithValue(
            "initialSkillSlots",
            initialSkillSlots);
        command.Parameters.AddWithValue("isCarried", isCarried);
        command.Parameters.AddWithValue("isSummoned", isSummoned);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task InsertPetStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        PetSavvy savvy,
        PetSavvy growth,
        CancellationToken cancellationToken)
    {
        var savvyValues = new[]
        {
            savvy.Agility, savvy.Strength, savvy.Accuracy,
            savvy.Technique, savvy.Wisdom, savvy.Luck
        };
        var growthValues = new[]
        {
            growth.Agility, growth.Strength, growth.Accuracy,
            growth.Technique, growth.Wisdom, growth.Luck
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
                    @petId, @statCode, @savvy, @growth,
                    @growth, 0, @savvy, @savvy
                );
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("petId", petId);
            command.Parameters.AddWithValue(
                "statCode",
                checked((short)(index + 1)));
            command.Parameters.AddWithValue(
                "savvy",
                savvyValues[index]);
            command.Parameters.AddWithValue(
                "growth",
                growthValues[index]);
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
