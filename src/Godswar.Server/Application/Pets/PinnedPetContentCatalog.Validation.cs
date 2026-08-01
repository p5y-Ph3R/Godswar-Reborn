namespace Godswar.Server.Application.Pets;

internal sealed partial class PinnedPetContentCatalog
{
    private static void Validate(
        PetContentSettings settings,
        PetSpeciesContentDefinition[] species,
        PetAptitudeContentDefinition[] aptitudes,
        PetNativeProfileContentDefinition[] profiles,
        PetExperienceStepContentDefinition[] experience,
        PetRebirthStepContentDefinition[] rebirth)
    {
        if (settings.MinimumLevel < 1 ||
            settings.MaximumLevel < settings.MinimumLevel ||
            settings.MaximumLevel > byte.MaxValue ||
            settings.MaximumOwnedPetCount is < 1 or > 64 ||
            settings.MaximumSkillCount is < 1 or > 12 ||
            settings.MinimumMergeLevel < settings.MinimumLevel ||
            settings.MinimumMergeLevel > settings.MaximumLevel ||
            settings.MinimumOwnerMergeAmity is < 0 or > 100 ||
            settings.MaximumSpiritItems is < 1 or > 100 ||
            settings.MaximumRebirthCount is < 1 or > 1000 ||
            settings.RequiredRebirthSpiritCount is < 1 or > 100 ||
            settings.RequiredRebirthSpiritCount > settings.MaximumSpiritItems ||
            settings.EggHatchRuntimeSkillId <= 0 ||
            settings.MergeSpiritItemId == 0 ||
            settings.RestrictedMergeSpiritItemId == 0 ||
            settings.RebirthSpiritItemId == 0 ||
            settings.RestrictedRebirthSpiritItemId == 0 ||
            string.IsNullOrWhiteSpace(settings.GrowthPolicyVersion) ||
            string.IsNullOrWhiteSpace(settings.InitialSavvyPolicyVersion) ||
            string.IsNullOrWhiteSpace(settings.AddedSavvyPolicyVersion) ||
            settings.AddedSavvyWeights.Count != 6 ||
            settings.AddedSavvyWeights.Any(static value => value <= 0) ||
            settings.AddedSavvyWeights.Sum(static value => (int)value) <= 0)
        {
            throw new InvalidOperationException(
                "The published pet-content settings are invalid.");
        }

        if (species.Length is < 1 or > 1024 ||
            species.Select(static value => value.SpeciesId).Distinct().Count() !=
                species.Length ||
            species.SelectMany(static value =>
                    value.EggItemId is { } itemId ? new[] { itemId } : [])
                .Distinct().Count() !=
                species.Count(static value => value.EggItemId.HasValue))
        {
            throw new InvalidOperationException(
                "The published pet species are empty or ambiguous.");
        }

        foreach (var value in species)
        {
            if (value.SpeciesId is < 1 or > byte.MaxValue ||
                string.IsNullOrWhiteSpace(value.DisplayName) ||
                value.FoodKind is < 1 or > 3 ||
                value.StarterSkillId <= 0 ||
                string.IsNullOrWhiteSpace(value.StarterSkillName) ||
                value.LifetimeValues.Count == 0 ||
                value.LifetimeValues.Any(static lifetime => lifetime <= 0) ||
                value.LifetimeValues.Distinct().Count() !=
                    value.LifetimeValues.Count ||
                value.EggItemId == 0 ||
                value.EggDeclaredSpeciesId is <= 0 or > byte.MaxValue ||
                value.MagicJadeItemId == 0)
            {
                throw new InvalidOperationException(
                    $"Published pet species {value.SpeciesId} is invalid.");
            }
        }

        if (aptitudes.Length is < 1 or > byte.MaxValue ||
            aptitudes.Select(static value => value.Aptitude).Distinct().Count() !=
                aptitudes.Length)
        {
            throw new InvalidOperationException(
                "The published pet aptitudes are empty or ambiguous.");
        }

        foreach (var value in aptitudes)
        {
            if (value.Aptitude is < 1 or > byte.MaxValue ||
                string.IsNullOrWhiteSpace(value.NameKey) ||
                string.IsNullOrWhiteSpace(value.DisplayName) ||
                value.MinimumTotalGrowth <= 0m ||
                value.MaximumTotalGrowth < value.MinimumTotalGrowth ||
                value.MaximumGrowthStatDeviation is <= 0m or > 0.25m ||
                value.MinimumInitialSavvy <= 0 ||
                value.MaximumInitialSavvy < value.MinimumInitialSavvy ||
                value.MaximumInitialSavvyStatDeviation is <= 0m or > 0.25m ||
                value.MinimumAddedSavvy <= 0 ||
                value.MaximumAddedSavvy < value.MinimumAddedSavvy)
            {
                throw new InvalidOperationException(
                    $"Published pet aptitude {value.Aptitude} is invalid.");
            }
        }

        ValidateProfiles(species, aptitudes, profiles);
        ValidateExperience(settings, experience);
        ValidateRebirth(settings, rebirth);
    }

    private static void ValidateProfiles(
        PetSpeciesContentDefinition[] species,
        PetAptitudeContentDefinition[] aptitudes,
        PetNativeProfileContentDefinition[] profiles)
    {
        var speciesById = species.ToDictionary(static value => value.SpeciesId);
        var aptitudeIds = aptitudes.Select(static value => value.Aptitude)
            .ToHashSet();
        if (profiles.Length < species.Length ||
            profiles.Select(static value => (value.SpeciesId, value.Aptitude))
                .Distinct().Count() != profiles.Length)
        {
            throw new InvalidOperationException(
                "Published native pet profiles are incomplete or ambiguous.");
        }

        foreach (var value in profiles)
        {
            if (!speciesById.TryGetValue(value.SpeciesId, out var petSpecies) ||
                !aptitudeIds.Contains(value.Aptitude) ||
                !value.StartingTraits.IsNonNegative ||
                !value.GeniusTraits.IsNonNegative ||
                value.NativeQuality < 0 || value.NativeSamsara < 0 ||
                value.NativeGenius < 0 || value.NativeSkillCount <= 0 ||
                value.NativeProcreate < 0 || value.Lifetime <= 0 ||
                value.StarterSkillId != petSpecies.StarterSkillId)
            {
                throw new InvalidOperationException(
                    $"Published native pet profile {value.SpeciesId}/{value.Aptitude} is invalid.");
            }
        }

        foreach (var petSpecies in species)
        {
            var lifetimes = profiles
                .Where(value => value.SpeciesId == petSpecies.SpeciesId)
                .Select(static value => value.Lifetime)
                .Distinct()
                .Order()
                .ToArray();
            if (!lifetimes.SequenceEqual(
                    petSpecies.LifetimeValues.Distinct().Order()))
            {
                throw new InvalidOperationException(
                    $"Published native lifetimes disagree for species {petSpecies.SpeciesId}.");
            }
        }
    }

    private static void ValidateExperience(
        PetContentSettings settings,
        PetExperienceStepContentDefinition[] experience)
    {
        if (experience.Length != settings.MaximumLevel - settings.MinimumLevel ||
            !experience.Select(static value => (int)value.CurrentLevel)
                .SequenceEqual(
                    Enumerable.Range(settings.MinimumLevel, experience.Length)) ||
            experience.Any(static value => value.RequiredExperience <= 0))
        {
            throw new InvalidOperationException(
                "Published pet experience steps are incomplete or invalid.");
        }
    }

    private static void ValidateRebirth(
        PetContentSettings settings,
        PetRebirthStepContentDefinition[] rebirth)
    {
        if (rebirth.Length != settings.MaximumRebirthCount ||
            !rebirth.Select(static value => (int)value.RebirthNumber)
                .SequenceEqual(Enumerable.Range(1, rebirth.Length)))
        {
            throw new InvalidOperationException(
                "Published pet rebirth steps are incomplete.");
        }

        foreach (var value in rebirth)
        {
            if (value.RequiredPetLevel < settings.MinimumLevel ||
                value.RequiredPetLevel > settings.MaximumLevel ||
                value.ChanceItemId == 0 ||
                string.IsNullOrWhiteSpace(value.ChanceItemName) ||
                value.MinimumIncreasePerStat < 0m ||
                value.MaximumIncreasePerStat < value.MinimumIncreasePerStat)
            {
                throw new InvalidOperationException(
                    $"Published pet rebirth step {value.RebirthNumber} is invalid.");
            }
        }
    }
}
