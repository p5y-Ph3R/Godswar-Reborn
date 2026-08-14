using System.Security.Cryptography;
using System.Data.Common;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<PetEggHatchResult> HatchPetEggAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default)
    {
        if (kitBagSlot is < 0 or >= KitBagProjectionSlots)
        {
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.InvalidBagSlot);
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        if (!await LockOwnedCharacterAsync(
                connection,
                transaction,
                accountId,
                characterId,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.CharacterNotFound);
        }

        await EnsureRawPetMutationAllowedAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var pets = await LockCharacterPetsAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        var (_, kitBag) =
            await LoadAuthoritativeItemProjectionsForUpdateAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
        var egg = KitBagSlots.GetItem(kitBag, kitBagSlot);
        if (egg.IsEmpty || egg.Stack <= 0)
        {
            var character = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.ItemNotFound,
                character);
        }

        if (!PetContent.TryGetSpeciesByEggItemId(
                egg.Id,
                out var species))
        {
            var character = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.NotPetEgg,
                character);
        }

        if (egg.Stack != 1)
        {
            var character = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.InvalidEggStack,
                character);
        }

        var petShedCapacity = await ReadPetShedCapacityAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);
        if (!PetShedCapacityPolicy.IsValid(petShedCapacity))
        {
            throw new InvalidDataException(
                "The locked character has an invalid pet-shed capacity.");
        }
        if (pets.Count >= petShedCapacity ||
            pets.Count >= PetContent.Settings.MaximumOwnedPetCount)
        {
            var character = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.PetCapacityReached,
                character);
        }

        if (!PetContent.TryGetAptitude(
                egg.Quality,
                out var eggAptitude))
        {
            var character = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.InvalidEggRarity,
                character);
        }

        var aptitude = (PetAptitude)eggAptitude.Aptitude;
        if (!PetContent.TryGetNativeProfile(
                species.SpeciesId,
                eggAptitude.Aptitude,
                out var nativeProfile))
        {
            var character = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PetEggHatchResult.Rejected(
                PetEggHatchStatus.UnsupportedEggRarity,
                character);
        }

        var hatchRank = PetContent.RollHatchRank(
            eggAptitude.Aptitude,
            _petHatchRankRollSource.NextRoll());

        var contentGrowth = PetContent.RollGrowth(
            checked((short)PetAptitude.Weak),
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var growth = new PetGrowthRoll(
            contentGrowth.TotalGrowth,
            ToPetSavvy(contentGrowth.Rates));
        var contentInitialSavvy = PetContent.RollInitialSavvy(
            eggAptitude.Aptitude,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var initialSavvy = ToPetSavvy(contentInitialSavvy.Values);
        var initialSavvyRoll = new PetInitialSavvyRoll(
            contentInitialSavvy.TotalSavvy,
            initialSavvy);
        var sex = checked((short)RandomNumberGenerator.GetInt32(2));
        var remainingLifetime = nativeProfile.Lifetime;
        var initialSkillSlots = PetSkillSlotPolicy.CreateHatchState(
            aptitude).OpenSkillCellCount;
        var preserveSummonedCompanion = pets.Any(
            static pet => pet.IsSummoned);
        await ClearPetPresenceForHatchAsync(
            connection,
            transaction,
            characterId,
            cancellationToken);

        var petId = await InsertHatchedPetAsync(
            connection,
            transaction,
            characterId,
            species,
            aptitude,
            hatchRank,
            PetContent.Revision.Sha256,
            contentInitialSavvy.TotalSavvy,
            sex,
            remainingLifetime,
            egg.Bound != 0,
            eggAptitude.InnateTalentMask,
            initialSkillSlots,
            isCarried: true,
            isSummoned: preserveSummonedCompanion,
            cancellationToken: cancellationToken);
        await InsertHatchedPetStatsAsync(
            connection,
            transaction,
            petId,
            initialSavvy,
            growth.BaseGrowthRates,
            cancellationToken);
        await InsertHatchedPetStarterSkillAsync(
            connection,
            transaction,
            petId,
            species.StarterSkillId,
            cancellationToken);

        var remainingStack = Math.Max(0, egg.Stack - 1);
        var consumedEgg = remainingStack == 0
            ? CompactItemEntry.Empty
            : egg with { Stack = checked((short)remainingStack) };
        await ApplyKitBagSlotMutationAsync(
            connection,
            transaction,
            characterId,
            kitBagSlot,
            egg,
            consumedEgg,
            "Pet egg hatch",
            "pet-egg-hatch",
            cancellationToken);

        var refreshedCharacter = await GetCharacterByIdAsync(
                connection,
                transaction,
                characterId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Hatched-pet owner {characterId} disappeared while locked.");
        await WritePetEggHatchAuditAsync(
            connection,
            transaction,
            characterId,
            petId,
            kitBagSlot,
            egg.Id,
            egg.Stack,
            remainingStack,
            egg.Quality,
            species.SpeciesId,
            aptitude,
            hatchRank,
            PetContent.Revision.Sha256,
            initialSavvyRoll,
            growth,
            sex,
            remainingLifetime,
            egg.Bound != 0,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new PetEggHatchResult(
            PetEggHatchStatus.Succeeded,
            refreshedCharacter,
            petId,
            species.SpeciesId,
            aptitude,
            hatchRank,
            PetContent.Revision.Sha256,
            initialSavvy,
            initialSavvyRoll,
            growth);
    }

    private async Task<long> InsertHatchedPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        PetSpeciesContentDefinition species,
        PetAptitude aptitude,
        PetHatchRankRoll hatchRank,
        string hatchRankContentRevision,
        int initialSavvyTotal,
        short sex,
        int remainingLifetime,
        bool isBound,
        short talentMask,
        short initialSkillSlots,
        bool isCarried,
        bool isSummoned,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
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
                birth_rank,
                hatch_rank_roll,
                hatch_rank_outcome_order,
                hatch_rank_content_revision,
                current_energy,
                maximum_energy,
                amity,
                satiety,
                remaining_lifetime,
                growth_revealed,
                bound,
                activity_state,
                talent_mask,
                has_owner_merge_talent,
                opened_skill_slots,
                available_skill_slots,
                is_carried,
                is_summoned
            )
            VALUES (
                @characterId,
                @speciesId,
                @name,
                @sex,
                1,
                0,
                @aptitude,
                @initialSavvyTotal,
                @initialSavvyPolicy,
                @initialSavvyTotal,
                @initialSavvyPolicy,
                @initialSavvySource,
                @rank,
                @rank,
                @rankRoll,
                @rankOutcomeOrder,
                @rankContentRevision,
                100,
                100,
                100,
                100,
                @remainingLifetime,
                false,
                @bound,
                'owned',
                @talentMask,
                @hasMergeTalent,
                @initialSkillSlots,
                @initialSkillSlots,
                @isCarried,
                @isSummoned
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "speciesId",
            species.SpeciesId);
        command.Parameters.AddWithValue("name", species.DisplayName);
        command.Parameters.AddWithValue("sex", sex);
        command.Parameters.AddWithValue(
            "aptitude",
            checked((short)aptitude));
        command.Parameters.AddWithValue(
            "initialSavvyTotal",
            initialSavvyTotal);
        command.Parameters.AddWithValue(
            "initialSavvyPolicy",
            PetContent.Settings.InitialSavvyPolicyVersion);
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
            hatchRankContentRevision);
        command.Parameters.AddWithValue(
            "remainingLifetime",
            remainingLifetime);
        command.Parameters.AddWithValue("talentMask", talentMask);
        command.Parameters.AddWithValue(
            "hasMergeTalent",
            (talentMask & PetTalentCatalog.Merge.MaskBit) != 0);
        command.Parameters.AddWithValue(
            "initialSkillSlots",
            initialSkillSlots);
        command.Parameters.AddWithValue("isCarried", isCarried);
        command.Parameters.AddWithValue("isSummoned", isSummoned);
        command.Parameters.AddWithValue("bound", isBound);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<short> ReadPetShedCapacityAsync(
        DbConnection connection,
        DbTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT pet_shed_capacity
            FROM public.character_base
            WHERE id = @characterId;
            """;
        AddPetEggParameter(command, "characterId", characterId);
        return Convert.ToInt16(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ClearPetPresenceForHatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE public.character_pets
            SET is_carried = false,
                is_summoned = false,
                contributes_to_character = false,
                revision = revision + 1,
                updated_at = transaction_timestamp()
            WHERE user_id = @characterId
              AND (is_carried OR is_summoned OR contributes_to_character);
            """;
        AddPetEggParameter(command, "characterId", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddPetEggParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task InsertHatchedPetStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        PetSavvy initialSavvy,
        PetSavvy growth,
        CancellationToken cancellationToken)
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
                birth_initial_savvy,
                rarity_added_savvy
            )
            VALUES
                (
                    @petId, 1, @savvyAgility, @growthAgility,
                    @growthAgility, 0, @savvyAgility, @savvyAgility
                ),
                (
                    @petId, 2, @savvyStrength, @growthStrength,
                    @growthStrength, 0, @savvyStrength, @savvyStrength
                ),
                (
                    @petId, 3, @savvyAccuracy, @growthAccuracy,
                    @growthAccuracy, 0, @savvyAccuracy, @savvyAccuracy
                ),
                (
                    @petId, 4, @savvyTechnique, @growthTechnique,
                    @growthTechnique, 0, @savvyTechnique, @savvyTechnique
                ),
                (
                    @petId, 5, @savvyWisdom, @growthWisdom,
                    @growthWisdom, 0, @savvyWisdom, @savvyWisdom
                ),
                (
                    @petId, 6, @savvyLuck, @growthLuck,
                    @growthLuck, 0, @savvyLuck, @savvyLuck
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue(
            "savvyAgility",
            initialSavvy.Agility);
        command.Parameters.AddWithValue(
            "savvyStrength",
            initialSavvy.Strength);
        command.Parameters.AddWithValue(
            "savvyAccuracy",
            initialSavvy.Accuracy);
        command.Parameters.AddWithValue(
            "savvyTechnique",
            initialSavvy.Technique);
        command.Parameters.AddWithValue(
            "savvyWisdom",
            initialSavvy.Wisdom);
        command.Parameters.AddWithValue(
            "savvyLuck",
            initialSavvy.Luck);
        command.Parameters.AddWithValue(
            "growthAgility",
            growth.Agility);
        command.Parameters.AddWithValue(
            "growthStrength",
            growth.Strength);
        command.Parameters.AddWithValue(
            "growthAccuracy",
            growth.Accuracy);
        command.Parameters.AddWithValue(
            "growthTechnique",
            growth.Technique);
        command.Parameters.AddWithValue(
            "growthWisdom",
            growth.Wisdom);
        command.Parameters.AddWithValue(
            "growthLuck",
            growth.Luck);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertHatchedPetStarterSkillAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        int starterSkillId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO character_pet_skills (
                pet_id,
                skill_id,
                slot_index,
                skill_rank,
                skill_experience,
                is_active
            )
            VALUES (@petId, @skillId, 0, 1, 0, true);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("skillId", starterSkillId);
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
