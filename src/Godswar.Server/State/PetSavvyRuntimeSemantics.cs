using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal static class PetSavvyRuntimeSemantics
{
    public const string SourceVersion =
        PetSavvyPersistenceContract.SourceVersion;
    public const string LegacyHighSavvyPolicyVersion =
        "legacy-high-savvy-range-v1";

    public static void ValidateProjectionSourceVersion(string? sourceVersion)
    {
        if (sourceVersion is not null &&
            !string.Equals(
                sourceVersion,
                SourceVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported pet Savvy source version '{sourceVersion}'.");
        }
    }

    public static void ValidateProjectionProvenance(
        PetBootstrapSnapshot pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        ValidateProjectionSourceVersion(pet.InitialSavvySourceVersion);

        var hasChildProvenance = pet.StatValues.Any(static stat =>
            stat.BirthInitialSavvy is not null ||
            stat.RarityAddedSavvy is not null);
        if (pet.InitialSavvySourceVersion is null)
        {
            if (hasChildProvenance)
            {
                throw new InvalidDataException(
                    "Legacy pet projection has partial Savvy provenance.");
            }
            return;
        }

        var ordered = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .ToArray();
        if (ordered.Length != 6 ||
            ordered.Where((stat, index) =>
                stat.StatCode != index + 1).Any())
        {
            throw new InvalidDataException(
                "Scaled-Added pet projection requires six ordered stats.");
        }

        decimal birthTotal = 0m;
        decimal currentBasicTotal = 0m;
        foreach (var stat in ordered)
        {
            if (stat.BirthInitialSavvy is not { } birth || birth <= 0m ||
                stat.RarityAddedSavvy is not { } rarity || rarity <= 0m ||
                birth != rarity ||
                stat.InitialSavvy <= 0m ||
                stat.BaseGrowthRate <= 0m ||
                stat.GrowthAcceleration < 0m)
            {
                throw new InvalidDataException(
                    "Scaled-Added pet projection has invalid Savvy provenance.");
            }

            _ = ResolveNativeAdded(
                pet.Level,
                stat.AddedSavvy,
                stat.BaseGrowthRate,
                stat.GrowthAcceleration,
                rarity);
            birthTotal += birth;
            currentBasicTotal += stat.InitialSavvy;
        }

        // Fairy's Feather redistributes the complete Basic pool. Individual
        // stats may therefore fall below their immutable hatch allocations;
        // only the aggregate progression floor is authoritative.
        if (currentBasicTotal < birthTotal)
        {
            throw new InvalidDataException(
                "Scaled-Added pet projection lost aggregate Basic Savvy.");
        }
    }

    /// <summary>
    /// The stock client labels its first Savvy column as Basic. The current
    /// value lives in initial_savvy; it starts from the hatch allocation and
    /// advances only through pet-to-pet Merge progression. Pet levels never
    /// change Basic.
    /// </summary>
    public static decimal ResolveNativeBasic(
        decimal initialSavvy,
        decimal? rarityAddedSavvy) =>
        initialSavvy;

    /// <summary>
    /// The stock client labels its second Savvy column as Added. It is the
    /// pet's current level-scaled value, not the concealed per-level Growth
    /// Rate itself. Rebirth acceleration participates in the effective rate.
    /// Rows without migrated rarity provenance retain their captured legacy
    /// value.
    /// </summary>
    public static decimal ResolveNativeAdded(
        int petLevel,
        decimal materializedAddedSavvy,
        decimal baseGrowthRate,
        decimal growthAcceleration,
        decimal? rarityAddedSavvy) =>
        rarityAddedSavvy is null
            ? materializedAddedSavvy
            : rarityAddedSavvy <= 0m
                ? throw new InvalidDataException(
                    "Scaled Added provenance must be positive.")
                : materializedAddedSavvy == ResolveLevelScaledAdded(
                    petLevel,
                    baseGrowthRate,
                    growthAcceleration)
                    ? materializedAddedSavvy
                    : throw new InvalidDataException(
                        "Materialized pet Added does not match Growth and level.");

    public static decimal ResolveLevelScaledAdded(
        int petLevel,
        decimal baseGrowthRate,
        decimal growthAcceleration)
    {
        return PetSavvyPersistenceContract.ResolveAdded(
            petLevel,
            baseGrowthRate,
            growthAcceleration);
    }

    public static PetSavvy ResolveLevelScaledAdded(
        int petLevel,
        PetSavvy baseGrowthRate,
        PetSavvy growthAcceleration)
    {
        if (!baseGrowthRate.IsNonNegative ||
            !growthAcceleration.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseGrowthRate),
                "Pet Growth values cannot be negative.");
        }

        return new PetSavvy(
            ResolveLevelScaledAdded(
                petLevel,
                baseGrowthRate.Agility,
                growthAcceleration.Agility),
            ResolveLevelScaledAdded(
                petLevel,
                baseGrowthRate.Strength,
                growthAcceleration.Strength),
            ResolveLevelScaledAdded(
                petLevel,
                baseGrowthRate.Accuracy,
                growthAcceleration.Accuracy),
            ResolveLevelScaledAdded(
                petLevel,
                baseGrowthRate.Technique,
                growthAcceleration.Technique),
            ResolveLevelScaledAdded(
                petLevel,
                baseGrowthRate.Wisdom,
                growthAcceleration.Wisdom),
            ResolveLevelScaledAdded(
                petLevel,
                baseGrowthRate.Luck,
                growthAcceleration.Luck));
    }

    public static PetSavvy ResolveMaterializedAdded(
        int petLevel,
        PetSavvy materializedAddedSavvy,
        PetSavvy baseGrowthRate,
        PetSavvy growthAcceleration,
        PetSavvy? rarityAddedSavvy)
    {
        if (!materializedAddedSavvy.IsNonNegative)
        {
            throw new InvalidDataException(
                "Pet Added values cannot be negative.");
        }
        if (rarityAddedSavvy is null)
        {
            return materializedAddedSavvy;
        }
        if (!HasStrictlyPositiveValues(rarityAddedSavvy.Value))
        {
            throw new InvalidDataException(
                "Scaled Added provenance must contain six positive values.");
        }

        var expected = ResolveLevelScaledAdded(
            petLevel,
            baseGrowthRate,
            growthAcceleration);
        return materializedAddedSavvy == expected
            ? materializedAddedSavvy
            : throw new InvalidDataException(
                "Materialized pet Added does not match Growth and level.");
    }

    public static PetSavvy ResolvePlayerVisibleTotal(
        int petLevel,
        PetSavvy initialSavvy,
        PetSavvy materializedAddedSavvy,
        PetSavvy baseGrowthRate,
        PetSavvy growthAcceleration,
        PetSavvy? rarityAddedSavvy)
    {
        if (!initialSavvy.IsNonNegative ||
            !materializedAddedSavvy.IsNonNegative)
        {
            throw new InvalidDataException(
                "Pet Savvy values cannot be negative.");
        }

        if (rarityAddedSavvy is { } rarity &&
            !HasStrictlyPositiveValues(rarity))
        {
            throw new InvalidDataException(
                "Scaled-Added provenance must contain six positive values.");
        }

        var added = ResolveMaterializedAdded(
            petLevel,
            materializedAddedSavvy,
            baseGrowthRate,
            growthAcceleration,
            rarityAddedSavvy);
        return initialSavvy + added;
    }

    public static PetSavvy ResolvePlayerVisibleTotal(
        int petLevel,
        PetSavvy initialSavvy,
        PetSavvy materializedAddedSavvy,
        PetSavvy baseGrowthRate,
        PetSavvy growthAcceleration,
        PetSavvy rarityAddedSavvy) =>
        ResolvePlayerVisibleTotal(
            petLevel,
            initialSavvy,
            materializedAddedSavvy,
            baseGrowthRate,
            growthAcceleration,
            rarityAddedSavvy == PetSavvy.Zero
                ? null
                : rarityAddedSavvy);

    public static bool HasStrictlyPositiveValues(PetSavvy value) =>
        value.Agility > 0m &&
        value.Strength > 0m &&
        value.Accuracy > 0m &&
        value.Technique > 0m &&
        value.Wisdom > 0m &&
        value.Luck > 0m;
}
