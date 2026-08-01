using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Pets;

/// <summary>
/// The only runtime-assembly boundary allowed to read the reviewed compiled
/// pet declarations. It exists solely to create the first immutable database
/// publication; active gameplay receives the database-loaded catalog.
/// </summary>
internal static class PetContentBaseline
{
    public const string Source = "reviewed-pet-baseline-v1";

    public static PinnedPetContentCatalog Create()
    {
        var settings = new PetContentSettings(
            PetExperienceCatalog.MinimumLevel,
            PetExperienceCatalog.MaximumLevel,
            PetManagerPlanner.MaximumOwnedPetCount,
            PetManagerPlanner.MaximumPetSkillCount,
            PetManagerPlanner.MinimumPetMergeLevel,
            PetManagerPlanner.MinimumOwnerMergeAmity,
            PetManagerPlanner.MaximumSpiritItems,
            PetRebirthGrowthPolicy.MaximumRebirthCount,
            PetRebirthGrowthPolicy.RequiredSpiritCount,
            PetSpeciesCatalog.EggHatchRuntimeSkillId,
            PetItemCatalog.MergedSpirit,
            PetItemCatalog.FusedHarpyia,
            PetItemCatalog.RebirthSpirit,
            PetItemCatalog.RebornHarpyia,
            PetGrowthPolicy.Version,
            PetInitialSavvyPolicy.Version,
            PetAddedSavvyPolicy.Version,
            PetAddedSavvyPolicy.AllocationWeights
                .Select(static value => checked((short)value))
                .ToArray());

        var species = PetSpeciesCatalog.All
            .Select(static value => new PetSpeciesContentDefinition(
                checked((short)value.Type),
                value.DisplayName,
                checked((short)value.FoodKind),
                value.StarterSkillId,
                value.StarterSkillName,
                value.ClientLifetimeValues,
                value.EggItemId,
                value.EggDeclaredSpeciesType is { } declared
                    ? checked((short)declared)
                    : null,
                value.MagicJadeItemId))
            .ToArray();

        var aptitudes = PetAptitudeCatalog.All
            .Select(value => CreateAptitude(value))
            .ToArray();
        var profiles = PetNativeAptitudeProfileCatalog.All
            .Select(static value => new PetNativeProfileContentDefinition(
                checked((short)value.SpeciesType),
                value.AptitudeValue,
                ToVector(value.StartingTraits),
                ToVector(value.GeniusTraits),
                value.NativeQuality,
                value.NativeSamsara,
                value.NativeGenius,
                value.StarterSkillId,
                value.NativeSkillCount,
                value.NativeProcreate,
                value.Lifetime))
            .ToArray();
        var experience = Enumerable
            .Range(
                PetExperienceCatalog.MinimumLevel,
                PetExperienceCatalog.MaximumLevel -
                    PetExperienceCatalog.MinimumLevel)
            .Select(static level =>
                new PetExperienceStepContentDefinition(
                    checked((short)level),
                    PetExperienceCatalog.RequiredForNextLevel(level)))
            .ToArray();
        var rebirth = Enumerable
            .Range(1, PetRebirthGrowthPolicy.MaximumRebirthCount)
            .Select(static rebirthNumber =>
                CreateRebirthStep(rebirthNumber))
            .ToArray();

        return PinnedPetContentCatalog.Create(
            Source,
            settings,
            species,
            aptitudes,
            profiles,
            experience,
            rebirth);
    }

    private static PetAptitudeContentDefinition CreateAptitude(
        PetAptitudeDefinition aptitude)
    {
        if (!PetGrowthPolicy.TryGet(aptitude.Aptitude, out var growth) ||
            !PetInitialSavvyPolicy.TryGet(
                aptitude.Aptitude,
                out var initial) ||
            !PetAddedSavvyPolicy.TryGet(
                aptitude.Aptitude,
                out var added))
        {
            throw new InvalidDataException(
                $"Pet aptitude {aptitude.Value} has incomplete baseline policy.");
        }

        return new PetAptitudeContentDefinition(
            aptitude.Value,
            aptitude.NameKey,
            aptitude.DisplayName,
            aptitude.IsServerExtension,
            growth.MinimumTotalGrowth,
            growth.MaximumTotalGrowth,
            growth.MaximumStatDeviationFraction,
            initial.MinimumTotalSavvy,
            initial.MaximumTotalSavvy,
            initial.MaximumStatDeviationFraction,
            added.MinimumTotalSavvy,
            added.MaximumTotalSavvy);
    }

    private static PetRebirthStepContentDefinition CreateRebirthStep(
        int rebirthNumber)
    {
        if (!PetRebirthGrowthPolicy.TryGetTier(
                rebirthNumber,
                out var tier))
        {
            throw new InvalidDataException(
                $"Pet rebirth {rebirthNumber} has no baseline tier.");
        }

        var range = PetRebirthGrowthPolicy.GetIncreaseRange(rebirthNumber);
        return new PetRebirthStepContentDefinition(
            checked((short)rebirthNumber),
            checked((short)BaselineRequiredLevelForRebirth(
                rebirthNumber - 1)),
            tier.ChanceItemId,
            tier.ChanceItemName,
            range.Minimum,
            range.Maximum);
    }

    private static int BaselineRequiredLevelForRebirth(
        int completedRebirths) =>
        completedRebirths switch
        {
            0 => 50,
            1 => 80,
            2 => 100,
            3 => 110,
            _ => 120
        };

    private static PetContentStatVector ToVector(PetSavvy value) =>
        new(
            value.Agility,
            value.Strength,
            value.Accuracy,
            value.Technique,
            value.Wisdom,
            value.Luck);
}
