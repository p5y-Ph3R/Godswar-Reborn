namespace Godswar.Server.Application.Pets;

internal static partial class PetBasicSavvyRedistributionPolicy
{
    private const int BasisPointScale = 10_000;

    private static void AllocateExtremeSingleFocus(
        int[] values,
        int totalUnits,
        int primary,
        Random random)
    {
        values[primary] = SamplePercentRange(
            totalUnits,
            minimumBasisPoints: 9_000,
            maximumBasisPoints: 9_200,
            random);
        var remainder = checked(totalUnits - values[primary]);
        var recipients = OtherStats(primary);
        var adaptiveMinimum = Math.Min(
            CeilingBasisPoints(totalUnits, 170),
            remainder / recipients.Length);
        AllocateBalancedResidual(
            values,
            recipients,
            remainder,
            totalUnits,
            random,
            adaptiveMinimum,
            FloorBasisPoints(totalUnits, 220));
    }

    private static void AllocateStrongSingleFocus(
        int[] values,
        int totalUnits,
        int primary,
        Random random)
    {
        values[primary] = SamplePercentRange(
            totalUnits,
            minimumBasisPoints: 8_200,
            maximumBasisPoints: 8_600,
            random);
        AllocateBalancedResidual(
            values,
            OtherStats(primary),
            checked(totalUnits - values[primary]),
            totalUnits,
            random);
    }

    private static void AllocateDualExtremeFocus(
        int[] values,
        int totalUnits,
        int primary,
        int secondary,
        Random random)
    {
        var focusedTotal = RoundBasisPoints(totalUnits, 9_000);
        values[primary] = SamplePercentRange(
            totalUnits,
            minimumBasisPoints: 5_300,
            maximumBasisPoints: 5_700,
            random);
        values[secondary] = checked(focusedTotal - values[primary]);
        AllocateBalancedResidual(
            values,
            OtherStats(primary, secondary),
            checked(totalUnits - focusedTotal),
            totalUnits,
            random);
    }

    private static void AllocateDualMediumFocus(
        int[] values,
        int totalUnits,
        int primary,
        int secondary,
        int tertiary,
        Random random)
    {
        var minimumResidual = CeilingBasisPoints(totalUnits, 510);
        var minimumTertiary = CeilingBasisPoints(totalUnits, 1_700);
        values[primary] = SamplePercentRange(
            totalUnits,
            minimumBasisPoints: 4_200,
            maximumBasisPoints: 4_700,
            random);

        var minimumSecondary = CeilingBasisPoints(totalUnits, 2_800);
        var maximumSecondary = Math.Min(
            FloorBasisPoints(totalUnits, 3_200),
            checked(
                totalUnits -
                values[primary] -
                minimumTertiary -
                minimumResidual));
        values[secondary] = SampleInclusive(
            minimumSecondary,
            maximumSecondary,
            random);

        var maximumTertiary = Math.Min(
            FloorBasisPoints(totalUnits, 2_100),
            checked(
                totalUnits -
                values[primary] -
                values[secondary] -
                minimumResidual));
        values[tertiary] = SampleInclusive(
            minimumTertiary,
            maximumTertiary,
            random);

        AllocateBalancedResidual(
            values,
            OtherStats(primary, secondary, tertiary),
            checked(
                totalUnits -
                values[primary] -
                values[secondary] -
                values[tertiary]),
            totalUnits,
            random);
    }

    private static void AllocateDualFocus(
        int[] values,
        int totalUnits,
        int primary,
        int secondary,
        Random random)
    {
        values[primary] = SamplePercentRange(
            totalUnits,
            minimumBasisPoints: 4_100,
            maximumBasisPoints: 4_500,
            random);
        values[secondary] = SamplePercentRange(
            totalUnits,
            minimumBasisPoints: 4_100,
            maximumBasisPoints: 4_500,
            random);
        AllocateBalancedResidual(
            values,
            OtherStats(primary, secondary),
            checked(
                totalUnits - values[primary] - values[secondary]),
            totalUnits,
            random);
    }

    private static void AllocateTrioFocus(
        int[] values,
        int totalUnits,
        int primary,
        int secondary,
        int tertiary,
        Random random)
    {
        foreach (var focus in new[] { primary, secondary, tertiary })
        {
            values[focus] = SamplePercentRange(
                totalUnits,
                minimumBasisPoints: 2_700,
                maximumBasisPoints: 3_100,
                random);
        }
        AllocateBalancedResidual(
            values,
            OtherStats(primary, secondary, tertiary),
            checked(
                totalUnits -
                values[primary] -
                values[secondary] -
                values[tertiary]),
            totalUnits,
            random);
    }

    private static void AllocateQuadFocus(
        int[] values,
        int totalUnits,
        int primary,
        int secondary,
        int tertiary,
        int quaternary,
        Random random)
    {
        foreach (var focus in new[]
                 { primary, secondary, tertiary, quaternary })
        {
            values[focus] = SamplePercentRange(
                totalUnits,
                minimumBasisPoints: 1_900,
                maximumBasisPoints: 2_200,
                random);
        }
        AllocateBalancedResidual(
            values,
            OtherStats(primary, secondary, tertiary, quaternary),
            checked(
                totalUnits -
                values[primary] -
                values[secondary] -
                values[tertiary] -
                values[quaternary]),
            totalUnits,
            random);
    }

    private static void AllocateBalancedResidual(
        int[] values,
        int[] recipients,
        int amount,
        int totalUnits,
        Random random,
        int explicitMinimum = 1,
        int explicitMaximum = int.MaxValue)
    {
        if (recipients.Length == 0 || amount < recipients.Length)
        {
            throw new InvalidOperationException(
                "The Basic-Savvy residual allocation is not feasible.");
        }

        var average = amount / (decimal)recipients.Length;
        var tolerance = totalUnits / 400m;
        var minimum = Math.Max(
            explicitMinimum,
            Math.Max(1, decimal.ToInt32(decimal.Ceiling(
                average - tolerance))));
        var maximum = Math.Min(
            explicitMaximum,
            decimal.ToInt32(decimal.Floor(average + tolerance)));
        if (maximum >= minimum &&
            amount >= (long)minimum * recipients.Length &&
            amount <= (long)maximum * recipients.Length)
        {
            AllocateBounded(
                values,
                recipients,
                amount,
                minimum,
                maximum,
                random);
            return;
        }

        AllocateTightBalanced(values, recipients, amount, random);
    }

    private static void AllocateTightBalanced(
        int[] values,
        int[] recipients,
        int amount,
        Random random)
    {
        var minimum = amount / recipients.Length;
        if (minimum <= 0)
        {
            throw new InvalidOperationException(
                "The Basic-Savvy residual cannot keep every stat positive.");
        }

        Shuffle(recipients, random);
        var recipientsWithExtra = amount % recipients.Length;
        for (var index = 0; index < recipients.Length; index++)
        {
            values[recipients[index]] =
                minimum + (index < recipientsWithExtra ? 1 : 0);
        }
    }

    private static void AllocateBounded(
        int[] values,
        int[] recipients,
        int amount,
        int minimum,
        int maximum,
        Random random)
    {
        if (recipients.Length == 0 ||
            minimum < 0 ||
            maximum < minimum ||
            amount < (long)minimum * recipients.Length ||
            amount > (long)maximum * recipients.Length)
        {
            throw new InvalidOperationException(
                "The Basic-Savvy allocation is not feasible.");
        }

        Shuffle(recipients, random);
        var capacity = maximum - minimum;
        var remaining = checked(amount - minimum * recipients.Length);
        for (var index = 0; index < recipients.Length; index++)
        {
            var recipientsAfterThis = recipients.Length - index - 1;
            var minimumExtra = checked((int)Math.Max(
                0L,
                (long)remaining -
                    (long)capacity * recipientsAfterThis));
            var maximumExtra = Math.Min(capacity, remaining);
            var extra = index == recipients.Length - 1
                ? remaining
                : checked((int)random.NextInt64(
                    minimumExtra,
                    (long)maximumExtra + 1));
            values[recipients[index]] = checked(minimum + extra);
            remaining -= extra;
        }

        if (remaining != 0)
        {
            throw new InvalidOperationException(
                "The Basic-Savvy allocation left an unassigned remainder.");
        }
    }

    private static int SamplePercentRange(
        int totalUnits,
        int minimumBasisPoints,
        int maximumBasisPoints,
        Random random) =>
        SampleInclusive(
            CeilingBasisPoints(totalUnits, minimumBasisPoints),
            FloorBasisPoints(totalUnits, maximumBasisPoints),
            random);

    private static int SampleInclusive(
        int minimum,
        int maximum,
        Random random)
    {
        if (maximum < minimum)
        {
            throw new InvalidOperationException(
                "The Basic-Savvy percentage range is not feasible.");
        }
        return minimum == maximum
            ? minimum
            : checked((int)random.NextInt64(
                minimum,
                (long)maximum + 1));
    }

    private static int RoundBasisPoints(int total, int basisPoints) =>
        checked((int)(
            ((long)total * basisPoints + BasisPointScale / 2) /
            BasisPointScale));

    private static int CeilingBasisPoints(int total, int basisPoints) =>
        checked((int)(
            ((long)total * basisPoints + BasisPointScale - 1) /
            BasisPointScale));

    private static int FloorBasisPoints(int total, int basisPoints) =>
        checked((int)((long)total * basisPoints / BasisPointScale));

    private static void Shuffle(int[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (values[index], values[swap]) = (values[swap], values[index]);
        }
    }
}
