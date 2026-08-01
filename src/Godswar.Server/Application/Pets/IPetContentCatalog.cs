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

    bool TryGetSpecies(
        int speciesId,
        out PetSpeciesContentDefinition definition);

    bool TryGetSpeciesByEggItemId(
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

    int RequiredExperienceForNextLevel(int currentLevel);

    PetGrowthContentRoll RollGrowth(short aptitude, Random random);

    PetSavvyContentRoll RollInitialSavvy(short aptitude, Random random);

    PetSavvyContentRoll RollAddedSavvy(short aptitude, Random random);

    bool IsValidRebirthIncrease(
        int rebirthNumber,
        PetContentStatVector current,
        PetContentStatVector proposed);

    void ValidateItemReferences(IItemTemplateCatalog items);
}
