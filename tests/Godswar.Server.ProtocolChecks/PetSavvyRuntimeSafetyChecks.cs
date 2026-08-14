using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSavvyRuntimeSafetyChecks
{
    public static Task RunAsync()
    {
        CheckStartupValidationPredicates();
        CheckRebirthRequiresRarityBaseline();
        CheckProgressionRefreshRejectsStaleAdded();
        CheckRedistributedBasicProjection();
        return Task.CompletedTask;
    }

    private static void CheckRedistributedBasicProjection()
    {
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            totalSavvy: 3_500,
            new Random(3_500));
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            totalGrowth: 50m,
            new Random(50));
        var hatched = PetEggHatchProtocolChecks.CreatePet(savvy, growth);
        var redistributed = new[]
        {
            3_000m, 100m, 100m, 100m, 100m, 100m
        };
        var birth = new[]
        {
            savvy.InitialSavvy.Agility,
            savvy.InitialSavvy.Strength,
            savvy.InitialSavvy.Accuracy,
            savvy.InitialSavvy.Technique,
            savvy.InitialSavvy.Wisdom,
            savvy.InitialSavvy.Luck
        };
        var pet = hatched with
        {
            InitialSavvySourceVersion =
                PetSavvyRuntimeSemantics.SourceVersion,
            StatValues = hatched.StatValues
                .OrderBy(static stat => stat.StatCode)
                .Select((stat, index) => stat with
                {
                    InitialSavvy = redistributed[index],
                    BirthInitialSavvy = birth[index],
                    RarityAddedSavvy = birth[index]
                })
                .ToArray()
        };

        PetSavvyRuntimeSemantics.ValidateProjectionProvenance(pet);
        _ = GameClientHandler.ResolvePetProgressionAdded(pet);
        var ownedPetList = PacketBuilder.OwnedPetList(
            PetContentTestCatalog.Instance,
            [pet],
            openedCellCount: 2);
        Check.True(
            ownedPetList.Length > 8,
            "opcode 10237 accepts aggregate-valid redistributed Basic Savvy");

        var invalid = pet with
        {
            StatValues = pet.StatValues.Select(static stat => stat with
            {
                InitialSavvy = 1m
            }).ToArray()
        };
        Check.Throws<InvalidDataException>(
            () => PetSavvyRuntimeSemantics
                .ValidateProjectionProvenance(invalid),
            "projection rejects a redistributed Basic pool below birth total");
        Check.Throws<InvalidDataException>(
            () => PacketBuilder.OwnedPetList(
                PetContentTestCatalog.Instance,
                [invalid],
                openedCellCount: 2),
            "opcode 10237 rejects a redistributed Basic pool below birth total");
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
                "pet.initial_savvy_policy_version " +
                "IS DISTINCT FROM @initialPolicyVersion AND " +
                "pet.initial_savvy_policy_version IS DISTINCT FROM " +
                "@migratedLegacySavvyPolicyVersion",
                StringComparison.Ordinal),
            "startup validation admits only new-hatch and migrated Savvy policies");
        Check.True(
            normalizedSql.Contains(
                "pet.rarity_added_savvy_policy_version " +
                "IS DISTINCT FROM @initialPolicyVersion AND " +
                "pet.rarity_added_savvy_policy_version IS DISTINCT FROM " +
                "@migratedLegacySavvyPolicyVersion",
                StringComparison.Ordinal),
            "the compatibility Savvy mirror supports the same two policies");
        Check.True(
            normalizedSql.Contains(
                "pet.initial_savvy_policy_version IS DISTINCT FROM " +
                "pet.rarity_added_savvy_policy_version",
                StringComparison.Ordinal),
            "startup validation rejects mixed Savvy-policy mirrors");
        Check.True(
            normalizedSql.Contains(
                "pet.initial_savvy_source_version " +
                "IS DISTINCT FROM @initialSourceVersion",
                StringComparison.Ordinal),
            "startup validation rejects NULL initial-savvy provenance");
        Check.True(
            normalizedSql.Contains(
                "stat.birth_initial_savvy " +
                "IS DISTINCT FROM stat.rarity_added_savvy",
                StringComparison.Ordinal),
            "startup validation requires the immutable Savvy mirrors to agree");
        Check.True(
            normalizedSql.Contains(
                "WHERE pet.initial_savvy_baseline_total IS NOT NULL " +
                "OR pet.initial_savvy_policy_version IS NOT NULL " +
                "OR pet.rarity_added_savvy_baseline_total IS NOT NULL " +
                "OR pet.rarity_added_savvy_policy_version IS NOT NULL " +
                "OR pet.initial_savvy_source_version IS NOT NULL GROUP BY",
                StringComparison.Ordinal),
            "all-null legacy provenance remains outside startup validation");
        Check.True(
            normalizedSql.Contains(
                "HAVING count(stat.stat_code) <> 6 " +
                "OR count(DISTINCT stat.stat_code) <> 6 " +
                "OR pet.initial_savvy_baseline_total IS NULL",
                StringComparison.Ordinal),
            "partial provenance enters validation and fails on a missing baseline");
        Check.True(
            normalizedSql.Contains(
                "stat.added_savvy IS DISTINCT FROM " +
                "( stat.base_growth_rate + stat.growth_acceleration ) * " +
                "pet.level",
                StringComparison.Ordinal),
            "startup validation requires exact level-scaled Added values");
        Check.True(
            normalizedSql.Contains(
                "COALESCE(sum(stat.birth_initial_savvy), 0) <> " +
                "pet.initial_savvy_baseline_total",
                StringComparison.Ordinal) &&
            normalizedSql.Contains(
                "COALESCE(sum(stat.initial_savvy), 0) < " +
                "pet.initial_savvy_baseline_total",
                StringComparison.Ordinal) &&
            !normalizedSql.Contains(
                "stat.initial_savvy < stat.birth_initial_savvy",
                StringComparison.Ordinal),
            "startup validation accepts redistributed per-stat Basic values while preserving the aggregate hatch baseline");

        var initialSql = NormalizeSql(
            PostgresGameStore.PetInitialSavvyStateValidationSql);
        Check.True(
            initialSql.Contains(
                "pet.initial_savvy_policy_version " +
                "IS DISTINCT FROM @initialPolicyVersion AND " +
                "pet.initial_savvy_policy_version IS DISTINCT FROM " +
                "@migratedLegacySavvyPolicyVersion",
                StringComparison.Ordinal),
            "initial-Savvy validation preserves truthful migrated provenance");
        Check.True(
            PetContentTestCatalog.Instance.Settings.InitialSavvyPolicyVersion !=
                PetContentTestCatalog.Instance.Settings.AddedSavvyPolicyVersion &&
            PetSavvyRuntimeSemantics.SourceVersion ==
                "basic-plus-scaled-growth-v3",
            "pinned Savvy policies retain distinct roles under scaled-Added v3 semantics");
    }

    private static string NormalizeSql(string sql) =>
        string.Join(
            ' ',
            sql.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

    private static void CheckRebirthRequiresRarityBaseline()
    {
        var rarityBaseline = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Weak,
            totalSavvy: 30,
            new Random(7)).InitialSavvy;
        var baseGrowth =
            new PetSavvy(0.01m, 0.02m, 0.03m, 0.04m, 0.05m, 0.06m);
        var pet = CreateEligiblePet(
            initialSavvy: rarityBaseline +
                new PetSavvy(1m, 2m, 3m, 4m, 5m, 6m),
            addedSavvy: baseGrowth +
                new PetSavvy(0.1m, 0.2m, 0.3m, 0.4m, 0.5m, 0.6m),
            baseGrowth: baseGrowth,
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
                    InitialSavvy = partialBaseline +
                        new PetSavvy(1m, 1m, 1m, 1m, 1m, 1m),
                    RarityAddedSavvy = partialBaseline
                },
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome,
                out _,
                out rejection) &&
            rejection == PetPlanRejection.InvalidPetState,
            "rebirth rejects a zero per-stat rarity baseline");

        var validPet = pet with
        {
            RarityAddedSavvy = rarityBaseline,
            AddedSavvy =
                PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                    pet.Level,
                    baseGrowth,
                    PetSavvy.Zero)
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
            plan!.PetAfter.AddedSavvy ==
                PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                    1,
                    baseGrowth,
                    outcome.GrowthAcceleration),
            "rebirth materializes level-one Added from accelerated Growth");

        foreach (var invalidRank in new[] { 655.36m, 1.001m })
        {
            Check.True(
                !PetManagerPlanner.TryPlanRebirth(
                    PetContentTestCatalog.Instance,
                    validPet with { Rank = invalidRank },
                    validPet.OwnerCharacterId,
                    new PetRebirthMaterials(5, 0),
                    outcome,
                    out _,
                    out rejection) &&
                rejection == PetPlanRejection.InvalidPetState,
                $"pet planner rejects native-wire-unsafe rank {invalidRank}");
        }
    }

    private static void CheckProgressionRefreshRejectsStaleAdded()
    {
        var savvy = PetInitialSavvyPolicy.Distribute(
            PetAptitude.Godly,
            totalSavvy: 3_500,
            new Random(73));
        var growth = PetGrowthPolicy.Distribute(
            PetAptitude.Godly,
            totalGrowth: 50m,
            new Random(74));
        var pet = PetEggHatchProtocolChecks.CreatePet(savvy, growth) with
        {
            Level = 30,
            InitialSavvySourceVersion =
                PetSavvyRuntimeSemantics.SourceVersion
        };
        pet = pet with
        {
            StatValues = pet.StatValues
                .Select(stat => stat with
                {
                    AddedSavvy =
                        PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                            pet.Level,
                            stat.BaseGrowthRate,
                            stat.GrowthAcceleration),
                    BirthInitialSavvy = stat.InitialSavvy,
                    RarityAddedSavvy = stat.InitialSavvy
                })
                .ToArray()
        };
        var added = GameClientHandler.ResolvePetProgressionAdded(pet);
        Check.Equal(
            pet.StatValues[0].AddedSavvy,
            added.Agility,
            "10286 uses the persisted level-scaled Added value");

        var stale = pet with
        {
            StatValues = pet.StatValues
                .Select(stat => stat.StatCode == 1
                    ? stat with
                    {
                        AddedSavvy = stat.AddedSavvy + 0.01m
                    }
                    : stat)
                .ToArray()
        };
        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ResolvePetProgressionAdded(stale),
            "10286 rejects stale scaled-Added materialization");
        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ResolvePetProgressionAdded(
                pet with
                {
                    InitialSavvySourceVersion = "savvy-plus-growth-v2"
                }),
            "10286 rejects obsolete Savvy provenance");
    }

    private static OwnedPet CreateEligiblePet(
        PetSavvy initialSavvy,
        PetSavvy addedSavvy,
        PetSavvy baseGrowth,
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
            InitialSavvy: initialSavvy,
            AddedSavvy: addedSavvy,
            BaseGrowthRates: baseGrowth,
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
