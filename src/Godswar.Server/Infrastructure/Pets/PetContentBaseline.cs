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
    public const string Source = "reviewed-pet-baseline-v10";

    public static PinnedPetContentCatalog Create()
    {
        var settings = new PetContentSettings(
            PetExperienceCatalog.MinimumLevel,
            PetExperienceCatalog.MaximumLevel,
            PetManagerPlanner.MaximumOwnedPetCount,
            PetManagerPlanner.MaximumPetSkillCount,
            655.35m,
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
        var profiles = CreateNativeProfiles();
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
        var mergeSavvySteps = CreateMergeSavvySteps();
        var mergeSavvyLookup = PetMergeSavvyLookupContentBaseline.Create();
        var hatchRankSteps = PetHatchRankContentBaseline.Create();
        var mergeRankLookup = PetMergeRankContentBaseline.CreateLookup();
        var mergeRankSpeciesFactors =
            PetMergeRankContentBaseline.CreateSpeciesFactors();
        var mergeRankSpiritSteps =
            PetMergeRankContentBaseline.CreateSpiritSteps();

        return PinnedPetContentCatalog.Create(
            Source,
            settings,
            species,
            aptitudes,
            profiles,
            experience,
            rebirth,
            mergeSavvySteps,
            mergeSavvyLookup,
            hatchRankSteps,
            mergeRankLookup,
            mergeRankSpeciesFactors,
            mergeRankSpiritSteps);
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
            added.MaximumTotalSavvy,
            checked((short)PetInnateTalentPolicy.Resolve(
                aptitude.Aptitude)));
    }

    private static PetNativeProfileContentDefinition[] CreateNativeProfiles()
    {
        var profiles = PetNativeAptitudeProfileCatalog.All
            .Select(static value => CreateNativeProfile(value))
            .ToList();

        // Stock Pet_Confect.xml has no aptitude-6 row. Calm is a project
        // aptitude, so its immutable database baseline uses each species'
        // immediately lower, client-authored Rational (aptitude 5) profile as
        // a conservative wire-compatible source. This preserves the species'
        // starter skill, lifetime and native scalar shape without modifying or
        // pretending to extend the fingerprinted 495-row stock catalog. Calm's
        // growth and added-savvy rolls still come from its own project policy.
        foreach (var species in PetSpeciesCatalog.All)
        {
            if (!PetNativeAptitudeProfileCatalog.TryGet(
                    species.Type,
                    PetAptitude.Rational,
                    out var source))
            {
                throw new InvalidDataException(
                    $"Pet species {species.Type} has no Rational compatibility source for Calm.");
            }

            profiles.Add(
                CreateNativeProfile(source) with
                {
                    Aptitude = (short)PetAptitude.Calm
                });
        }

        return profiles
            .OrderBy(static value => value.SpeciesId)
            .ThenBy(static value => value.Aptitude)
            .ToArray();
    }

    private static PetNativeProfileContentDefinition CreateNativeProfile(
        PetNativeAptitudeProfile value) =>
        new(
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
            value.Lifetime);

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

        // The content row publishes the best (five-spirit) preview. Runtime
        // validation uses the selected 0..5 spirit count directly.
        var range = PetRebirthSpiritPolicy.GetIncreaseRange(
            PetRebirthSpiritPolicy.MaximumCount);
        return new PetRebirthStepContentDefinition(
            checked((short)rebirthNumber),
            checked((short)BaselineRequiredLevelForRebirth(
                rebirthNumber - 1)),
            tier.ChanceItemId,
            tier.ChanceItemName,
            range.Minimum,
            range.Maximum);
    }

    private static PetMergeSavvyStepContentDefinition[]
        CreateMergeSavvySteps() =>
        [
            MergeSavvyStep(PetAptitude.Brave, 3.95m, 7.90m),
            MergeSavvyStep(PetAptitude.Zealous, 3.95m, 7.90m),
            MergeSavvyStep(PetAptitude.Smart, 3.95m, 7.90m),
            MergeSavvyStep(PetAptitude.Overbearing, 13.50m, 18.00m),
            MergeSavvyStep(PetAptitude.Ferocious, 13.50m, 18.00m),
            MergeSavvyStep(PetAptitude.Almighty, 13.50m, 18.00m),
            MergeSavvyStep(PetAptitude.Godly, 13.50m, 18.00m),
            MergeSavvyStep(PetAptitude.Celestial, 13.50m, 18.00m),
            MergeSavvyStep(PetAptitude.Transcendent, 13.50m, 18.00m)
        ];

    private static PetMergeSavvyStepContentDefinition MergeSavvyStep(
        PetAptitude aptitude,
        decimal minimum,
        decimal maximum) =>
        new(
            checked((short)aptitude),
            PetManagerPlanner.MaximumSpiritItems,
            minimum,
            maximum);

    private static int BaselineRequiredLevelForRebirth(
        int completedRebirths) =>
        completedRebirths switch
        {
            // Product override: the installed stock resources say 50, but
            // this server intentionally opens the first rebirth at level 30.
            0 => 30,
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
