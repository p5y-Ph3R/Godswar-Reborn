using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetMergeRankPolicyChecks
{
    public static Task RunAsync()
    {
        var content = PetContentTestCatalog.Instance;
        Check.Equal(655.35m, content.Settings.MaximumRank,
            "pet rank cap matches the native UInt16 hundredths field");
        Check.Equal(200, content.MergeRankLookup.Count,
            "all stock Qualityadd rows are pinned");
        Check.Equal(PetSpeciesCatalog.SpeciesCount,
            content.MergeRankSpeciesFactors.Count,
            "every stock species has a pinned Merge rank factor");
        Check.Equal(6, content.MergeRankSpiritSteps.Count,
            "zero through five spirit-count effectiveness rows are pinned");

        CheckLookupBoundaries(content);
        CheckDeputySpeciesAndFiveSpiritBounds(content);
        CheckHistoricalDecimalFactorSemantics(content);
        CheckNoGainAndWireCap(content);
        CheckInstalledClientCompatibilityGuard();
        return Task.CompletedTask;
    }

    private static void CheckInstalledClientCompatibilityGuard()
    {
        var baseline = PetContentBaseline.Create();
        var thresholdMutation = baseline.MergeRankLookup.ToArray();
        thresholdMutation[50] = thresholdMutation[50] with
        {
            MinimumRankDifference = checked(
                thresholdMutation[50].MinimumRankDifference + 1)
        };
        AssertCompatibilityMutationRejected(
            baseline,
            "a structurally valid Qualityadd threshold mutation",
            lookup: thresholdMutation);

        var baseMutation = baseline.MergeRankLookup.ToArray();
        baseMutation[120] = baseMutation[120] with
        {
            BaseIncrease = checked((ushort)(
                baseMutation[120].BaseIncrease + 1))
        };
        AssertCompatibilityMutationRejected(
            baseline,
            "a structurally valid Qualityadd value mutation",
            lookup: baseMutation);

        var factorMutation = baseline.MergeRankSpeciesFactors.ToArray();
        factorMutation[3] = factorMutation[3] with { Factor = 2.7m };
        AssertCompatibilityMutationRejected(
            baseline,
            "a structurally valid species-factor mutation",
            speciesFactors: factorMutation);

        var spiritMutation = baseline.MergeRankSpiritSteps.ToArray();
        spiritMutation[0] = spiritMutation[0] with { MinimumPercent = 11 };
        AssertCompatibilityMutationRejected(
            baseline,
            "a structurally valid spirit-bound mutation",
            spiritSteps: spiritMutation);
    }

    private static void AssertCompatibilityMutationRejected(
        PinnedPetContentCatalog baseline,
        string assertion,
        IReadOnlyList<PetMergeRankLookupContentDefinition>? lookup = null,
        IReadOnlyList<PetMergeRankSpeciesFactorContentDefinition>?
            speciesFactors = null,
        IReadOnlyList<PetMergeRankSpiritStepContentDefinition>?
            spiritSteps = null)
    {
        try
        {
            _ = PinnedPetContentCatalog.Create(
                baseline.Revision.Source,
                baseline.Settings,
                baseline.Species,
                baseline.Aptitudes,
                baseline.NativeProfiles,
                baseline.ExperienceSteps,
                baseline.RebirthSteps,
                baseline.MergeSavvySteps,
                baseline.MergeSavvyLookup,
                baseline.HatchRankSteps,
                lookup ?? baseline.MergeRankLookup,
                speciesFactors ?? baseline.MergeRankSpeciesFactors,
                spiritSteps ?? baseline.MergeRankSpiritSteps);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "reviewed installed-client baseline",
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Pinned pet content accepted {assertion}.");
    }

    private static void CheckLookupBoundaries(
        Godswar.Server.Application.Pets.IPetContentCatalog content)
    {
        Check.True(!content.TryResolveMergeRankLookup(-3001, out _),
            "difference below first Qualityadd row resolves no gain");
        Check.True(content.TryResolveMergeRankLookup(-3000, out var first) &&
            first.BaseIncrease == 1,
            "first Qualityadd threshold resolves exactly");
        Check.True(content.TryResolveMergeRankLookup(-1, out var belowEqual) &&
            belowEqual.MinimumRankDifference == -24 &&
            belowEqual.BaseIncrease == 248,
            "lookup chooses the greatest threshold not above the difference");
        Check.True(content.TryResolveMergeRankLookup(0, out var equal) &&
            equal.BaseIncrease == 250,
            "equal-rank pets use base value 250");
        Check.True(content.TryResolveMergeRankLookup(500, out var last) &&
            last.BaseIncrease == 300,
            "last Qualityadd threshold resolves exactly");
        Check.True(content.TryResolveMergeRankLookup(5000, out var saturated) &&
            saturated.BaseIncrease == 300,
            "lookup saturates above its final threshold");
    }

    private static void CheckDeputySpeciesAndFiveSpiritBounds(
        Godswar.Server.Application.Pets.IPetContentCatalog content)
    {
        AssertRoll(content, speciesId: 2, scripted: 100,
            expectedIncrease: 100, "0.8 deputy factor minimum");
        AssertRoll(content, speciesId: 1, scripted: 350,
            expectedIncrease: 350, "1.4 deputy factor maximum");
        AssertRoll(content, speciesId: 37, scripted: 325,
            expectedIncrease: 325, "2.6 deputy factor minimum");

        Check.True(PetMergeRankPolicy.IsValidOutcome(
                content, 15m, 15m, 37, 5, 21.50m),
            "five-spirit 2.6-factor maximum is accepted");
        Check.True(!PetMergeRankPolicy.IsValidOutcome(
                content, 15m, 15m, 37, 5, 21.51m),
            "rank result above the published maximum is rejected");
    }

    private static void CheckHistoricalDecimalFactorSemantics(
        IPetContentCatalog content)
    {
        AssertRollAtRanks(content, 30m, 0.48m, speciesId: 2,
            scripted: 4, expectedIncrease: 4,
            "base 5 times decimal 0.8 is 4");
        AssertRollAtRanks(content, 30m, 0.48m, speciesId: 1,
            scripted: 7, expectedIncrease: 7,
            "base 5 times decimal 1.4 is 7");
        AssertRollAtRanks(content, 30m, 0.48m, speciesId: 37,
            scripted: 13, expectedIncrease: 13,
            "base 5 times decimal 2.6 is 13");
        AssertRoll(content, speciesId: 2, scripted: 200,
            expectedIncrease: 200,
            "base 250 times decimal 0.8 is 200");
        AssertRoll(content, speciesId: 1, scripted: 350,
            expectedIncrease: 350,
            "base 250 times decimal 1.4 is 350");
        AssertRoll(content, speciesId: 37, scripted: 650,
            expectedIncrease: 650,
            "base 250 times decimal 2.6 is 650");

        Check.True(PetMergeRankPolicy.TryRollIncrease(
                content, 15m, 15m, 1, 5, new FixedRandom(350),
                out var evidence, out var increase, out _) &&
            increase == 350 &&
            evidence.FactorAdjustedBaseIncrease == 350 &&
            evidence.AppliedSpeciesFactor == 1.4m &&
            evidence.PolicyRevision == PetMergeRankPolicy.PolicyRevision,
            "rank evidence preserves the historical decimal calculation");
    }

    private static void CheckNoGainAndWireCap(
        Godswar.Server.Application.Pets.IPetContentCatalog content)
    {
        Check.True(PetMergeRankPolicy.TryRollIncrease(
                content, 40m, 0m, 37, 5, new Random(1),
                out var noGain, out var unchanged) &&
            noGain == 0 && unchanged == 40m,
            "difference below the lookup produces a valid zero rank gain");

        Check.True(PetMergeRankPolicy.TryRollIncrease(
                content, 654m, 654m, 37, 5, new Random(1),
                out var capped, out var cappedRank) &&
            capped == 135 && cappedRank == 655.35m,
            "near-cap Merge reaches but never exceeds the wire-safe ceiling");

        Check.True(PetMergeRankPolicy.TryRollIncrease(
                content, 100.94m, 100.94m, 37, 5,
                new FixedRandom(325), out _, out _),
            "client-proven rank above 100 remains valid");
    }

    private static void AssertRoll(
        IPetContentCatalog content,
        int speciesId,
        int scripted,
        ushort expectedIncrease,
        string label)
    {
        AssertRollAtRanks(
            content,
            15m,
            15m,
            speciesId,
            scripted,
            expectedIncrease,
            label);
    }

    private static void AssertRollAtRanks(
        IPetContentCatalog content,
        decimal primaryRank,
        decimal deputyRank,
        int speciesId,
        int scripted,
        ushort expectedIncrease,
        string label) =>
        Check.True(PetMergeRankPolicy.TryRollIncrease(
                content, primaryRank, deputyRank, speciesId, 5,
                new FixedRandom(scripted), out var increase, out var after) &&
            increase == expectedIncrease &&
            after == primaryRank + expectedIncrease / 100m,
            label);

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) =>
            value >= minValue && value < maxValue
                ? value
                : throw new InvalidOperationException(
                    $"Scripted rank roll {value} is outside " +
                    $"[{minValue}, {maxValue}).");
    }
}
