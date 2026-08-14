namespace Godswar.Server.World.Systems.Combat;

internal enum CombatDamageProvenance : byte
{
    Direct = 1,
    Rebound = 2
}

/// <summary>
/// Post-damage effects calculated from damage that was actually committed.
/// Returned healing still needs to be capped by the source's missing HP.
/// Rebound damage is explicitly tagged non-recursive by provenance.
/// </summary>
internal readonly record struct CombatSecondaryEffectResolution(
    uint LifeAbsorptionHealing,
    uint ReboundDamage,
    CombatDamageProvenance ReboundProvenance)
{
    public bool HasEffects => LifeAbsorptionHealing > 0 || ReboundDamage > 0;
}

internal static class CombatSecondaryEffectPolicy
{
    public const int MaximumLifeAbsorptionBasisPoints = 10_000;
    public const int MaximumDamageReboundBasisPoints = 10_000;

    public static CombatSecondaryEffectResolution Resolve(
        uint committedDamage,
        in CombatAttackerStats attacker,
        in CombatTargetStats target,
        CombatDamageProvenance provenance = CombatDamageProvenance.Direct)
    {
        if (provenance != CombatDamageProvenance.Direct ||
            committedDamage == 0)
        {
            return new CombatSecondaryEffectResolution(
                0,
                0,
                CombatDamageProvenance.Rebound);
        }

        var healing = ScaleCommittedDamage(
            committedDamage,
            attacker.LifeAbsorptionBasisPoints,
            MaximumLifeAbsorptionBasisPoints);
        var reboundPercent = ScaleCommittedDamage(
            committedDamage,
            target.DamageReboundBasisPoints,
            MaximumDamageReboundBasisPoints);
        var rebound = Math.Min(
            (ulong)uint.MaxValue,
            (ulong)reboundPercent +
            (uint)Math.Max(0, target.DamageReboundFlat));
        return new CombatSecondaryEffectResolution(
            healing,
            (uint)rebound,
            CombatDamageProvenance.Rebound);
    }

    public static int ClampLifeAbsorptionToMissingHealth(
        uint requestedHealing,
        int currentHealth,
        int maximumHealth)
    {
        if (requestedHealing == 0 ||
            currentHealth <= 0 ||
            maximumHealth <= currentHealth)
        {
            return 0;
        }

        return checked((int)Math.Min(
            requestedHealing,
            (uint)(maximumHealth - currentHealth)));
    }

    private static uint ScaleCommittedDamage(
        uint committedDamage,
        int basisPoints,
        int maximumBasisPoints)
    {
        var bounded = (uint)Math.Clamp(
            basisPoints,
            0,
            maximumBasisPoints);
        return (uint)((ulong)committedDamage * bounded / 10_000UL);
    }
}
