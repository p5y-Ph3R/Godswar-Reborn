using Godswar.Server.Application.Items;

namespace Godswar.Server.Application.Pets;

/// <summary>
/// One immutable pet-content revision pinned for the lifetime of a process.
/// Runtime pet decisions must use this contract rather than authoring tables
/// or compiled baseline declarations.
/// </summary>
internal interface IPetContentCatalog
{
    PetContentRevision Revision { get; }

    PetContentSettings Settings { get; }

    IReadOnlyList<PetSpeciesContentDefinition> Species { get; }

    IReadOnlyList<PetAptitudeContentDefinition> Aptitudes { get; }

    IReadOnlyList<PetNativeProfileContentDefinition> NativeProfiles { get; }

    IReadOnlyList<PetExperienceStepContentDefinition> ExperienceSteps { get; }

    IReadOnlyList<PetRebirthStepContentDefinition> RebirthSteps { get; }

    IReadOnlyList<PetMergeSavvyStepContentDefinition> MergeSavvySteps { get; }

    IReadOnlyList<PetMergeSavvyLookupContentDefinition> MergeSavvyLookup
        { get; }

    IReadOnlyList<PetHatchRankStepContentDefinition> HatchRankSteps { get; }

    IReadOnlyList<PetMergeRankLookupContentDefinition> MergeRankLookup { get; }

    IReadOnlyList<PetMergeRankSpeciesFactorContentDefinition>
        MergeRankSpeciesFactors { get; }

    IReadOnlyList<PetMergeRankSpiritStepContentDefinition>
        MergeRankSpiritSteps { get; }

    bool TryGetSpecies(
        int speciesId,
        out PetSpeciesContentDefinition definition);

    bool TryGetSpeciesByEggItemId(
        uint itemId,
        out PetSpeciesContentDefinition definition);

    bool TryGetSpeciesByMagicJadeItemId(
        uint itemId,
        out PetSpeciesContentDefinition definition);

    bool TryGetAptitude(
        short aptitude,
        out PetAptitudeContentDefinition definition);

    bool TryGetNativeProfile(
        int speciesId,
        short aptitude,
        out PetNativeProfileContentDefinition definition);

    bool TryGetRebirthStep(
        int rebirthNumber,
        out PetRebirthStepContentDefinition definition);

    bool TryGetMergeSavvyStep(
        int aptitude,
        int spiritCount,
        out PetMergeSavvyStepContentDefinition definition);

    bool TryResolveMergeSavvyLookup(
        int savvyDifferenceHundredths,
        out PetMergeSavvyLookupContentDefinition definition);

    bool TryResolveMergeRankLookup(
        int rankDifferenceHundredths,
        out PetMergeRankLookupContentDefinition definition);

    bool TryGetMergeRankSpeciesFactor(
        int speciesId,
        out PetMergeRankSpeciesFactorContentDefinition definition);

    bool TryGetMergeRankSpiritStep(
        int spiritCount,
        out PetMergeRankSpiritStepContentDefinition definition);

    int RequiredExperienceForNextLevel(int currentLevel);

    PetGrowthContentRoll RollGrowth(short aptitude, Random random);

    PetSavvyContentRoll RollInitialSavvy(short aptitude, Random random);

    PetHatchRankRoll RollHatchRank(short aptitude, int roll);

    // Historical schema-compatibility policy only. New hatches must use
    // RollInitialSavvy for Savvy and RollGrowth for Added-value.
    PetSavvyContentRoll RollAddedSavvy(short aptitude, Random random);

    bool IsValidRebirthIncrease(
        int rebirthNumber,
        PetContentStatVector current,
        PetContentStatVector proposed);

    void ValidateItemReferences(IItemTemplateCatalog items);
}
