namespace Godswar.Server.Application.Pets;

internal enum PetBasicSavvyRedistributionTier : byte
{
    // Values 1-7 are retained for durable evidence created by older policies.
    ExtremeSingleFocus = 1,
    StrongSingleFocus = 2,
    DualFocus = 3,
    Balanced = 4,
    DominantDualFocus = 5,
    OrdinaryRandom = 6,
    WeightedTripleFocus = 7,
    DualExtremeFocus = 8,
    DualMediumFocus = 9,
    TrioFocus = 10,
    QuadFocus = 11
}

internal enum PetSavvyStat : byte
{
    None = 0,
    Agility = 1,
    Strength = 2,
    Accuracy = 3,
    Technique = 4,
    Wisdom = 5,
    Luck = 6
}

internal readonly record struct PetBasicSavvyRedistributionRoll(
    PetBasicSavvyRedistributionTier Tier,
    PetContentStatVector BasicSavvy,
    PetSavvyStat PrimaryFocus,
    PetSavvyStat SecondaryFocus,
    PetSavvyStat TertiaryFocus,
    PetSavvyStat QuaternaryFocus)
{
    public decimal TotalSavvy =>
        BasicSavvy.Agility +
        BasicSavvy.Strength +
        BasicSavvy.Accuracy +
        BasicSavvy.Technique +
        BasicSavvy.Wisdom +
        BasicSavvy.Luck;
}

/// <summary>
/// Server-authored Fairy's Feather policy. It redistributes, but never
/// creates or removes, the pet's current Basic Savvy. All calculations use
/// the stock client's hundredth-point precision.
/// </summary>
internal static partial class PetBasicSavvyRedistributionPolicy
{
    public const string Version = "fairy-basic-savvy-v4";

    private const int StatCount = 6;
    private const int ValueScale = 100;
    private const int MinimumSupportedUnits = ValueScale;

    public static PetBasicSavvyRedistributionRoll Redistribute(
        PetContentStatVector currentBasicSavvy,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var totalUnits = ToTotalUnits(currentBasicSavvy);
        var tier = ResolveTier(random.Next(100));
        var values = new int[StatCount];
        var primary = -1;
        var secondary = -1;
        var tertiary = -1;
        var quaternary = -1;

        switch (tier)
        {
            case PetBasicSavvyRedistributionTier.ExtremeSingleFocus:
                primary = random.Next(StatCount);
                AllocateExtremeSingleFocus(
                    values,
                    totalUnits,
                    primary,
                    random);
                break;

            case PetBasicSavvyRedistributionTier.StrongSingleFocus:
                primary = random.Next(StatCount);
                AllocateStrongSingleFocus(
                    values,
                    totalUnits,
                    primary,
                    random);
                break;

            case PetBasicSavvyRedistributionTier.DualExtremeFocus:
                primary = random.Next(StatCount);
                secondary = ResolveOtherStat(
                    primary,
                    random.Next(StatCount - 1));
                AllocateDualExtremeFocus(
                    values,
                    totalUnits,
                    primary,
                    secondary,
                    random);
                break;

            case PetBasicSavvyRedistributionTier.DualMediumFocus:
                SelectThreeFocusStats(
                    random,
                    out primary,
                    out secondary,
                    out tertiary);
                AllocateDualMediumFocus(
                    values,
                    totalUnits,
                    primary,
                    secondary,
                    tertiary,
                    random);
                break;

            case PetBasicSavvyRedistributionTier.DualFocus:
                primary = random.Next(StatCount);
                secondary = ResolveOtherStat(
                    primary,
                    random.Next(StatCount - 1));
                AllocateDualFocus(
                    values,
                    totalUnits,
                    primary,
                    secondary,
                    random);
                break;

            case PetBasicSavvyRedistributionTier.TrioFocus:
                SelectThreeFocusStats(
                    random,
                    out primary,
                    out secondary,
                    out tertiary);
                AllocateTrioFocus(
                    values,
                    totalUnits,
                    primary,
                    secondary,
                    tertiary,
                    random);
                break;

            case PetBasicSavvyRedistributionTier.QuadFocus:
                SelectFourFocusStats(
                    random,
                    out primary,
                    out secondary,
                    out tertiary,
                    out quaternary);
                AllocateQuadFocus(
                    values,
                    totalUnits,
                    primary,
                    secondary,
                    tertiary,
                    quaternary,
                    random);
                break;

            case PetBasicSavvyRedistributionTier.OrdinaryRandom:
                AllocateBounded(
                    values,
                    Enumerable.Range(0, StatCount).ToArray(),
                    totalUnits,
                    CeilingBasisPoints(totalUnits, 500),
                    FloorBasisPoints(totalUnits, 3_000),
                    random);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Basic-Savvy redistribution tier {tier}.");
        }

        EnsureValidResult(values, totalUnits);
        return new PetBasicSavvyRedistributionRoll(
            tier,
            ToVector(values),
            ToStat(primary),
            ToStat(secondary),
            ToStat(tertiary),
            ToStat(quaternary));
    }

    internal static PetBasicSavvyRedistributionTier ResolveTier(
        int percentile)
    {
        if (percentile is < 0 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        return percentile switch
        {
            < 1 => PetBasicSavvyRedistributionTier.ExtremeSingleFocus,
            < 5 => PetBasicSavvyRedistributionTier.StrongSingleFocus,
            < 10 => PetBasicSavvyRedistributionTier.DualExtremeFocus,
            < 15 => PetBasicSavvyRedistributionTier.DualMediumFocus,
            < 25 => PetBasicSavvyRedistributionTier.DualFocus,
            < 50 => PetBasicSavvyRedistributionTier.TrioFocus,
            < 80 => PetBasicSavvyRedistributionTier.QuadFocus,
            _ => PetBasicSavvyRedistributionTier.OrdinaryRandom
        };
    }

    private static void SelectThreeFocusStats(
        Random random,
        out int primary,
        out int secondary,
        out int tertiary)
    {
        primary = random.Next(StatCount);
        secondary = ResolveOtherStat(
            primary,
            random.Next(StatCount - 1));
        var remaining = OtherStats(primary, secondary);
        tertiary = remaining[random.Next(remaining.Length)];
    }

    private static void SelectFourFocusStats(
        Random random,
        out int primary,
        out int secondary,
        out int tertiary,
        out int quaternary)
    {
        SelectThreeFocusStats(
            random,
            out primary,
            out secondary,
            out tertiary);
        var remaining = OtherStats(primary, secondary, tertiary);
        quaternary = remaining[random.Next(remaining.Length)];
    }

    private static int ToTotalUnits(PetContentStatVector savvy)
    {
        var values = new[]
        {
            savvy.Agility,
            savvy.Strength,
            savvy.Accuracy,
            savvy.Technique,
            savvy.Wisdom,
            savvy.Luck
        };
        long total = 0;
        foreach (var value in values)
        {
            if (value < 0m || value > int.MaxValue / (decimal)ValueScale)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(savvy),
                    "Basic Savvy is outside the supported native range.");
            }

            var scaled = value * ValueScale;
            if (scaled != decimal.Truncate(scaled))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(savvy),
                    "Basic Savvy must be non-negative hundredth values.");
            }

            total = checked(total + decimal.ToInt64(scaled));
        }

        if (total < MinimumSupportedUnits || total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(savvy),
                "Total Basic Savvy is outside the supported native range.");
        }

        return checked((int)total);
    }

    private static int[] OtherStats(
        int first,
        int second = -1,
        int third = -1,
        int fourth = -1) =>
        Enumerable.Range(0, StatCount)
            .Where(index =>
                index != first &&
                index != second &&
                index != third &&
                index != fourth)
            .ToArray();

    private static int ResolveOtherStat(int first, int otherIndex) =>
        otherIndex >= first ? otherIndex + 1 : otherIndex;

    private static PetContentStatVector ToVector(
        IReadOnlyList<int> values) =>
        new(
            values[0] / (decimal)ValueScale,
            values[1] / (decimal)ValueScale,
            values[2] / (decimal)ValueScale,
            values[3] / (decimal)ValueScale,
            values[4] / (decimal)ValueScale,
            values[5] / (decimal)ValueScale);

    private static PetSavvyStat ToStat(int index) =>
        index < 0 ? PetSavvyStat.None : (PetSavvyStat)(index + 1);

    private static void EnsureValidResult(
        IReadOnlyList<int> values,
        int expectedTotal)
    {
        if (values.Count != StatCount ||
            values.Any(static value => value <= 0) ||
            values.Sum() != expectedTotal)
        {
            throw new InvalidOperationException(
                "The Basic-Savvy redistribution violated its invariants.");
        }
    }
}
