namespace Godswar.Server.Application.Pets;

internal sealed partial class PinnedPetContentCatalog
{
    private static void Validate(
        PetContentSettings settings,
        PetSpeciesContentDefinition[] species,
        PetAptitudeContentDefinition[] aptitudes,
        PetNativeProfileContentDefinition[] profiles,
        PetExperienceStepContentDefinition[] experience,
        PetRebirthStepContentDefinition[] rebirth,
        PetMergeSavvyStepContentDefinition[] mergeSavvy,
        PetMergeSavvyLookupContentDefinition[] mergeSavvyLookup,
        PetHatchRankStepContentDefinition[] hatchRank,
        PetMergeRankLookupContentDefinition[] mergeRankLookup,
        PetMergeRankSpeciesFactorContentDefinition[] mergeRankSpeciesFactors,
        PetMergeRankSpiritStepContentDefinition[] mergeRankSpiritSteps)
    {
        if (settings.MinimumLevel < 1 ||
            settings.MaximumLevel < settings.MinimumLevel ||
            settings.MaximumLevel > byte.MaxValue ||
            settings.MaximumOwnedPetCount is < 1 or > 64 ||
            settings.MaximumSkillCount is < 1 or > 12 ||
            settings.MaximumRank <= 0m ||
            settings.MaximumRank > ushort.MaxValue / 100m ||
            settings.MaximumRank * 100m !=
                decimal.Truncate(settings.MaximumRank * 100m) ||
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
                value.MaximumAddedSavvy < value.MinimumAddedSavvy ||
                value.InnateTalentMask is < 0 or > 31 ||
                value.InnateTalentMask !=
                    ExpectedInnateTalentMask(value.Aptitude))
            {
                throw new InvalidOperationException(
                    $"Published pet aptitude {value.Aptitude} is invalid.");
            }
        }

        ValidateProfiles(species, aptitudes, profiles);
        ValidateExperience(settings, experience);
        ValidateRebirth(settings, rebirth);
        ValidateMergeSavvy(aptitudes, mergeSavvy);
        ValidateMergeSavvyLookup(mergeSavvyLookup);
        PetHatchRankContentPolicy.Validate(
            aptitudes.Select(static value => value.Aptitude).ToArray(),
            hatchRank);
        ValidateInstalledClientHatchRankCompatibility(hatchRank);
        if (hatchRank.Any(value => value.Rank > settings.MaximumRank))
        {
            throw new InvalidOperationException(
                "Published pet hatch-rank outcomes exceed the rank cap.");
        }
        ValidateMergeRank(
            settings,
            species,
            mergeRankLookup,
            mergeRankSpeciesFactors,
            mergeRankSpiritSteps);
        ValidateMagicJadeAppearances(species, mergeRankSpeciesFactors);
    }

    private static short ExpectedInnateTalentMask(short aptitude) =>
        aptitude >= 14
            ? (short)31
            : aptitude >= 10
                ? (short)26
                : (short)0;

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

    private static void ValidateMergeSavvy(
        PetAptitudeContentDefinition[] aptitudes,
        PetMergeSavvyStepContentDefinition[] mergeSavvy)
    {
        if (mergeSavvy
                .Select(static value =>
                    (value.Aptitude, value.SpiritCount))
                .Distinct()
                .Count() != mergeSavvy.Length)
        {
            throw new InvalidOperationException(
                "Published pet merge-savvy steps are ambiguous.");
        }

        var aptitudeIds = aptitudes
            .Select(static value => value.Aptitude)
            .ToHashSet();
        foreach (var value in mergeSavvy)
        {
            if (!aptitudeIds.Contains(value.Aptitude) ||
                value.SpiritCount is < 0 or > 5 ||
                value.MinimumIncreasePerStat < 0.01m ||
                value.MaximumIncreasePerStat <
                    value.MinimumIncreasePerStat)
            {
                throw new InvalidOperationException(
                    $"Published pet merge-savvy step " +
                    $"{value.Aptitude}/{value.SpiritCount} is invalid.");
            }
        }
    }

    private static void ValidateMergeRank(
        PetContentSettings settings,
        PetSpeciesContentDefinition[] species,
        PetMergeRankLookupContentDefinition[] lookup,
        PetMergeRankSpeciesFactorContentDefinition[] speciesFactors,
        PetMergeRankSpiritStepContentDefinition[] spiritSteps)
    {
        if (lookup.Length != 200 ||
            lookup[0].MinimumRankDifference != -3000 ||
            lookup[^1].MinimumRankDifference != 500 ||
            lookup[0].BaseIncrease != 1 ||
            lookup[^1].BaseIncrease != 300 ||
            lookup.Select(static value => value.MinimumRankDifference)
                .Distinct().Count() != lookup.Length ||
            lookup.Select(static value => value.BaseIncrease)
                .Distinct().Count() != lookup.Length ||
            lookup.Zip(lookup.Skip(1), static (left, right) =>
                    right.MinimumRankDifference > left.MinimumRankDifference &&
                    right.BaseIncrease > left.BaseIncrease)
                .Any(static value => !value))
        {
            throw new InvalidOperationException(
                "Published pet Merge rank lookup is incomplete or ambiguous.");
        }

        var speciesIds = species
            .Select(static value => value.SpeciesId)
            .Order()
            .ToArray();
        if (!speciesFactors.Select(static value => value.SpeciesId)
                .Order().SequenceEqual(speciesIds) ||
            speciesFactors.Any(static value =>
                value.Factor <= 0m ||
                value.Factor > 10m ||
                value.Factor * 10m != decimal.Truncate(value.Factor * 10m)))
        {
            throw new InvalidOperationException(
                "Published pet Merge rank species factors are invalid.");
        }

        if (spiritSteps.Length != settings.MaximumSpiritItems + 1 ||
            !spiritSteps.Select(static value => (int)value.SpiritCount)
                .SequenceEqual(Enumerable.Range(0, spiritSteps.Length)) ||
            spiritSteps.Any(static value =>
                value.MinimumPercent is < 0 or > 100 ||
                value.MaximumPercent < value.MinimumPercent ||
                value.MaximumPercent > 100))
        {
            throw new InvalidOperationException(
                "Published pet Merge rank spirit steps are invalid.");
        }

        ValidateInstalledClientMergeRankCompatibility(
            lookup,
            speciesFactors,
            spiritSteps);
    }

    private static void ValidateInstalledClientHatchRankCompatibility(
        PetHatchRankStepContentDefinition[] steps)
    {
        // Runtime rolls consume the pinned database revision. This sentinel
        // prevents an accidentally published revision from changing the
        // explicitly approved launch brackets or their 60/30/10 weights.
        if (steps.Length != 16 *
                PetHatchRankContentPolicy.OutcomesPerAptitude)
        {
            throw new InvalidOperationException(
                "Published pet hatch ranks diverge from the approved baseline.");
        }

        for (short aptitude = 1; aptitude <= 16; aptitude++)
        {
            decimal[] expectedRanks = aptitude switch
            {
                <= 2 => [0m, 0.30m, 0.40m],
                <= 4 => [0.30m, 0.40m, 0.80m],
                <= 6 => [0.40m, 0.80m, 1.00m],
                <= 8 => [0.80m, 1.00m, 1.50m],
                <= 10 => [1.00m, 1.50m, 2.00m],
                <= 12 => [1.50m, 2.00m, 2.70m],
                <= 14 => [2.00m, 2.70m, 3.00m],
                _ => [2.70m, 3.00m, 3.60m]
            };
            for (short outcome = 0;
                 outcome < PetHatchRankContentPolicy.OutcomesPerAptitude;
                 outcome++)
            {
                var step = steps[
                    (aptitude - 1) *
                        PetHatchRankContentPolicy.OutcomesPerAptitude +
                    outcome];
                var expectedWeight = outcome switch
                {
                    0 => 60,
                    1 => 30,
                    _ => 10
                };
                if (step.Aptitude != aptitude ||
                    step.OutcomeOrder != outcome ||
                    step.Rank != expectedRanks[outcome] ||
                    step.Weight != expectedWeight)
                {
                    throw new InvalidOperationException(
                        "Published pet hatch ranks diverge from the " +
                        "approved 60/30/10 baseline.");
                }
            }
        }
    }

    private static void ValidateInstalledClientMergeRankCompatibility(
        PetMergeRankLookupContentDefinition[] lookup,
        PetMergeRankSpeciesFactorContentDefinition[] speciesFactors,
        PetMergeRankSpiritStepContentDefinition[] spiritSteps)
    {
        // Runtime rolls still consume the pinned database revision. This
        // compiled sentinel only prevents an unreviewed revision from
        // changing values that the installed client's preview hard-codes.
        for (var index = 0; index < 200; index++)
        {
            var order = index + 1;
            var expectedThreshold = order switch
            {
                <= 49 => checked(-3000 + (order - 1) * 12),
                <= 100 => checked(-2400 + (order - 50) * 12),
                <= 175 => checked(-1800 + (order - 100) * 24),
                _ => checked((order - 175) * 20)
            };
            var expectedBaseIncrease = checked((ushort)(order <= 100
                ? order
                : order * 2 - 100));
            if (lookup[index].MinimumRankDifference != expectedThreshold ||
                lookup[index].BaseIncrease != expectedBaseIncrease)
            {
                throw new InvalidOperationException(
                    "Published pet Merge rank lookup diverges from the " +
                    "reviewed installed-client baseline.");
            }
        }

        if (speciesFactors.Length != 45)
        {
            throw new InvalidOperationException(
                "Published pet Merge rank species factors diverge from the " +
                "reviewed installed-client baseline.");
        }
        for (var index = 0; index < speciesFactors.Length; index++)
        {
            var speciesId = checked((short)(index + 1));
            var expectedFactor = speciesId switch
            {
                2 or 3 or 6 or 10 => 0.8m,
                1 or 7 => 1.4m,
                _ => 2.6m
            };
            if (speciesFactors[index].SpeciesId != speciesId ||
                speciesFactors[index].Factor != expectedFactor)
            {
                throw new InvalidOperationException(
                    "Published pet Merge rank species factors diverge from " +
                    "the reviewed installed-client baseline.");
            }
        }

        if (spiritSteps.Length != 6)
        {
            throw new InvalidOperationException(
                "Published pet Merge rank spirit steps diverge from the " +
                "reviewed installed-client baseline.");
        }
        for (var index = 0; index < spiritSteps.Length; index++)
        {
            var spiritCount = checked((short)index);
            if (spiritSteps[index] !=
                new PetMergeRankSpiritStepContentDefinition(
                    spiritCount,
                    checked((short)(spiritCount * 10)),
                    100))
            {
                throw new InvalidOperationException(
                    "Published pet Merge rank spirit steps diverge from the " +
                    "reviewed installed-client baseline.");
            }
        }
    }
}
