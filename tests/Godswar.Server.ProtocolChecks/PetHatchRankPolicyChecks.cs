using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetHatchRankPolicyChecks
{
    public const string CheckName =
        "Database-pinned weighted pet hatch ranks";

    public static Task RunAsync()
    {
        var catalog = PetContentBaseline.Create();
        var expected = new Dictionary<PetAptitude, decimal[]>
        {
            [PetAptitude.Weak] = [0m, 0.30m, 0.40m],
            [PetAptitude.Fool] = [0m, 0.30m, 0.40m],
            [PetAptitude.Cowish] = [0.30m, 0.40m, 0.80m],
            [PetAptitude.Moderate] = [0.30m, 0.40m, 0.80m],
            [PetAptitude.Rational] = [0.40m, 0.80m, 1.00m],
            [PetAptitude.Calm] = [0.40m, 0.80m, 1.00m],
            [PetAptitude.Grumpy] = [0.80m, 1.00m, 1.50m],
            [PetAptitude.Brave] = [0.80m, 1.00m, 1.50m],
            [PetAptitude.Zealous] = [1.00m, 1.50m, 2.00m],
            [PetAptitude.Smart] = [1.00m, 1.50m, 2.00m],
            [PetAptitude.Overbearing] = [1.50m, 2.00m, 2.70m],
            [PetAptitude.Ferocious] = [1.50m, 2.00m, 2.70m],
            [PetAptitude.Almighty] = [2.00m, 2.70m, 3.00m],
            [PetAptitude.Godly] = [2.00m, 2.70m, 3.00m],
            [PetAptitude.Celestial] = [2.70m, 3.00m, 3.60m],
            [PetAptitude.Transcendent] = [2.70m, 3.00m, 3.60m]
        };

        Check.Equal(
            PetAptitudeCatalog.Count *
                PetHatchRankContentPolicy.OutcomesPerAptitude,
            catalog.HatchRankSteps.Count,
            "all aptitude hatch-rank outcomes are pinned");
        foreach (var (aptitude, ranks) in expected)
        {
            var steps = catalog.HatchRankSteps
                .Where(value => value.Aptitude == (short)aptitude)
                .OrderBy(static value => value.OutcomeOrder)
                .ToArray();
            Check.True(
                steps.Select(static value => value.Rank)
                    .SequenceEqual(ranks),
                $"{aptitude} rank bracket is approved");
            Check.True(
                steps.Select(static value => value.Weight)
                    .SequenceEqual(new short[] { 60, 30, 10 }),
                $"{aptitude} uses 60/30/10 weights");

            Check.Equal(
                ranks[0],
                catalog.RollHatchRank((short)aptitude, 0).Rank,
                $"{aptitude} low roll begins at zero");
            Check.Equal(
                ranks[0],
                catalog.RollHatchRank((short)aptitude, 59).Rank,
                $"{aptitude} low roll ends at 59");
            Check.Equal(
                ranks[1],
                catalog.RollHatchRank((short)aptitude, 60).Rank,
                $"{aptitude} middle roll begins at 60");
            Check.Equal(
                ranks[1],
                catalog.RollHatchRank((short)aptitude, 89).Rank,
                $"{aptitude} middle roll ends at 89");
            Check.Equal(
                ranks[2],
                catalog.RollHatchRank((short)aptitude, 90).Rank,
                $"{aptitude} high roll begins at 90");
            Check.Equal(
                ranks[2],
                catalog.RollHatchRank((short)aptitude, 99).Rank,
                $"{aptitude} high roll ends at 99");
        }

        AssertInvalidRollRejected(catalog, -1);
        AssertInvalidRollRejected(catalog, 100);
        AssertIncompletePolicyRejected(catalog.HatchRankSteps.Skip(1).ToArray());
        AssertInvalidRankPolicyRejected(catalog.HatchRankSteps, 655.36m);
        AssertInvalidRankPolicyRejected(catalog.HatchRankSteps, 3.601m);
        AssertPinnedBaselineRejects(
            catalog,
            catalog.HatchRankSteps.Select(step =>
                    step.Aptitude == (short)PetAptitude.Weak &&
                    step.OutcomeOrder == 0
                        ? step with { Weight = 50 }
                        : step.Aptitude == (short)PetAptitude.Weak &&
                          step.OutcomeOrder == 1
                            ? step with { Weight = 40 }
                            : step)
                .ToArray(),
            "structurally valid 50/40/10 weights");
        AssertPinnedBaselineRejects(
            catalog,
            catalog.HatchRankSteps.Select(step =>
                    step.Aptitude == (short)PetAptitude.Weak &&
                    step.OutcomeOrder == 1
                        ? step with { Rank = 0.31m }
                        : step)
                .ToArray(),
            "an unapproved but structurally valid rank bracket");
        return Task.CompletedTask;
    }

    private static void AssertInvalidRollRejected(
        IPetContentCatalog catalog,
        int roll)
    {
        try
        {
            _ = catalog.RollHatchRank((short)PetAptitude.Smart, roll);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Hatch-rank policy accepted invalid roll {roll}.");
    }

    private static void AssertIncompletePolicyRejected(
        IReadOnlyList<PetHatchRankStepContentDefinition> steps)
    {
        try
        {
            PetHatchRankContentPolicy.Validate(
                PetAptitudeCatalog.All
                    .Select(static value => value.Value)
                    .ToArray(),
                steps);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Hatch-rank policy accepted incomplete content.");
    }

    private static void AssertInvalidRankPolicyRejected(
        IReadOnlyList<PetHatchRankStepContentDefinition> steps,
        decimal invalidRank)
    {
        var invalid = steps
            .Select(step =>
                step.Aptitude == (short)PetAptitude.Transcendent &&
                step.OutcomeOrder == 2
                    ? step with { Rank = invalidRank }
                    : step)
            .ToArray();
        try
        {
            PetHatchRankContentPolicy.Validate(
                PetAptitudeCatalog.All
                    .Select(static value => value.Value)
                    .ToArray(),
                invalid);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Hatch-rank policy accepted wire-unsafe rank {invalidRank}.");
    }

    private static void AssertPinnedBaselineRejects(
        PinnedPetContentCatalog catalog,
        IReadOnlyList<PetHatchRankStepContentDefinition> hatchRanks,
        string description)
    {
        try
        {
            _ = PinnedPetContentCatalog.Create(
                catalog.Revision.Source,
                catalog.Settings,
                catalog.Species,
                catalog.Aptitudes,
                catalog.NativeProfiles,
                catalog.ExperienceSteps,
                catalog.RebirthSteps,
                catalog.MergeSavvySteps,
                catalog.MergeSavvyLookup,
                hatchRanks,
                catalog.MergeRankLookup,
                catalog.MergeRankSpeciesFactors,
                catalog.MergeRankSpiritSteps);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains(
                "approved",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Pinned hatch-rank startup accepted {description}.");
    }
}
