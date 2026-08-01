using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSavvyRuntimeSafetyChecks
{
    public static Task RunAsync()
    {
        CheckStartupValidationPredicates();
        CheckRebirthRequiresRarityBaseline();
        return Task.CompletedTask;
    }

    private static void CheckStartupValidationPredicates()
    {
        var sql = PostgresGameStore.PetSavvyBaselineValidationSql;
        var normalizedSql = string.Join(
            ' ',
            sql.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        Check.True(
            normalizedSql.Contains(
                "pet.rarity_added_savvy_baseline_total IS NULL",
                StringComparison.Ordinal),
            "startup validation rejects absent rarity-savvy totals");
        Check.True(
            normalizedSql.Contains(
                "pet.rarity_added_savvy_policy_version " +
                "IS DISTINCT FROM @addedPolicyVersion",
                StringComparison.Ordinal),
            "startup validation rejects NULL added-savvy provenance");
        Check.True(
            normalizedSql.Contains(
                "pet.initial_savvy_source_version " +
                "IS DISTINCT FROM @initialSourceVersion",
                StringComparison.Ordinal),
            "startup validation rejects NULL initial-savvy provenance");
        Check.True(
            normalizedSql.Contains(
                "stat.birth_initial_savvy " +
                "IS DISTINCT FROM stat.base_growth_rate",
                StringComparison.Ordinal),
            "startup validation requires birth initial savvy to equal base growth");
        Check.True(
            normalizedSql.Contains(
                "WHERE pet.rarity_added_savvy_baseline_total IS NOT NULL " +
                "OR pet.rarity_added_savvy_policy_version IS NOT NULL " +
                "OR pet.initial_savvy_source_version IS NOT NULL GROUP BY",
                StringComparison.Ordinal),
            "all-null legacy provenance remains outside startup validation");
        Check.True(
            normalizedSql.Contains(
                "HAVING count(stat.stat_code) <> 6 " +
                "OR count(DISTINCT stat.stat_code) <> 6 " +
                "OR pet.rarity_added_savvy_baseline_total IS NULL",
                StringComparison.Ordinal),
            "partial provenance enters validation and fails on a missing baseline");
        Check.True(
            normalizedSql.Contains(
                "count(DISTINCT stat.rarity_added_savvy) < 2",
                StringComparison.Ordinal),
            "startup validation rejects an all-equal rarity distribution");
    }

    private static void CheckRebirthRequiresRarityBaseline()
    {
        var rarityBaseline = PetAddedSavvyPolicy.Distribute(
            PetAptitude.Weak,
            totalSavvy: 300,
            new Random(7)).AddedSavvy;
        var pet = CreateEligiblePet(
            addedSavvy: rarityBaseline +
                new PetSavvy(1m, 2m, 3m, 4m, 5m, 6m),
            rarityBaseline: PetSavvy.Zero);
        var outcome = new AuthoritativePetRebirthOutcome(
            CarriedExperience: 0,
            RankAfter: pet.Rank,
            GrowthAcceleration:
                new PetSavvy(
                    0.15m,
                    0.15m,
                    0.15m,
                    0.15m,
                    0.15m,
                    0.15m));

        Check.True(
            !PetManagerPlanner.TryPlanRebirth(PetContentTestCatalog.Instance,
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome,
                out var missingPlan,
                out var rejection),
            "rebirth rejects an absent rarity-added-savvy baseline");
        Check.True(
            missingPlan is null &&
            rejection == PetPlanRejection.InvalidPetState,
            "absent rarity baseline has a safe model-state rejection");

        var partialBaseline =
            new PetSavvy(0m, 50m, 50m, 50m, 50m, 50m);
        Check.True(
            !PetManagerPlanner.TryPlanRebirth(PetContentTestCatalog.Instance,
                pet with
                {
                    AddedSavvy = partialBaseline,
                    RarityAddedSavvy = partialBaseline
                },
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome,
                out _,
                out rejection) &&
            rejection == PetPlanRejection.InvalidPetState,
            "rebirth rejects a zero per-stat rarity baseline");

        var allEqualBaseline =
            new PetSavvy(50m, 50m, 50m, 50m, 50m, 50m);
        Check.True(
            !PetManagerPlanner.TryPlanRebirth(PetContentTestCatalog.Instance,
                pet with
                {
                    AddedSavvy = allEqualBaseline,
                    RarityAddedSavvy = allEqualBaseline
                },
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome,
                out _,
                out rejection) &&
            rejection == PetPlanRejection.InvalidPetState,
            "rebirth rejects an all-equal rarity baseline");

        var validPet = pet with
        {
            RarityAddedSavvy = rarityBaseline
        };
        Check.True(
            PetManagerPlanner.TryPlanRebirth(PetContentTestCatalog.Instance,
                validPet,
                validPet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome,
                out var plan,
                out rejection),
            "rebirth accepts a complete authoritative rarity baseline");
        Check.True(
            rejection == PetPlanRejection.None &&
            plan!.PetAfter.AddedSavvy == rarityBaseline,
            "rebirth restores the immutable rarity baseline without erasing it");
    }

    private static OwnedPet CreateEligiblePet(
        PetSavvy addedSavvy,
        PetSavvy rarityBaseline) =>
        new(
            PetId: 1,
            OwnerCharacterId: 7,
            SpeciesType: 1,
            Name: "Savvy Safety Pet",
            Level: 50,
            Experience: 0,
            Rank: 1m,
            Aptitude: PetAptitude.Weak,
            InitialSavvy:
                new PetSavvy(1m, 1m, 1m, 1m, 1m, 1m),
            AddedSavvy: addedSavvy,
            BaseGrowthRates:
                new PetSavvy(1m, 1m, 1m, 1m, 1m, 1m),
            GrowthAcceleration: PetSavvy.Zero,
            CompletedPetMerges: 0,
            CompletedRebirths: 0,
            RebirthsRemaining: 1,
            HasSoulContract: true,
            HasOwnerMergeTalent: false,
            IsBound: false,
            IsSummoned: true,
            IsAway: false,
            CurrentEnergy: 100,
            MaximumEnergy: 100,
            Amity: 100,
            OwnerMerge: null,
            RarityAddedSavvy: rarityBaseline);
}
