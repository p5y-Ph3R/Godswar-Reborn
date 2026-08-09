namespace Godswar.Server.State;

internal static class PriestHealingMath
{
    private const decimal BasisPointScale = 10_000m;

    public static int ResolveHealAmount(
        int baseHeal,
        int outgoingHealingBonusBasisPoints,
        int receivedHealingBonusBasisPoints)
    {
        if (baseHeal <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseHeal),
                baseHeal,
                "A base heal must be positive.");
        }

        var combinedMultiplier = Math.Max(
            0m,
            BasisPointScale +
            outgoingHealingBonusBasisPoints +
            receivedHealingBonusBasisPoints);
        var resolved = decimal.Truncate(
            baseHeal *
            combinedMultiplier /
            BasisPointScale);

        return resolved >= int.MaxValue
            ? int.MaxValue
            : decimal.ToInt32(resolved);
    }

    public static int ResolveCombatTextAmount(
        int resolvedHeal,
        int appliedHeal)
    {
        if (resolvedHeal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolvedHeal),
                resolvedHeal,
                "A resolved heal cannot be negative.");
        }

        if (appliedHeal < 0 || appliedHeal > resolvedHeal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(appliedHeal),
                appliedHeal,
                "An applied heal must be between zero and the resolved heal.");
        }

        // The reference server reports resolved healing before the HP cap,
        // just as damage combat text reports resolved damage before lethal
        // overkill is capped. Applied healing remains the authoritative HP
        // mutation and may be zero for a living target already at full HP.
        return resolvedHeal;
    }
}
