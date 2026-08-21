namespace Godswar.Server.World.Systems.Combat;

internal enum CombatRandomStage : byte
{
    Hit = 1,
    Critical = 2,
    StatusProc = 3
}

/// <summary>
/// Stateless, platform-stable combat rolls derived only from server-owned
/// event identity. Separate stage salts prevent a hit roll from being reused
/// as a critical or hostile-status roll.
/// </summary>
internal static class DeterministicCombatRandom
{
    private const ulong TargetSalt = 0x9E3779B97F4A7C15UL;
    private const ulong StageSalt = 0xD1B54A32D192ED03UL;

    public static int RollBasisPoints(
        ulong eventId,
        int targetOrder,
        CombatRandomStage stage)
    {
        if (targetOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetOrder));
        }

        var seed = eventId;
        seed ^= unchecked(TargetSalt * ((ulong)(uint)targetOrder + 1UL));
        seed ^= unchecked(StageSalt * ((ulong)stage + 1UL));
        var random = SplitMix64(seed);
        return (int)(((UInt128)random * AuthoredCombatFormula.BasisPointScale) >> 64);
    }

    private static ulong SplitMix64(ulong seed)
    {
        var value = unchecked(seed + 0x9E3779B97F4A7C15UL);
        value = unchecked((value ^ (value >> 30)) *
                          0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) *
                          0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }
}
