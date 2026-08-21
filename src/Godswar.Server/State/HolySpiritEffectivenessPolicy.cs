using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.State;

internal enum HolySpiritValueKind : byte
{
    Flat = 1,

    /// <summary>
    /// One unit is 0.01 percentage point. For example, 320 is 3.20%.
    /// </summary>
    HundredthPercent = 2
}

internal readonly record struct HolySpiritDefinition(
    uint ItemId,
    string Name,
    HolyStoneAffinity Affinity,
    short EffectId,
    int GradeOneMinimumValue,
    int GradeOneMaximumValue,
    HolySpiritValueKind ValueKind);

internal readonly record struct HolySpiritEffectivenessBracket(
    int MinimumValue,
    int MaximumValue)
{
    public bool Contains(int value) =>
        value >= MinimumValue && value <= MaximumValue;
}

internal readonly record struct HolySpiritEffectivenessRoll(
    HolySpiritDefinition Definition,
    short HolyStoneGrade,
    bool GoddessStoneApplied,
    HolySpiritEffectivenessBracket Bracket,
    int Value);

internal interface IHolySpiritEffectivenessRandomSource
{
    int NextInclusive(int minimumInclusive, int maximumInclusive);
}

/// <summary>
/// Defines stable Holy Spirit identities and durable acceptance ranges. Values
/// are fixed-point integers so persisted results and historical receipts can
/// be replayed without floating-point drift. PostgreSQL supplies the mutable
/// maximum for adjustable Cooled effects on the production roll path.
/// </summary>
internal static class HolySpiritEffectivenessPolicy
{
    public const short MinimumHolyStoneGrade =
        HolySpiritImplementationPolicy.MinimumHolyStoneGrade;
    public const short MaximumHolyStoneGrade =
        HolySpiritImplementationPolicy.MaximumHolyStoneGrade;

    private static readonly HolySpiritDefinition[] Definitions =
        HolySpiritImplementationPolicy.All
            .Select(ToStateDefinition)
            .ToArray();

    private static readonly IReadOnlyDictionary<uint, HolySpiritDefinition>
        ByItemId = Definitions.ToDictionary(static value => value.ItemId);

    public static IReadOnlyList<HolySpiritDefinition> All { get; } =
        Array.AsReadOnly(Definitions);

    public static bool TryGetDefinition(
        uint spiritItemId,
        out HolySpiritDefinition definition) =>
        ByItemId.TryGetValue(spiritItemId, out definition);

    public static bool TryGetDefinition(
        uint spiritItemId,
        out HolyStoneAffinity affinity,
        out short effectId,
        out int gradeOneMinimum,
        out int gradeOneMaximum,
        out HolySpiritValueKind valueKind)
    {
        if (!TryGetDefinition(spiritItemId, out var definition))
        {
            affinity = default;
            effectId = 0;
            gradeOneMinimum = 0;
            gradeOneMaximum = 0;
            valueKind = default;
            return false;
        }

        affinity = definition.Affinity;
        effectId = definition.EffectId;
        gradeOneMinimum = definition.GradeOneMinimumValue;
        gradeOneMaximum = definition.GradeOneMaximumValue;
        valueKind = definition.ValueKind;
        return true;
    }

    public static bool IsCompatibleWithHolyStone(
        uint spiritItemId,
        uint holyStoneItemId) =>
        HolySpiritImplementationPolicy.IsCompatibleWithHolyStone(
            spiritItemId,
            holyStoneItemId);

    public static bool TryGetGradeBracket(
        uint spiritItemId,
        int holyStoneGrade,
        out int lower,
        out int upper)
    {
        return HolySpiritImplementationPolicy.TryGetGradeBracket(
            spiritItemId,
            holyStoneGrade,
            out lower,
            out upper);
    }

    public static HolySpiritEffectivenessRoll Roll(
        uint spiritItemId,
        int holyStoneGrade,
        bool hasGoddessStone,
        IHolySpiritEffectivenessRandomSource randomSource)
    {
        if (!TryGetDefinition(spiritItemId, out var definition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(spiritItemId),
                spiritItemId,
                "Unknown Holy Spirit item.");
        }
        if (definition.EffectId is
            HolySpiritImplementationPolicy
                .CooledPhysicalDamageReductionEffectId or
            HolySpiritImplementationPolicy
                .CooledMagicDamageReductionEffectId or
            HolySpiritImplementationPolicy
                .CooledCriticalDamageReductionEffectId)
        {
            throw new InvalidOperationException(
                "Adjustable Cooled effects require a startup-pinned " +
                "PostgreSQL maximum.");
        }

        return Roll(
            spiritItemId,
            holyStoneGrade,
            hasGoddessStone,
            definition.GradeOneMaximumValue,
            randomSource);
    }

    public static HolySpiritEffectivenessRoll Roll(
        uint spiritItemId,
        int holyStoneGrade,
        bool hasGoddessStone,
        int gradeOneMaximum,
        IHolySpiritEffectivenessRandomSource randomSource)
    {
        ArgumentNullException.ThrowIfNull(randomSource);
        if (!TryGetDefinition(spiritItemId, out var definition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(spiritItemId),
                spiritItemId,
                "Unknown Holy Spirit item.");
        }
        if (holyStoneGrade is
                < MinimumHolyStoneGrade or > MaximumHolyStoneGrade ||
            gradeOneMaximum < definition.GradeOneMinimumValue ||
            gradeOneMaximum > definition.GradeOneMaximumValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(holyStoneGrade),
                holyStoneGrade,
                "Holy Stone grade or effectiveness maximum is invalid.");
        }

        var lower = checked(
            definition.GradeOneMinimumValue * holyStoneGrade);
        var upper = checked(gradeOneMaximum * holyStoneGrade);

        if (hasGoddessStone)
        {
            // The guide specifies that a Goddess' Stone raises the lower
            // effectiveness limit but does not publish an exact formula.
            // Raise the floor by 10% of the bracket span (rounded up), while
            // preserving a random result and the native upper limit.
            var span = checked(upper - lower);
            var uplift = Math.Max(1, checked((span + 9) / 10));
            lower = Math.Min(upper, checked(lower + uplift));
        }

        var value = randomSource.NextInclusive(lower, upper);
        var bracket = new HolySpiritEffectivenessBracket(lower, upper);
        if (!bracket.Contains(value))
        {
            throw new InvalidOperationException(
                "Holy Spirit random source returned a value outside the " +
                "authoritative bracket.");
        }

        return new HolySpiritEffectivenessRoll(
            definition,
            checked((short)holyStoneGrade),
            hasGoddessStone,
            bracket,
            value);
    }

    private static HolySpiritDefinition ToStateDefinition(
        HolySpiritImplementationDefinition definition) =>
        new(
            definition.ItemId,
            LegacyName(definition.ItemId),
            definition.Affinity switch
            {
                HolySpiritImplementationAffinity.Heated =>
                    HolyStoneAffinity.Heated,
                HolySpiritImplementationAffinity.Cooled =>
                    HolyStoneAffinity.Cooled,
                HolySpiritImplementationAffinity.Zephyr =>
                    HolyStoneAffinity.Zephyr,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(definition))
            },
            definition.EffectId,
            definition.GradeOneMinimumValue,
            definition.GradeOneMaximumValue,
            definition.ValueKind ==
                HolySpiritImplementationValueKind.HundredthPercent
                ? HolySpiritValueKind.HundredthPercent
                : HolySpiritValueKind.Flat);

    private static string LegacyName(uint itemId) =>
        itemId switch
        {
            9060 => "Fire Spirit of Destruction",
            9061 => "Fire Spirit of Penetration",
            9062 => "Fire Spirit of Fist",
            9063 => "Fire Spirit of Fiery",
            9064 => "Fire Spirit of Blood",
            9065 => "Fire Spirit of Pressure",
            9066 => "Fire Spirit of Assail",
            9067 => "Fire Spirit of Lightning",
            9080 => "Water Spirit of Darkness",
            9081 => "Water Spirit of Mist",
            9082 => "Water Spirit of Silence",
            9083 => "Water Spirit of Chillness",
            9084 => "Water Spirit of Ice",
            9085 => "Water Spirit of Frost",
            9086 => "Water Spirit of Intent",
            9087 => "Water Spirit of Resilience",
            9090 => "Daedalus Spirit of Attunement",
            9091 => "Hephaestus Spirit of Tempering",
            9092 => "Mnemosyne Spirit of Preservation",
            9093 => "Themis Spirit of Continuity",
            _ => throw new ArgumentOutOfRangeException(nameof(itemId))
        };
}
