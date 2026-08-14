using System.Text.Json;
using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private static string SerializePetMergeState(
        LockedOwnerMergePet? primary,
        LockedOwnerMergePet? deputy,
        PetSavvy? savvy,
        PetSavvy? deputySavvy,
        PetMergeSavvyRollEvidence? savvyEvidence,
        PetMergeRankRollEvidence? rankEvidence,
        PetToPetMergeCommand request,
        string contentRevision)
    {
        object? serializedSavvyEvidence = savvyEvidence is null
            ? null
            : new
            {
                policy_revision = savvyEvidence.PolicyRevision,
                content_revision = savvyEvidence.ContentRevision,
                deputy_species_id = savvyEvidence.DeputySpeciesId,
                species_factor = savvyEvidence.SpeciesFactor,
                spirit_count = savvyEvidence.SpiritCount,
                minimum_percent = savvyEvidence.MinimumPercent,
                maximum_percent = savvyEvidence.MaximumPercent,
                stats = savvyEvidence.Stats.Select(stat => new
                {
                    stat_code = stat.StatCode,
                    primary_basic_hundredths =
                        stat.PrimaryBasicHundredths,
                    deputy_basic_hundredths =
                        stat.DeputyBasicHundredths,
                    deputy_added_hundredths =
                        stat.DeputyAddedHundredths,
                    added_contribution_hundredths =
                        stat.AddedContributionHundredths,
                    savvy_difference_hundredths =
                        stat.SavvyDifferenceHundredths,
                    lookup_minimum_savvy_difference =
                        stat.LookupMinimumSavvyDifference,
                    lookup_base_increase = stat.LookupBaseIncrease,
                    minimum_increase_hundredths =
                        stat.MinimumIncreaseHundredths,
                    maximum_increase_hundredths =
                        stat.MaximumIncreaseHundredths,
                    rolled_increase_hundredths =
                        stat.RolledIncreaseHundredths
                })
            };
        object? serializedRankEvidence = rankEvidence is null
            ? null
            : new
            {
                policy_revision = rankEvidence.PolicyRevision,
                content_revision = rankEvidence.ContentRevision,
                primary_rank_hundredths =
                    rankEvidence.PrimaryRankHundredths,
                deputy_rank_hundredths =
                    rankEvidence.DeputyRankHundredths,
                rank_difference_hundredths =
                    rankEvidence.RankDifferenceHundredths,
                lookup_minimum_rank_difference =
                    rankEvidence.LookupMinimumRankDifference,
                lookup_base_increase = rankEvidence.LookupBaseIncrease,
                deputy_species_id = rankEvidence.DeputySpeciesId,
                species_factor = rankEvidence.SpeciesFactor,
                applied_species_factor =
                    rankEvidence.AppliedSpeciesFactor,
                spirit_count = rankEvidence.SpiritCount,
                minimum_percent = rankEvidence.MinimumPercent,
                maximum_percent = rankEvidence.MaximumPercent,
                factor_adjusted_base_increase =
                    rankEvidence.FactorAdjustedBaseIncrease,
                uncapped_minimum_increase =
                    rankEvidence.UncappedMinimumIncrease,
                uncapped_maximum_increase =
                    rankEvidence.UncappedMaximumIncrease,
                remaining_to_cap = rankEvidence.RemainingToCap,
                effective_minimum_increase =
                    rankEvidence.EffectiveMinimumIncrease,
                effective_maximum_increase =
                    rankEvidence.EffectiveMaximumIncrease,
                rolled_increase = rankEvidence.RolledIncrease,
                cap_applied = rankEvidence.CapApplied,
                maximum_rank_hundredths =
                    rankEvidence.MaximumRankHundredths
            };
        return JsonSerializer.Serialize(new
        {
            primary_pet_id = primary?.PetId,
            primary_revision = primary?.Revision,
            primary_completed_merges = primary?.CompletedPetMerges,
            primary_rank = primary?.Rank,
            deputy_pet_id = deputy?.PetId,
            deputy_revision = deputy?.Revision,
            deputy_species_id = deputy?.SpeciesId,
            deputy_aptitude = deputy?.Aptitude,
            deputy_level = deputy?.Level,
            deputy_rank = deputy?.Rank,
            deputy_initial_savvy = deputySavvy,
            material_item_id = request.MaterialItemId,
            material_quantity = request.MaterialQuantity,
            savvy_evidence = serializedSavvyEvidence,
            rank_evidence = serializedRankEvidence,
            initial_savvy = savvy,
            pet_content_revision = contentRevision
        });
    }
}
