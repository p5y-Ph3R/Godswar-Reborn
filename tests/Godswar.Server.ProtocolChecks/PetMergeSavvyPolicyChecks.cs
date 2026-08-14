using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetMergeSavvyPolicyChecks
{
    private static readonly PetSavvy ScreenshotPrimary = new(
        122.89m,
        122.89m,
        128.98m,
        200.00m,
        200.00m,
        131.62m);

    private static readonly PetSavvy ScreenshotDeputyBasic = new(
        44.03m,
        44.03m,
        62.98m,
        1.00m,
        1.00m,
        35.79m);

    private static readonly PetSavvy ScreenshotDeputyAdded = new(
        447.44m,
        447.44m,
        447.44m,
        447.44m,
        447.44m,
        447.44m);

    public static Task RunAsync()
    {
        CheckHistoricalScreenshotWithoutSpirits();
        CheckFiveSpiritMinimumsAndIndependentRolls();
        CheckZeroMaximumRowsAndValidation();
        CheckPlannerEnforcesPerStatBounds();
        CheckPlannerAcceptsAllZeroOutcome();
        CheckSoulContractDoesNotAffectPetMerge();
        return Task.CompletedTask;
    }

    private static void CheckHistoricalScreenshotWithoutSpirits()
    {
        var random = new SequenceRandom(1, 780, 400, 421);
        Check.True(
            PetMergeSavvyPolicy.TryRollGains(
                PetContentTestCatalog.Instance,
                ScreenshotPrimary,
                ScreenshotDeputyBasic,
                ScreenshotDeputyAdded,
                deputySpeciesId: 4,
                spiritCount: 0,
                random,
                out var evidence,
                out var gains),
            "historical zero-spirit screenshot inputs resolve");
        Check.Equal(
            new PetSavvy(0.01m, 7.80m, 4.00m, 0m, 0m, 4.21m),
            gains,
            "each eligible screenshot stat rolls in its own displayed range");
        Check.Equal(4, random.CallCount,
            "only non-fixed eligible rows consume random draws");
        Check.True(
            evidence.SpeciesFactor == 2.6m &&
            evidence.SpiritCount == 0 &&
            evidence.Stats.Count == 6,
            "screenshot evidence pins factor, material count, and six rows");

        var agility = evidence.Stats[0];
        Check.True(
            agility.AddedContributionHundredths == 8_948 &&
            agility.SavvyDifferenceHundredths == 1_062 &&
            agility.LookupBaseIncrease == 300 &&
            agility.MinimumIncreaseHundredths == 1 &&
            agility.MaximumIncreaseHundredths == 780,
            "Agility reproduces the historical 0.01-7.80 preview");
        var luck = evidence.Stats[5];
        Check.True(
            luck.SavvyDifferenceHundredths == -635 &&
            luck.LookupMinimumSavvyDifference == -656 &&
            luck.LookupBaseIncrease == 162 &&
            luck.MinimumIncreaseHundredths == 1 &&
            luck.MaximumIncreaseHundredths == 421,
            "Luck reproduces the historical 0.01-4.21 preview");
        Check.True(
            evidence.Stats[3].LookupBaseIncrease is null &&
            evidence.Stats[3].MinimumIncreaseHundredths == 0 &&
            evidence.Stats[3].MaximumIncreaseHundredths == 0,
            "a stat below the first Restrict row remains a zero delta");
    }

    private static void CheckFiveSpiritMinimumsAndIndependentRolls()
    {
        var random = new SequenceRandom(390, 780, 500, 211);
        Check.True(
            PetMergeSavvyPolicy.TryRollGains(
                PetContentTestCatalog.Instance,
                ScreenshotPrimary,
                ScreenshotDeputyBasic,
                ScreenshotDeputyAdded,
                deputySpeciesId: 4,
                spiritCount: 5,
                random,
                out var evidence,
                out var gains),
            "five-spirit screenshot inputs resolve");
        Check.Equal(
            new PetSavvy(3.90m, 7.80m, 5.00m, 0m, 0m, 2.11m),
            gains,
            "five spirits roll from the per-stat half-to-maximum ranges");
        Check.Equal(4, random.CallCount,
            "each non-fixed five-spirit row has an independent draw");
        Check.True(
            evidence.MinimumPercent == 50 &&
            evidence.MaximumPercent == 100 &&
            evidence.Stats[0].MinimumIncreaseHundredths == 390 &&
            evidence.Stats[5].MinimumIncreaseHundredths == 211,
            "five spirits use half-up 50-percent minimums");
    }

    private static void CheckZeroMaximumRowsAndValidation()
    {
        var primary = Repeat(40m);
        var deputy = new PetSavvy(0m, 0.10m, 0m, 0m, 0m, 0m);
        Check.True(
            PetMergeSavvyPolicy.TryRollGains(
                PetContentTestCatalog.Instance,
                primary,
                deputy,
                PetSavvy.Zero,
                deputySpeciesId: 2,
                spiritCount: 0,
                new SequenceRandom(),
                out var evidence,
                out var gains),
            "the 0.8 species boundary resolves even when most maxima truncate to zero");
        Check.Equal(
            new PetSavvy(0m, 0.01m, 0m, 0m, 0m, 0m),
            gains,
            "Values 1 truncates to zero while Values 2 truncates to one hundredth");
        Check.True(
            evidence.Stats[0].LookupBaseIncrease == 1 &&
            evidence.Stats[0].MaximumIncreaseHundredths == 0 &&
            evidence.Stats[1].LookupBaseIncrease == 2 &&
            evidence.Stats[1].MaximumIncreaseHundredths == 1,
            "exact-decimal truncation retains the native low-factor eligibility edge");

        var validAfter = primary + gains;
        Check.True(
            PetMergeSavvyPolicy.IsValidOutcome(
                PetContentTestCatalog.Instance,
                primary,
                deputy,
                PetSavvy.Zero,
                deputySpeciesId: 2,
                spiritCount: 0,
                validAfter),
            "validation accepts required zero rows and one eligible increase");
        Check.True(
            !PetMergeSavvyPolicy.IsValidOutcome(
                PetContentTestCatalog.Instance,
                primary,
                deputy,
                PetSavvy.Zero,
                deputySpeciesId: 2,
                spiritCount: 0,
                validAfter with { Strength = 40.02m }),
            "validation rejects a gain above its per-stat maximum");
        Check.True(
            !PetMergeSavvyPolicy.IsValidOutcome(
                PetContentTestCatalog.Instance,
                primary,
                deputy,
                PetSavvy.Zero,
                deputySpeciesId: 2,
                spiritCount: 0,
                validAfter with { Strength = 40.011m }),
            "validation rejects sub-hundredth outcomes");
    }

    private static void CheckPlannerEnforcesPerStatBounds()
    {
        var primary = CreatePet(
            petId: 1,
            ScreenshotPrimary,
            PetSavvy.Zero,
            isSummoned: true);
        var deputy = CreatePet(
            petId: 2,
            ScreenshotDeputyBasic,
            ScreenshotDeputyAdded,
            isSummoned: false);
        var materials = new PetMergeMaterials(5, 0);
        var validGains = new PetSavvy(
            3.90m,
            7.80m,
            5.00m,
            0m,
            0m,
            2.11m);
        var validOutcome = new AuthoritativePetMergeOutcome(
            primary.Rank + 3.25m,
            primary.InitialSavvy + validGains);

        Check.True(
            PetManagerPlanner.TryPlanPetMerge(
                PetContentTestCatalog.Instance,
                primary,
                deputy,
                primary.OwnerCharacterId,
                materials,
                validOutcome,
                out var plan,
                out var rejection),
            "planner accepts independently bounded gains including zero rows");
        Check.True(rejection == PetPlanRejection.None && plan is not null,
            "valid historical merge outcome has no rejection");

        var invalidOutcome = validOutcome with
        {
            InitialSavvyAfter = validOutcome.InitialSavvyAfter with
            {
                Accuracy = primary.InitialSavvy.Accuracy + 7.81m
            }
        };
        Check.True(
            !PetManagerPlanner.TryPlanPetMerge(
                PetContentTestCatalog.Instance,
                primary,
                deputy,
                primary.OwnerCharacterId,
                materials,
                invalidOutcome,
                out _,
                out rejection),
            "planner rejects one gain above its derived maximum");
        Check.True(
            rejection == PetPlanRejection.InvalidAuthoritativeOutcome,
            "per-stat range violation is an authoritative-outcome rejection");
    }

    private static void CheckPlannerAcceptsAllZeroOutcome()
    {
        var primary = CreatePet(
            petId: 3,
            Repeat(40m),
            PetSavvy.Zero,
            isSummoned: true) with
        {
            Rank = 40m
        };
        var deputy = CreatePet(
            petId: 4,
            PetSavvy.Zero,
            PetSavvy.Zero,
            isSummoned: false) with
        {
            SpeciesType = 2,
            Rank = 0m
        };
        var unchanged = new AuthoritativePetMergeOutcome(
            primary.Rank,
            primary.InitialSavvy);

        Check.True(
            PetManagerPlanner.TryPlanPetMerge(
                PetContentTestCatalog.Instance,
                primary,
                deputy,
                primary.OwnerCharacterId,
                new PetMergeMaterials(0, 0),
                unchanged,
                out var plan,
                out var rejection),
            "planner permits a knowingly accepted merge with six zero Savvy rows and zero rank gain");
        Check.True(
            rejection == PetPlanRejection.None &&
            plan is not null &&
            plan.PrimaryPetAfter.InitialSavvy == primary.InitialSavvy &&
            plan.PrimaryPetAfter.Rank == primary.Rank &&
            plan.PrimaryPetAfter.CompletedPetMerges == 1,
            "all-zero outcome still consumes the deputy and advances merge count without regressing values");
    }

    private static void CheckSoulContractDoesNotAffectPetMerge()
    {
        var primary = CreatePet(
            petId: 5,
            ScreenshotPrimary,
            PetSavvy.Zero,
            isSummoned: true);
        var deputy = CreatePet(
            petId: 6,
            ScreenshotDeputyBasic,
            ScreenshotDeputyAdded,
            isSummoned: false);
        var contractedPrimary = primary with
        {
            HasSoulContract = true,
            SoulContractStage = 6
        };
        var contractedDeputy = deputy with
        {
            HasSoulContract = true,
            SoulContractStage = 6
        };
        var gains = new PetSavvy(
            3.90m,
            7.80m,
            5.00m,
            0m,
            0m,
            2.11m);
        var outcome = new AuthoritativePetMergeOutcome(
            primary.Rank + 3.25m,
            primary.InitialSavvy + gains);
        var materials = new PetMergeMaterials(5, 0);

        Check.True(
            primary.TotalSavvy == contractedPrimary.TotalSavvy &&
            primary.EffectiveTotalSavvy !=
                contractedPrimary.EffectiveTotalSavvy,
            "Soul Contract changes effective traits but not raw pet-Merge input");
        var unsignedAccepted = PetManagerPlanner.TryPlanPetMerge(
            PetContentTestCatalog.Instance,
            primary,
            deputy,
            primary.OwnerCharacterId,
            materials,
            outcome,
            out var unsignedPlan,
            out var unsignedRejection);
        var contractedAccepted = PetManagerPlanner.TryPlanPetMerge(
            PetContentTestCatalog.Instance,
            contractedPrimary,
            contractedDeputy,
            contractedPrimary.OwnerCharacterId,
            materials,
            outcome,
            out var contractedPlan,
            out var contractedRejection);
        Check.True(
            unsignedAccepted && contractedAccepted,
            "pet-to-pet Merge accepts the same raw authoritative outcome regardless of Soul stage");
        Check.True(
            unsignedRejection == PetPlanRejection.None &&
            contractedRejection == PetPlanRejection.None &&
            unsignedPlan!.PrimaryPetAfter.InitialSavvy ==
                contractedPlan!.PrimaryPetAfter.InitialSavvy &&
            unsignedPlan.PrimaryPetAfter.Rank ==
                contractedPlan.PrimaryPetAfter.Rank &&
            contractedPlan.PrimaryPetAfter.SoulContractStage == 6,
            "pet-to-pet Merge preserves Soul stage without feeding it into rank or Savvy gains");
    }

    private static PetSavvy Repeat(decimal value) =>
        new(value, value, value, value, value, value);

    private static OwnedPet CreatePet(
        long petId,
        PetSavvy initial,
        PetSavvy added,
        bool isSummoned) =>
        new(
            petId,
            OwnerCharacterId: 7,
            SpeciesType: 4,
            Name: $"Merge Pet {petId}",
            Level: 30,
            Experience: 0,
            Rank: 15m,
            Aptitude: PetAptitude.Smart,
            InitialSavvy: initial,
            AddedSavvy: added,
            BaseGrowthRates: PetSavvy.Zero,
            GrowthAcceleration: PetSavvy.Zero,
            CompletedPetMerges: 0,
            CompletedRebirths: 0,
            RebirthsRemaining: 1,
            HasSoulContract: false,
            HasOwnerMergeTalent: false,
            IsBound: false,
            IsSummoned: isSummoned,
            IsAway: false,
            CurrentEnergy: 0,
            MaximumEnergy: 100,
            Amity: 0,
            OwnerMerge: null);

    private sealed class SequenceRandom(params int[] values) : Random
    {
        private int _position;

        public int CallCount => _position;

        public override int Next(int minValue, int maxValue)
        {
            if (_position >= values.Length)
            {
                throw new InvalidOperationException(
                    "The merge test consumed too many random values.");
            }

            var value = values[_position++];
            if (value < minValue || value >= maxValue)
            {
                throw new InvalidOperationException(
                    $"Scripted random value {value} is outside " +
                    $"[{minValue}, {maxValue}).");
            }

            return value;
        }
    }
}
