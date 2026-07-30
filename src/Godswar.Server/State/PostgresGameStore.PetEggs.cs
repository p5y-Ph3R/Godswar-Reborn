using System.Security.Cryptography;
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

        if (!PetSpeciesCatalog.TryGetByEggItemId(
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

        if (pets.Count >= PetManagerPlanner.MaximumOwnedPetCount)
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

        if (!PetAptitudeCatalog.TryGet(
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

        var aptitude = eggAptitude.Aptitude;
        if (!PetNativeAptitudeProfileCatalog.TryGet(
                species.Type,
                aptitude,
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

        var growth = PetGrowthPolicy.Roll(
            aptitude,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var initialSavvy = growth.BaseGrowthRates;
        var addedSavvy = PetAddedSavvyPolicy.Roll(
            aptitude,
            new Random(RandomNumberGenerator.GetInt32(int.MaxValue)));
        var sex = checked((short)RandomNumberGenerator.GetInt32(2));
        var remainingLifetime = nativeProfile.Lifetime;

        var petId = await InsertHatchedPetAsync(
            connection,
            transaction,
            characterId,
            species,
            aptitude,
            addedSavvy.TotalSavvy,
            sex,
            remainingLifetime,
            egg.Bound != 0,
            cancellationToken);
        await InsertHatchedPetStatsAsync(
            connection,
            transaction,
            petId,
            initialSavvy,
            addedSavvy.AddedSavvy,
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
            species.Type,
            aptitude,
            initialSavvy,
            addedSavvy,
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
            species.Type,
            aptitude,
            initialSavvy,
            addedSavvy,
            growth);
    }

    private static async Task<long> InsertHatchedPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        PetSpeciesDefinition species,
        PetAptitude aptitude,
        int addedSavvyTotal,
        short sex,
        int remainingLifetime,
        bool isBound,
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
                rarity_added_savvy_baseline_total,
                rarity_added_savvy_policy_version,
                initial_savvy_source_version,
                rank,
                current_energy,
                maximum_energy,
                amity,
                satiety,
                remaining_lifetime,
                growth_revealed,
                bound,
                activity_state
            )
            VALUES (
                @characterId,
                @speciesId,
                @name,
                @sex,
                1,
                0,
                @aptitude,
                @addedSavvyTotal,
                @addedSavvyPolicy,
                'growth-x1-v1',
                0,
                100,
                100,
                100,
                100,
                @remainingLifetime,
                false,
                @bound,
                'owned'
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "speciesId",
            checked((short)species.Type));
        command.Parameters.AddWithValue("name", species.DisplayName);
        command.Parameters.AddWithValue("sex", sex);
        command.Parameters.AddWithValue(
            "aptitude",
            checked((short)aptitude));
        command.Parameters.AddWithValue(
            "addedSavvyTotal",
            addedSavvyTotal);
        command.Parameters.AddWithValue(
            "addedSavvyPolicy",
            PetAddedSavvyPolicy.Version);
        command.Parameters.AddWithValue(
            "remainingLifetime",
            remainingLifetime);
        command.Parameters.AddWithValue("bound", isBound);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertHatchedPetStatsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long petId,
        PetSavvy initialSavvy,
        PetSavvy addedSavvy,
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
                    @petId, 1, @savvyAgility, @addedSavvyAgility,
                    @growthAgility, 0, @savvyAgility, @addedSavvyAgility
                ),
                (
                    @petId, 2, @savvyStrength, @addedSavvyStrength,
                    @growthStrength, 0, @savvyStrength, @addedSavvyStrength
                ),
                (
                    @petId, 3, @savvyAccuracy, @addedSavvyAccuracy,
                    @growthAccuracy, 0, @savvyAccuracy, @addedSavvyAccuracy
                ),
                (
                    @petId, 4, @savvyTechnique, @addedSavvyTechnique,
                    @growthTechnique, 0, @savvyTechnique, @addedSavvyTechnique
                ),
                (
                    @petId, 5, @savvyWisdom, @addedSavvyWisdom,
                    @growthWisdom, 0, @savvyWisdom, @addedSavvyWisdom
                ),
                (
                    @petId, 6, @savvyLuck, @addedSavvyLuck,
                    @growthLuck, 0, @savvyLuck, @addedSavvyLuck
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
            "addedSavvyAgility",
            addedSavvy.Agility);
        command.Parameters.AddWithValue(
            "addedSavvyStrength",
            addedSavvy.Strength);
        command.Parameters.AddWithValue(
            "addedSavvyAccuracy",
            addedSavvy.Accuracy);
        command.Parameters.AddWithValue(
            "addedSavvyTechnique",
            addedSavvy.Technique);
        command.Parameters.AddWithValue(
            "addedSavvyWisdom",
            addedSavvy.Wisdom);
        command.Parameters.AddWithValue(
            "addedSavvyLuck",
            addedSavvy.Luck);
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

}
