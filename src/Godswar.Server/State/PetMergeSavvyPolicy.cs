using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal sealed record PetMergeSavvyStatRollEvidence(
    short StatCode,
    int PrimaryBasicHundredths,
    int DeputyBasicHundredths,
    int DeputyAddedHundredths,
    int AddedContributionHundredths,
    int SavvyDifferenceHundredths,
    int? LookupMinimumSavvyDifference,
    ushort? LookupBaseIncrease,
    int MinimumIncreaseHundredths,
    int MaximumIncreaseHundredths,
    int RolledIncreaseHundredths);

internal sealed record PetMergeSavvyRollEvidence(
    string PolicyRevision,
    string ContentRevision,
    int DeputySpeciesId,
    decimal SpeciesFactor,
    short SpiritCount,
    short MinimumPercent,
    short MaximumPercent,
    IReadOnlyList<PetMergeSavvyStatRollEvidence> Stats);

/// <summary>
/// Authoritative fixed-hundredth interpretation of the stock Pet_Alter
/// Inosculate preview. Each stat resolves and rolls independently from the
/// primary Basic, deputy Basic, and deputy level-scaled Added values.
/// </summary>
internal static class PetMergeSavvyPolicy
{
    public const string PolicyRevision =
        "historical-pet-alter-restrict-exact-decimal-v1";

    private const int AddedDivisor = 5;
    private const decimal HundredthsScale = 100m;

    public static bool TryRollGains(
        IPetContentCatalog content,
        PetSavvy primaryBasic,
        PetSavvy deputyBasic,
        PetSavvy deputyAdded,
        int deputySpeciesId,
        int spiritCount,
        Random random,
        out PetMergeSavvyRollEvidence evidence,
        out PetSavvy gains)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(random);

        if (!TryResolveBounds(
                content,
                primaryBasic,
                deputyBasic,
                deputyAdded,
                deputySpeciesId,
                spiritCount,
                out var resolved))
        {
            evidence = null!;
            gains = PetSavvy.Zero;
            return false;
        }

        var rolled = new int[resolved.Stats.Length];
        var stats = new PetMergeSavvyStatRollEvidence[resolved.Stats.Length];
        for (var index = 0; index < resolved.Stats.Length; index++)
        {
            var bounds = resolved.Stats[index];
            var draw = bounds.Minimum == bounds.Maximum
                ? bounds.Minimum
                : random.Next(bounds.Minimum, checked(bounds.Maximum + 1));
            rolled[index] = draw;
            stats[index] = new PetMergeSavvyStatRollEvidence(
                checked((short)(index + 1)),
                bounds.PrimaryBasic,
                bounds.DeputyBasic,
                bounds.DeputyAdded,
                bounds.AddedContribution,
                bounds.SavvyDifference,
                bounds.Lookup?.MinimumSavvyDifference,
                bounds.Lookup?.BaseIncrease,
                bounds.Minimum,
                bounds.Maximum,
                draw);
        }

        gains = FromHundredths(rolled);
        evidence = new PetMergeSavvyRollEvidence(
            PolicyRevision,
            content.Revision.Sha256,
            deputySpeciesId,
            resolved.Species.Factor,
            resolved.Spirits.SpiritCount,
            resolved.Spirits.MinimumPercent,
            resolved.Spirits.MaximumPercent,
            Array.AsReadOnly(stats));
        return true;
    }

    public static bool IsValidOutcome(
        IPetContentCatalog content,
        PetSavvy primaryBasic,
        PetSavvy deputyBasic,
        PetSavvy deputyAdded,
        int deputySpeciesId,
        int spiritCount,
        PetSavvy after)
    {
        if (!TryResolveBounds(
                content,
                primaryBasic,
                deputyBasic,
                deputyAdded,
                deputySpeciesId,
                spiritCount,
                out var resolved))
        {
            return false;
        }

        var afterValues = Values(after);
        var beforeValues = Values(primaryBasic);
        for (var index = 0; index < resolved.Stats.Length; index++)
        {
            if (!TryToExactHundredths(
                    afterValues[index],
                    out var afterHundredths) ||
                !TryToExactHundredths(
                    beforeValues[index],
                    out var beforeHundredths))
            {
                return false;
            }

            var increase = checked(afterHundredths - beforeHundredths);
            var bounds = resolved.Stats[index];
            if (increase < bounds.Minimum || increase > bounds.Maximum)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveBounds(
        IPetContentCatalog content,
        PetSavvy primaryBasic,
        PetSavvy deputyBasic,
        PetSavvy deputyAdded,
        int deputySpeciesId,
        int spiritCount,
        out ResolvedSavvyBounds resolved)
    {
        resolved = null!;
        if (!primaryBasic.IsNonNegative ||
            !deputyBasic.IsNonNegative ||
            !deputyAdded.IsNonNegative ||
            !content.TryGetMergeRankSpeciesFactor(
                deputySpeciesId,
                out var species) ||
            species.Factor <= 0m ||
            !content.TryGetMergeRankSpiritStep(
                spiritCount,
                out var spirits) ||
            spirits.SpiritCount != spiritCount ||
            spirits.MinimumPercent < 0 ||
            spirits.MaximumPercent < spirits.MinimumPercent ||
            spirits.MaximumPercent > 100)
        {
            return false;
        }

        var primaryValues = Values(primaryBasic);
        var deputyValues = Values(deputyBasic);
        var addedValues = Values(deputyAdded);
        var stats = new ResolvedStatBounds[primaryValues.Length];
        for (var index = 0; index < stats.Length; index++)
        {
            if (!TryToExactHundredths(
                    primaryValues[index],
                    out var primary) ||
                !TryToExactHundredths(
                    deputyValues[index],
                    out var deputy) ||
                !TryToWireHundredths(addedValues[index], out var added))
            {
                return false;
            }

            var addedContribution = added / AddedDivisor;
            var differenceWide =
                (long)addedContribution - primary + deputy;
            if (differenceWide < int.MinValue ||
                differenceWide > int.MaxValue)
            {
                return false;
            }

            var difference = (int)differenceWide;
            var hasLookup = content.TryResolveMergeSavvyLookup(
                difference,
                out var lookup);
            int maximum;
            try
            {
                maximum = hasLookup
                    ? checked(decimal.ToInt32(decimal.Truncate(
                        lookup.BaseIncrease * species.Factor)))
                    : 0;
            }
            catch (OverflowException)
            {
                return false;
            }

            if (maximum < 0)
            {
                return false;
            }

            var minimum = maximum == 0
                ? 0
                : spiritCount == 0
                    ? 1
                    : RoundHalfUp(
                        checked(maximum * spirits.MinimumPercent),
                        100);
            if (minimum < 0 || minimum > maximum)
            {
                return false;
            }

            stats[index] = new ResolvedStatBounds(
                primary,
                deputy,
                added,
                addedContribution,
                difference,
                hasLookup ? lookup : null,
                minimum,
                maximum);
        }

        resolved = new ResolvedSavvyBounds(species, spirits, stats);
        return true;
    }

    private static int RoundHalfUp(int value, int divisor) =>
        checked((value + divisor / 2) / divisor);

    private static bool TryToExactHundredths(decimal value, out int result)
    {
        if (!TryToWireHundredths(value, out result))
        {
            return false;
        }

        return value * HundredthsScale ==
            decimal.Truncate(value * HundredthsScale);
    }

    private static bool TryToWireHundredths(decimal value, out int result)
    {
        if (value < 0m || value > int.MaxValue / HundredthsScale)
        {
            result = 0;
            return false;
        }

        result = decimal.ToInt32(decimal.Round(
            value * HundredthsScale,
            0,
            MidpointRounding.AwayFromZero));
        return true;
    }

    private static decimal[] Values(PetSavvy value) =>
    [
        value.Agility,
        value.Strength,
        value.Accuracy,
        value.Technique,
        value.Wisdom,
        value.Luck
    ];

    private static PetSavvy FromHundredths(IReadOnlyList<int> values) =>
        new(
            values[0] / HundredthsScale,
            values[1] / HundredthsScale,
            values[2] / HundredthsScale,
            values[3] / HundredthsScale,
            values[4] / HundredthsScale,
            values[5] / HundredthsScale);

    private sealed record ResolvedSavvyBounds(
        PetMergeRankSpeciesFactorContentDefinition Species,
        PetMergeRankSpiritStepContentDefinition Spirits,
        ResolvedStatBounds[] Stats);

    private sealed record ResolvedStatBounds(
        int PrimaryBasic,
        int DeputyBasic,
        int DeputyAdded,
        int AddedContribution,
        int SavvyDifference,
        PetMergeSavvyLookupContentDefinition? Lookup,
        int Minimum,
        int Maximum);
}
