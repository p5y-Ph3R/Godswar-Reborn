namespace Godswar.Server.Application.Pets;

internal sealed record PetContentRevision(
    string Sha256,
    int SpeciesCount,
    int AptitudeCount,
    int NativeProfileCount,
    int ExperienceStepCount,
    int RebirthStepCount,
    string Source);

internal sealed record PetContentSettings(
    short MinimumLevel,
    short MaximumLevel,
    short MaximumOwnedPetCount,
    short MaximumSkillCount,
    short MinimumMergeLevel,
    short MinimumOwnerMergeAmity,
    short MaximumSpiritItems,
    short MaximumRebirthCount,
    short RequiredRebirthSpiritCount,
    int EggHatchRuntimeSkillId,
    uint MergeSpiritItemId,
    uint RestrictedMergeSpiritItemId,
    uint RebirthSpiritItemId,
    uint RestrictedRebirthSpiritItemId,
    string GrowthPolicyVersion,
    string InitialSavvyPolicyVersion,
    string AddedSavvyPolicyVersion,
    IReadOnlyList<short> AddedSavvyWeights);

internal sealed record PetSpeciesContentDefinition(
    short SpeciesId,
    string DisplayName,
    short FoodKind,
    int StarterSkillId,
    string StarterSkillName,
    IReadOnlyList<int> LifetimeValues,
    uint? EggItemId,
    short? EggDeclaredSpeciesId,
    uint MagicJadeItemId);

internal sealed record PetAptitudeContentDefinition(
    short Aptitude,
    string NameKey,
    string DisplayName,
    bool IsServerExtension,
    decimal MinimumTotalGrowth,
    decimal MaximumTotalGrowth,
    decimal MaximumGrowthStatDeviation,
    int MinimumInitialSavvy,
    int MaximumInitialSavvy,
    decimal MaximumInitialSavvyStatDeviation,
    int MinimumAddedSavvy,
    int MaximumAddedSavvy);

internal readonly record struct PetContentStatVector(
    decimal Agility,
    decimal Strength,
    decimal Accuracy,
    decimal Technique,
    decimal Wisdom,
    decimal Luck)
{
    public bool IsNonNegative =>
        Agility >= 0m && Strength >= 0m && Accuracy >= 0m &&
        Technique >= 0m && Wisdom >= 0m && Luck >= 0m;
}

internal sealed record PetNativeProfileContentDefinition(
    short SpeciesId,
    short Aptitude,
    PetContentStatVector StartingTraits,
    PetContentStatVector GeniusTraits,
    int NativeQuality,
    int NativeSamsara,
    int NativeGenius,
    int StarterSkillId,
    int NativeSkillCount,
    int NativeProcreate,
    int Lifetime);

internal sealed record PetExperienceStepContentDefinition(
    short CurrentLevel,
    int RequiredExperience);

internal sealed record PetRebirthStepContentDefinition(
    short RebirthNumber,
    short RequiredPetLevel,
    uint ChanceItemId,
    string ChanceItemName,
    decimal MinimumIncreasePerStat,
    decimal MaximumIncreasePerStat);

internal sealed record PetGrowthContentRoll(
    decimal TotalGrowth,
    PetContentStatVector Rates);

internal sealed record PetSavvyContentRoll(
    int TotalSavvy,
    PetContentStatVector Values);
