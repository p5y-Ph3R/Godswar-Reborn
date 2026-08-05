namespace Godswar.Server.Domain.Inventory;

internal enum HolySpiritImplementationAffinity : byte
{
    Heated = 1,
    Cooled = 2
}

internal enum HolySpiritImplementationValueKind : byte
{
    Flat = 1,
    HundredthPercent = 2
}

internal readonly record struct HolySpiritImplementationDefinition(
    uint ItemId,
    uint HolyStoneItemId,
    HolySpiritImplementationAffinity Affinity,
    short EffectId,
    int GradeOneMinimumValue,
    int GradeOneMaximumValue,
    HolySpiritImplementationValueKind ValueKind);

/// <summary>
/// Owns the stable Holy Spirit identities and effectiveness brackets shared by
/// command receipt validation and the authoritative implementation planner.
/// </summary>
internal static class HolySpiritImplementationPolicy
{
    public const short MinimumHolyStoneGrade = 1;
    public const short MaximumHolyStoneGrade = 10;
    public const uint HeatedHolyStoneItemId = 9030;
    public const uint CooledHolyStoneItemId = 9031;

    private static readonly HolySpiritImplementationDefinition[] Definitions =
    [
        Percent(9060, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 1, 32, 80),
        Percent(9061, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 2, 32, 80),
        Flat(9062, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 5, 16, 40),
        Flat(9063, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 6, 12, 30),
        Percent(9064, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 7, 24, 60),
        Flat(9065, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 8, 40, 100),
        Percent(9066, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 3, 20, 50),
        Percent(9067, HeatedHolyStoneItemId,
            HolySpiritImplementationAffinity.Heated, 4, 24, 60),
        Percent(9080, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 9, 22, 55),
        Percent(9081, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 10, 22, 55),
        Flat(9082, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 11, 16, 40),
        Flat(9083, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 12, 14, 35),
        Percent(9084, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 19, 16, 40),
        Flat(9085, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 20, 16, 40),
        Percent(9086, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 13, 28, 70),
        Flat(9087, CooledHolyStoneItemId,
            HolySpiritImplementationAffinity.Cooled, 14, 40, 100)
    ];

    private static readonly IReadOnlyDictionary<uint,
        HolySpiritImplementationDefinition> ByItemId =
        Definitions.ToDictionary(static value => value.ItemId);

    public static IReadOnlyList<HolySpiritImplementationDefinition> All
        { get; } = Array.AsReadOnly(Definitions);

    public static bool TryGetDefinition(
        uint spiritItemId,
        out HolySpiritImplementationDefinition definition) =>
        ByItemId.TryGetValue(spiritItemId, out definition);

    public static bool IsCompatibleWithHolyStone(
        uint spiritItemId,
        uint holyStoneItemId) =>
        TryGetDefinition(spiritItemId, out var definition) &&
        definition.HolyStoneItemId == holyStoneItemId;

    public static bool TryGetGradeBracket(
        uint spiritItemId,
        int holyStoneGrade,
        out int lower,
        out int upper)
    {
        lower = 0;
        upper = 0;
        if (!TryGetDefinition(spiritItemId, out var definition) ||
            holyStoneGrade is
                < MinimumHolyStoneGrade or > MaximumHolyStoneGrade)
        {
            return false;
        }

        lower = checked(
            definition.GradeOneMinimumValue * holyStoneGrade);
        upper = checked(
            definition.GradeOneMaximumValue * holyStoneGrade);
        return true;
    }

    private static HolySpiritImplementationDefinition Percent(
        uint itemId,
        uint holyStoneItemId,
        HolySpiritImplementationAffinity affinity,
        short effectId,
        int minimum,
        int maximum) =>
        new(
            itemId,
            holyStoneItemId,
            affinity,
            effectId,
            minimum,
            maximum,
            HolySpiritImplementationValueKind.HundredthPercent);

    private static HolySpiritImplementationDefinition Flat(
        uint itemId,
        uint holyStoneItemId,
        HolySpiritImplementationAffinity affinity,
        short effectId,
        int minimum,
        int maximum) =>
        new(
            itemId,
            holyStoneItemId,
            affinity,
            effectId,
            minimum,
            maximum,
            HolySpiritImplementationValueKind.Flat);
}
