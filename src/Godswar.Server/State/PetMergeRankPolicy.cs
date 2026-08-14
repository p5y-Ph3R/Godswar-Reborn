using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

internal sealed record PetMergeRankRollEvidence(
    string PolicyRevision,
    string ContentRevision,
    int PrimaryRankHundredths,
    int DeputyRankHundredths,
    int RankDifferenceHundredths,
    int? LookupMinimumRankDifference,
    ushort? LookupBaseIncrease,
    int DeputySpeciesId,
    decimal SpeciesFactor,
    decimal AppliedSpeciesFactor,
    short SpiritCount,
    short MinimumPercent,
    short MaximumPercent,
    int FactorAdjustedBaseIncrease,
    int UncappedMinimumIncrease,
    int UncappedMaximumIncrease,
    int RemainingToCap,
    int EffectiveMinimumIncrease,
    int EffectiveMaximumIncrease,
    ushort RolledIncrease,
    bool CapApplied,
    int MaximumRankHundredths);

/// <summary>
/// Authoritative interpretation of the stock Merge rank preview. All random
/// choices are made by the server in native fixed hundredths.
/// </summary>
internal static class PetMergeRankPolicy
{
    public const string PolicyRevision =
        "historical-client-decimal-pet-alter-v2";
    private const decimal HundredthsScale = 100m;

    public static bool TryRollIncrease(
        IPetContentCatalog content,
        decimal primaryRank,
        decimal deputyRank,
        int deputySpeciesId,
        int spiritCount,
        Random random,
        out ushort increase,
        out decimal rankAfter) =>
        TryRollIncrease(
            content,
            primaryRank,
            deputyRank,
            deputySpeciesId,
            spiritCount,
            random,
            out _,
            out increase,
            out rankAfter);

    public static bool TryRollIncrease(
        IPetContentCatalog content,
        decimal primaryRank,
        decimal deputyRank,
        int deputySpeciesId,
        int spiritCount,
        Random random,
        out PetMergeRankRollEvidence evidence,
        out ushort increase,
        out decimal rankAfter)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(random);

        if (!TryResolveBounds(
                content,
                primaryRank,
                deputyRank,
                deputySpeciesId,
                spiritCount,
                out var bounds))
        {
            evidence = null!;
            increase = 0;
            rankAfter = primaryRank;
            return false;
        }

        var rolled = bounds.Maximum == bounds.Minimum
            ? bounds.Minimum
            : random.Next(bounds.Minimum, checked(bounds.Maximum + 1));
        increase = checked((ushort)rolled);
        rankAfter = checked(
            (bounds.PrimaryHundredths + rolled) / HundredthsScale);
        evidence = new PetMergeRankRollEvidence(
            PolicyRevision,
            content.Revision.Sha256,
            bounds.PrimaryHundredths,
            bounds.DeputyHundredths,
            bounds.RankDifference,
            bounds.Lookup?.MinimumRankDifference,
            bounds.Lookup?.BaseIncrease,
            deputySpeciesId,
            bounds.Species.Factor,
            bounds.AppliedSpeciesFactor,
            bounds.Spirits.SpiritCount,
            bounds.Spirits.MinimumPercent,
            bounds.Spirits.MaximumPercent,
            bounds.FactorAdjustedBase,
            bounds.UncappedMinimum,
            bounds.UncappedMaximum,
            bounds.RemainingToCap,
            bounds.Minimum,
            bounds.Maximum,
            increase,
            bounds.Maximum < bounds.UncappedMaximum,
            bounds.MaximumRankHundredths);
        return true;
    }

    public static bool IsValidOutcome(
        IPetContentCatalog content,
        decimal primaryRank,
        decimal deputyRank,
        int deputySpeciesId,
        int spiritCount,
        decimal rankAfter)
    {
        if (!TryResolveBounds(
                content,
                primaryRank,
                deputyRank,
                deputySpeciesId,
                spiritCount,
                out var bounds) ||
            !TryToHundredths(rankAfter, out var afterHundredths))
        {
            return false;
        }

        var increase = afterHundredths - bounds.PrimaryHundredths;
        return increase >= bounds.Minimum && increase <= bounds.Maximum;
    }

    private static bool TryResolveBounds(
        IPetContentCatalog content,
        decimal primaryRank,
        decimal deputyRank,
        int deputySpeciesId,
        int spiritCount,
        out ResolvedRankBounds bounds)
    {
        bounds = null!;
        if (!TryToHundredths(primaryRank, out var primaryHundredths) ||
            !TryToHundredths(deputyRank, out var deputyHundredths) ||
            !TryToHundredths(
                content.Settings.MaximumRank,
                out var maximumRankHundredths) ||
            primaryHundredths > maximumRankHundredths ||
            deputyHundredths > maximumRankHundredths ||
            !content.TryGetMergeRankSpeciesFactor(
                deputySpeciesId,
                out var species) ||
            !content.TryGetMergeRankSpiritStep(
                spiritCount,
                out var spirits))
        {
            return false;
        }

        var difference = checked(deputyHundredths - primaryHundredths);
        var hasLookup = content.TryResolveMergeRankLookup(
            difference,
            out var lookup);
        // The historical merge preview rounds the XML decimal factor as
        // authored. This intentionally avoids the installed build's binary32
        // underflow artifact (for example, 300 * 2.6f becoming 779 instead
        // of the observed 780 hundredths).
        var appliedSpeciesFactor = species.Factor;
        var factorAdjusted = hasLookup
            ? decimal.ToInt32(decimal.Truncate(
                lookup.BaseIncrease * appliedSpeciesFactor))
            : 0;
        // Stock no-spirit rank preview is a single exact base value. Spirit
        // rows turn that base into a percentage range; Savvy uses a separate
        // 0.01..base rule for the same zero-material request.
        var uncappedMinimum = spiritCount == 0
            ? factorAdjusted
            : factorAdjusted > 0
                ? DivideRoundingUp(
                    checked(factorAdjusted * spirits.MinimumPercent),
                    100)
                : 0;
        var uncappedMaximum = spiritCount == 0
            ? factorAdjusted
            : factorAdjusted > 0
                ? checked(factorAdjusted * spirits.MaximumPercent / 100)
                : 0;
        var remaining = checked(maximumRankHundredths - primaryHundredths);
        var maximum = Math.Min(uncappedMaximum, remaining);
        var minimum = Math.Min(uncappedMinimum, maximum);
        if (minimum < 0 || maximum < 0)
        {
            return false;
        }

        bounds = new ResolvedRankBounds(
            primaryHundredths,
            deputyHundredths,
            difference,
            hasLookup ? lookup : null,
            species,
            appliedSpeciesFactor,
            spirits,
            factorAdjusted,
            uncappedMinimum,
            uncappedMaximum,
            remaining,
            minimum,
            maximum,
            maximumRankHundredths);
        return true;
    }

    private static int DivideRoundingUp(int value, int divisor) =>
        checked((value + divisor - 1) / divisor);

    private static bool TryToHundredths(decimal value, out int result)
    {
        if (value < 0m || value > ushort.MaxValue / HundredthsScale)
        {
            result = 0;
            return false;
        }

        var scaled = value * HundredthsScale;
        if (scaled != decimal.Truncate(scaled))
        {
            result = 0;
            return false;
        }

        result = decimal.ToInt32(scaled);
        return true;
    }

    private sealed record ResolvedRankBounds(
        int PrimaryHundredths,
        int DeputyHundredths,
        int RankDifference,
        PetMergeRankLookupContentDefinition? Lookup,
        PetMergeRankSpeciesFactorContentDefinition Species,
        decimal AppliedSpeciesFactor,
        PetMergeRankSpiritStepContentDefinition Spirits,
        int FactorAdjustedBase,
        int UncappedMinimum,
        int UncappedMaximum,
        int RemainingToCap,
        int Minimum,
        int Maximum,
        int MaximumRankHundredths);
}
