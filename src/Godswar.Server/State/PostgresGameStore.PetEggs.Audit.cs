using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    private async Task WritePetEggHatchAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        long petId,
        int kitBagSlot,
        uint eggItemId,
        short originalStack,
        int remainingStack,
        short eggQuality,
        int speciesType,
        PetAptitude aptitude,
        PetHatchRankRoll hatchRank,
        string hatchRankContentRevision,
        PetInitialSavvyRoll initialSavvy,
        PetGrowthRoll growth,
        short sex,
        int remainingLifetime,
        bool isBound,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pet_operation_audit (
                request_id,
                user_id,
                user_id_snapshot,
                pet_id,
                pet_id_snapshot,
                operation,
                outcome,
                before_state,
                after_state,
                consumed_items
            )
            VALUES (
                @requestId,
                @characterId,
                @characterId,
                @petId,
                @petId,
                'hatch',
                'committed',
                jsonb_build_object(
                    'kit_bag_slot', @kitBagSlot,
                    'egg_item_id', @eggItemId,
                    'egg_quality', @eggQuality,
                    'stack', @originalStack
                ),
                jsonb_build_object(
                    'pet_id', @petId,
                    'species_type', @speciesType,
                    'aptitude', @aptitude,
                    'birth_rank', @birthRank,
                    'hatch_rank_roll', @hatchRankRoll,
                    'hatch_rank_outcome_order', @hatchRankOutcomeOrder,
                    'hatch_rank_content_revision', @hatchRankContentRevision,
                    'sex', @sex,
                    'remaining_lifetime', @remainingLifetime,
                    'bound', @bound,
                    'total_initial_savvy', @totalInitialSavvy,
                    'initial_savvy', jsonb_build_object(
                        'agility', @initialSavvyAgility,
                        'strength', @initialSavvyStrength,
                        'accuracy', @initialSavvyAccuracy,
                        'technique', @initialSavvyTechnique,
                        'wisdom', @initialSavvyWisdom,
                        'luck', @initialSavvyLuck
                    ),
                    'total_added_savvy', @totalAddedSavvy,
                    'added_savvy', jsonb_build_object(
                        'agility', @addedSavvyAgility,
                        'strength', @addedSavvyStrength,
                        'accuracy', @addedSavvyAccuracy,
                        'technique', @addedSavvyTechnique,
                        'wisdom', @addedSavvyWisdom,
                        'luck', @addedSavvyLuck
                    ),
                    'total_growth', @totalGrowth,
                    'base_growth', jsonb_build_object(
                        'agility', @growthAgility,
                        'strength', @growthStrength,
                        'accuracy', @growthAccuracy,
                        'technique', @growthTechnique,
                        'wisdom', @growthWisdom,
                        'luck', @growthLuck
                    ),
                    'remaining_egg_stack', @remainingStack,
                    'initial_savvy_source', @initialSavvySource,
                    'initial_savvy_policy', @initialSavvyPolicy,
                    'added_savvy_policy', @addedSavvyPolicy,
                    'growth_policy', @growthPolicy
                ),
                jsonb_build_array(
                    jsonb_build_object(
                        'item_id', @eggItemId,
                        'quantity', 1,
                        'kit_bag_slot', @kitBagSlot
                    )
                )
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("requestId", Guid.NewGuid());
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("petId", petId);
        command.Parameters.AddWithValue("kitBagSlot", kitBagSlot);
        command.Parameters.AddWithValue(
            "eggItemId",
            checked((int)eggItemId));
        command.Parameters.AddWithValue("originalStack", originalStack);
        command.Parameters.AddWithValue("remainingStack", remainingStack);
        command.Parameters.AddWithValue("eggQuality", eggQuality);
        command.Parameters.AddWithValue("speciesType", speciesType);
        command.Parameters.AddWithValue(
            "aptitude",
            checked((short)aptitude));
        command.Parameters.AddWithValue("birthRank", hatchRank.Rank);
        command.Parameters.AddWithValue("hatchRankRoll", hatchRank.Roll);
        command.Parameters.AddWithValue(
            "hatchRankOutcomeOrder",
            hatchRank.OutcomeOrder);
        command.Parameters.AddWithValue(
            "hatchRankContentRevision",
            hatchRankContentRevision);
        command.Parameters.AddWithValue("sex", sex);
        command.Parameters.AddWithValue(
            "remainingLifetime",
            remainingLifetime);
        command.Parameters.AddWithValue("bound", isBound);
        command.Parameters.AddWithValue(
            "totalInitialSavvy",
            initialSavvy.TotalSavvy);
        command.Parameters.AddWithValue(
            "initialSavvyAgility",
            initialSavvy.InitialSavvy.Agility);
        command.Parameters.AddWithValue(
            "initialSavvyStrength",
            initialSavvy.InitialSavvy.Strength);
        command.Parameters.AddWithValue(
            "initialSavvyAccuracy",
            initialSavvy.InitialSavvy.Accuracy);
        command.Parameters.AddWithValue(
            "initialSavvyTechnique",
            initialSavvy.InitialSavvy.Technique);
        command.Parameters.AddWithValue(
            "initialSavvyWisdom",
            initialSavvy.InitialSavvy.Wisdom);
        command.Parameters.AddWithValue(
            "initialSavvyLuck",
            initialSavvy.InitialSavvy.Luck);
        command.Parameters.AddWithValue(
            "totalAddedSavvy",
            growth.TotalGrowth);
        command.Parameters.AddWithValue(
            "addedSavvyAgility",
            growth.BaseGrowthRates.Agility);
        command.Parameters.AddWithValue(
            "addedSavvyStrength",
            growth.BaseGrowthRates.Strength);
        command.Parameters.AddWithValue(
            "addedSavvyAccuracy",
            growth.BaseGrowthRates.Accuracy);
        command.Parameters.AddWithValue(
            "addedSavvyTechnique",
            growth.BaseGrowthRates.Technique);
        command.Parameters.AddWithValue(
            "addedSavvyWisdom",
            growth.BaseGrowthRates.Wisdom);
        command.Parameters.AddWithValue(
            "addedSavvyLuck",
            growth.BaseGrowthRates.Luck);
        command.Parameters.AddWithValue(
            "totalGrowth",
            growth.TotalGrowth);
        command.Parameters.AddWithValue(
            "growthAgility",
            growth.BaseGrowthRates.Agility);
        command.Parameters.AddWithValue(
            "growthStrength",
            growth.BaseGrowthRates.Strength);
        command.Parameters.AddWithValue(
            "growthAccuracy",
            growth.BaseGrowthRates.Accuracy);
        command.Parameters.AddWithValue(
            "growthTechnique",
            growth.BaseGrowthRates.Technique);
        command.Parameters.AddWithValue(
            "growthWisdom",
            growth.BaseGrowthRates.Wisdom);
        command.Parameters.AddWithValue(
            "growthLuck",
            growth.BaseGrowthRates.Luck);
        command.Parameters.AddWithValue(
            "initialSavvySource",
            PetSavvyRuntimeSemantics.SourceVersion);
        command.Parameters.AddWithValue(
            "initialSavvyPolicy",
            PetContent.Settings.InitialSavvyPolicyVersion);
        command.Parameters.AddWithValue(
            "addedSavvyPolicy",
            PetContent.Settings.GrowthPolicyVersion);
        command.Parameters.AddWithValue(
            "growthPolicy",
            PetContent.Settings.GrowthPolicyVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
