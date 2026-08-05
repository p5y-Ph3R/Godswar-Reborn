namespace Godswar.Server.State;

/// <summary>
/// Preserves the deterministic values emitted before per-item Holy Spirit
/// effectiveness was persisted. New implementations always persist their
/// random roll; this table is only a compatibility bridge for legacy rows.
/// </summary>
internal static class HolySpiritLegacyEffectiveness
{
    private static readonly short[] PercentHigh =
        [110, 170, 240, 320, 410, 500, 650, 850, 1100, 1400];

    private static readonly short[] PercentMedium =
        [80, 120, 170, 230, 300, 370, 500, 700, 950, 1200];

    private static readonly short[] FlatHigh =
        [120, 190, 280, 380, 500, 620, 850, 1200, 1650, 2200];

    private static readonly short[] FlatCritical =
        [150, 240, 340, 460, 590, 720, 950, 1300, 1800, 2400];

    private static readonly short[] FlatLow =
        [60, 90, 130, 170, 210, 250, 350, 500, 700, 950];

    public static bool TryResolve(
        short effectId,
        short level,
        out short value)
    {
        value = 0;
        if (level is < 1 or > 10)
        {
            return false;
        }

        var values = effectId switch
        {
            1 or 2 or 3 or 4 => PercentHigh,
            5 or 6 => FlatHigh,
            7 or 9 or 10 or 13 or 15 or 17 or 19 => PercentMedium,
            8 => FlatCritical,
            11 or 12 or 14 or 16 or 18 or 20 => FlatLow,
            _ => null
        };
        if (values is null)
        {
            return false;
        }

        value = values[level - 1];
        return true;
    }
}
