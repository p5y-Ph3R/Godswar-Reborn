using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static class PetMergeSavvyLookupContentChecks
{
    public const string CheckName =
        "Installed-client pet Merge-savvy lookup content";

    public static Task RunAsync()
    {
        var content = PetContentTestCatalog.Instance;
        Check.Equal(200, content.MergeSavvyLookup.Count,
            "all Pet_Alter Restrict/Values rows are pinned");
        Check.Equal(content.MergeSavvyLookup.Count,
            content.Revision.MergeSavvyLookupCount,
            "manifest counts the Merge-savvy lookup rows");

        for (var index = 0; index < content.MergeSavvyLookup.Count; index++)
        {
            var order = index + 1;
            var expectedValue = checked((ushort)(order <= 100
                ? order
                : order * 2 - 100));
            Check.True(
                content.MergeSavvyLookup[index] ==
                    new PetMergeSavvyLookupContentDefinition(
                        ExpectedThreshold(order),
                        expectedValue),
                $"Pet_Alter Restrict/Values row {order} is exact");
        }

        Check.True(!content.TryResolveMergeSavvyLookup(-4001, out _),
            "difference below the first Restrict row has no lookup value");
        Check.True(content.TryResolveMergeSavvyLookup(-4000, out var first) &&
            first.BaseIncrease == 1,
            "first Restrict boundary resolves exactly");
        Check.True(content.TryResolveMergeSavvyLookup(-635, out var sample) &&
            sample.MinimumSavvyDifference == -656 &&
            sample.BaseIncrease == 162,
            "lookup selects the greatest Restrict value not above a difference");
        Check.True(content.TryResolveMergeSavvyLookup(800, out var last) &&
            last.BaseIncrease == 300 &&
            content.TryResolveMergeSavvyLookup(5000, out var saturated) &&
            saturated == last,
            "lookup resolves its last row and saturates above it");

        Check.Equal(6, content.MergeRankSpiritSteps.Count,
            "shared Merge spirit content covers zero through five items");
        for (short spiritCount = 0; spiritCount <= 5; spiritCount++)
        {
            Check.True(content.TryGetMergeRankSpiritStep(
                    spiritCount, out var spirit) &&
                spirit.MinimumPercent == spiritCount * 10 &&
                spirit.MaximumPercent == 100,
                $"spirit count {spiritCount} has native 10%-step bounds");
        }

        AssertCompatibilityMutationRejected();
        return Task.CompletedTask;
    }

    private static void AssertCompatibilityMutationRejected()
    {
        var baseline = PetContentBaseline.Create();
        var mutation = baseline.MergeSavvyLookup.ToArray();
        mutation[130] = mutation[130] with
        {
            MinimumSavvyDifference = checked(
                mutation[130].MinimumSavvyDifference + 1)
        };
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
                mutation,
                baseline.HatchRankSteps,
                baseline.MergeRankLookup,
                baseline.MergeRankSpeciesFactors,
                baseline.MergeRankSpiritSteps);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(
                "reviewed installed-client baseline",
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            "Pinned content accepted a mutated Pet_Alter Restrict row.");
    }

    private static int ExpectedThreshold(int order) =>
        order switch
        {
            <= 10 => -4000 + (order - 1) * 10,
            <= 16 => -3910 + (order - 10) * 12,
            <= 22 => -3838 + (order - 16) * 14,
            <= 28 => -3754 + (order - 22) * 16,
            <= 34 => -3658 + (order - 28) * 18,
            <= 40 => -3550 + (order - 34) * 20,
            <= 45 => -3430 + (order - 40) * 22,
            <= 50 => -3320 + (order - 45) * 24,
            <= 125 => -3200 + (order - 50) * 32,
            <= 175 => -800 + (order - 125) * 24,
            <= 200 => 400 + (order - 175) * 16,
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
}
