using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal static partial class ElementalResonanceExecutionPolicy
{
    public static ResonancePassiveAdjustment ApplyPassiveBonuses(
        ElementalEquipmentProfile profile,
        long maximumHealth,
        long movementSpeed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (maximumHealth < 0 || movementSpeed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHealth));
        }

        if (TryGetParameters<StatBonusParameters>(
                profile,
                ElementalResonanceEffectKind.GaiaMaximumHealth,
                out var health))
        {
            maximumHealth = ElementalBasisPointMath.ScaleUp(
                maximumHealth,
                health.StatBasisPoints);
        }

        if (TryGetParameters<StatBonusParameters>(
                profile,
                ElementalResonanceEffectKind.AeolusMovementSpeed,
                out var speed))
        {
            movementSpeed = ElementalBasisPointMath.ScaleUp(
                movementSpeed,
                speed.StatBasisPoints);
        }

        return new(maximumHealth, movementSpeed);
    }

    private static bool TryGetParameters<TParameters>(
        ElementalEquipmentProfile profile,
        ElementalResonanceEffectKind effect,
        out TParameters parameters)
        where TParameters : ElementalResonanceParameters
    {
        foreach (var tier in profile.ActiveResonanceTiers.Values
                     .SelectMany(static value => value)
                     .Where(value => value.Effect == effect)
                     .OrderByDescending(static value => value.RequiredPieces))
        {
            if (tier.Parameters is TParameters typed)
            {
                parameters = typed;
                return true;
            }
        }

        parameters = null!;
        return false;
    }

    private static ElementalExecutionLimits? ResolveLimits(
        DeterministicCombatEventContext combatEvent)
    {
        if (!combatEvent.IsPvp)
        {
            return ElementalExecutionLimits.CurrentPve;
        }

        if (!combatEvent.PvpEligibility.Admits(
                combatEvent.SourceCharacterId,
                combatEvent.TargetCharacterId,
                combatEvent.MapId))
        {
            return null;
        }

        return ElementalExecutionLimits.FromPvp(
            combatEvent.PvpEligibility.Caps);
    }

    private static long CappedPortionOfAppliedHit(
        long appliedDamage,
        int authoredBasisPoints,
        ElementalExecutionLimits limits)
    {
        var basisPoints = ElementalBasisPointMath.ClampBasisPoints(
            authoredBasisPoints,
            limits.MaximumTriggeredDamageBasisPointsOfAppliedHit);
        return ElementalBasisPointMath.Portion(appliedDamage, basisPoints);
    }

    private static bool IsOwnedSource(
        DeterministicCombatEventContext combatEvent,
        ElementalResonanceState state) =>
        combatEvent.SourceCharacterId == state.OwnerCharacterId;

    private static bool IsOwnedTarget(
        DeterministicCombatEventContext combatEvent,
        ElementalResonanceState state) =>
        combatEvent.TargetCharacterId == state.OwnerCharacterId;
}
