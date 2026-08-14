using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetSystemFoundationChecks
{
    private static void CheckRebirth()
    {
        var content = PetContentTestCatalog.Instance;
        var maximumLevelCases = new[]
        {
            (Completed: 0, Rebirth: 1, Gate: 30, Surplus: 242_980_800L),
            (Completed: 1, Rebirth: 2, Gate: 80, Surplus: 156_880_350L),
            (Completed: 2, Rebirth: 3, Gate: 100, Surplus: 93_759_075L),
            (Completed: 3, Rebirth: 4, Gate: 110, Surplus: 51_664_650L),
            (Completed: 4, Rebirth: 5, Gate: 120, Surplus: 0L),
            (Completed: 5, Rebirth: 6, Gate: 120, Surplus: 0L),
            (Completed: 99, Rebirth: 100, Gate: 120, Surplus: 0L)
        };
        foreach (var value in maximumLevelCases)
        {
            Check.True(
                content.TryGetRebirthStep(value.Rebirth, out var step) &&
                step.RequiredPetLevel == value.Gate,
                $"rebirth {value.Rebirth} publishes its active level gate");
            Check.Equal(
                value.Gate,
                PetManagerPlanner.RequiredLevelForRebirth(
                    content,
                    value.Completed),
                $"rebirth {value.Rebirth} plans against its active step");
            Check.True(
                PetRebirthExperiencePolicy.TryCalculateCarry(
                    content,
                    petLevel: 120,
                    requiredLevel: value.Gate,
                    currentExperience: 12_345,
                    out var carry) &&
                carry.HistoricalSurplusExperience == value.Surplus &&
                carry.PreRebirthUnspentExperience == 12_345 &&
                carry.TotalExperience == value.Surplus + 12_345,
                $"rebirth {value.Rebirth} carries surplus and unspent EXP");
            Check.True(
                PetRebirthExperiencePolicy.TryCalculateCarry(
                    content,
                    petLevel: value.Gate,
                    requiredLevel: value.Gate,
                    currentExperience: 12_345,
                    out carry) &&
                carry.HistoricalSurplusExperience == 0 &&
                carry.TotalExperience == 12_345,
                $"rebirth {value.Rebirth} preserves EXP at its exact gate");
        }

        var oneLevelSurplusCases = new[]
        {
            (Gate: 30, Refund: 933_675L),
            (Gate: 80, Refund: 2_632_050L),
            (Gate: 100, Refund: 3_838_650L),
            (Gate: 110, Refund: 4_699_650L)
        };
        foreach (var value in oneLevelSurplusCases)
        {
            Check.True(
                PetRebirthExperiencePolicy.TryCalculateCarry(
                    content,
                    petLevel: value.Gate + 1,
                    requiredLevel: value.Gate,
                    currentExperience: 0,
                    out var carry) &&
                carry.TotalExperience == value.Refund,
                $"one level above gate {value.Gate} refunds that transition");
        }

        const long firstSurplus = 242_980_800L;
        Check.True(
            PetRebirthExperiencePolicy.TryCalculateCarry(
                content,
                petLevel: 120,
                requiredLevel: 30,
                currentExperience:
                    PetExperienceItemPolicy.MaximumNativePetExperience -
                    firstSurplus,
                out var maximumCarry) &&
            maximumCarry.TotalExperience ==
                PetExperienceItemPolicy.MaximumNativePetExperience,
            "rebirth permits the exact native EXP ceiling");
        Check.True(
            !PetRebirthExperiencePolicy.TryCalculateCarry(
                content,
                petLevel: 120,
                requiredLevel: 30,
                currentExperience:
                    PetExperienceItemPolicy.MaximumNativePetExperience -
                    firstSurplus + 1,
                out _),
            "rebirth rejects carry beyond the native EXP ceiling");

        var initial = new PetSavvy(50m, 51m, 52m, 53m, 54m, 55m);
        var baseGrowth =
            new PetSavvy(0.01m, 0.02m, 0.03m, 0.04m, 0.05m, 0.06m);
        var pet = CreatePet() with
        {
            Level = 80,
            CompletedRebirths = 1,
            RebirthsRemaining = 3,
            HasSoulContract = true,
            IsSummoned = true,
            Rank = 20m,
            InitialSavvy = initial,
            AddedSavvy =
                PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                    80,
                    baseGrowth,
                    PetSavvy.Zero),
            BaseGrowthRates = baseGrowth,
            RarityAddedSavvy =
                new PetSavvy(40m, 41m, 42m, 43m, 44m, 45m)
        };
        var acceleration =
            new PetSavvy(0.15m, 0.15m, 0.15m, 0.15m, 0.15m, 0.15m);
        var outcome = new AuthoritativePetRebirthOutcome(
            CarriedExperience: 1234,
            RankAfter: 22m,
            acceleration);

        Check.True(
            PetManagerPlanner.TryPlanRebirth(
                content,
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome with
                {
                    CarriedExperience =
                        PetExperienceItemPolicy.MaximumNativePetExperience
                },
                out _,
                out _) &&
            !PetManagerPlanner.TryPlanRebirth(
                content,
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome with
                {
                    CarriedExperience =
                        PetExperienceItemPolicy.MaximumNativePetExperience + 1
                },
                out _,
                out var overflowRejection) &&
            overflowRejection ==
                PetPlanRejection.InvalidAuthoritativeOutcome,
            "rebirth planner accepts the native EXP ceiling and rejects overflow");

        Check.True(
            PetManagerPlanner.TryPlanRebirth(
                PetContentTestCatalog.Instance,
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(
                    RebirthSpiritCount: 5,
                    RebornHarpyiaCount: 0),
                outcome,
                out var plan,
                out var rejection),
            "eligible pet rebirth is planned");
        Check.True(rejection == PetPlanRejection.None, "rebirth accepted");
        Check.Equal(1, plan!.PetAfter.Level, "rebirth returns pet to level one");
        Check.Equal(
            1234L,
            plan.PetAfter.Experience,
            "server-authored carried EXP is applied");
        Check.Equal(
            initial,
            plan.PetAfter.InitialSavvy,
            "rebirth preserves initial savvy");
        Check.Equal(
            PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                1,
                pet.BaseGrowthRates,
                acceleration),
            plan.PetAfter.AddedSavvy,
            "rebirth materializes accelerated level-one Added values");
        Check.Equal(
            acceleration,
            plan.PetAfter.GrowthAcceleration,
            "server-authored growth is applied");
        Check.Equal(
            2,
            plan.PetAfter.CompletedRebirths,
            "rebirth count advances");
        Check.Equal(
            2,
            plan.PetAfter.RebirthsRemaining,
            "one rebirth chance is consumed");

        Check.True(
            !PetManagerPlanner.TryPlanRebirth(
                PetContentTestCatalog.Instance,
                pet with { HasSoulContract = false },
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome,
                out _,
                out rejection),
            "rebirth requires the stock Soul Contract prerequisite");
        Check.True(
            rejection == PetPlanRejection.SoulContractRequired,
            "missing Soul Contract has a specific rejection");

        Check.True(
            !PetManagerPlanner.TryPlanRebirth(
                PetContentTestCatalog.Instance,
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(0, 1),
                outcome,
                out _,
                out rejection),
            "restricted Reborn Harpyia cannot be used on an unbound pet");
        Check.True(
            rejection ==
                PetPlanRejection.RestrictedMaterialRequiresBoundPet,
            "restricted rebirth material has a specific rejection");

        Check.True(
            PetManagerPlanner.TryPlanRebirth(
                PetContentTestCatalog.Instance,
                pet with { IsBound = true },
                pet.OwnerCharacterId,
                new PetRebirthMaterials(0, 5),
                outcome,
                out var boundPlan,
                out rejection),
            "a bound pet may use the restricted Reborn Harpyia variant");
        Check.Equal(
            5,
            boundPlan!.Materials.RebornHarpyiaCount,
            "restricted rebirth materials remain in the transaction plan");

        var zeroOutcome = outcome with
        {
            GrowthAcceleration = new PetSavvy(
                0.01m, 0.02m, 0.03m, 0.04m, 0.05m, 0.20m)
        };
        Check.True(
            PetManagerPlanner.TryPlanRebirth(
                PetContentTestCatalog.Instance,
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(0, 0),
                zeroOutcome,
                out var zeroPlan,
                out rejection) &&
            zeroPlan!.Materials == new PetRebirthMaterials(0, 0),
            "zero-spirit rebirth is eligible without inventory consumption");
    }
}
