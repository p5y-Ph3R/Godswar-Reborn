using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal static class ElementalEffectExecutionPolicy
{
    public static bool TryPlanDirectApplication(
        DeterministicCombatEventContext combatEvent,
        ElementKind element,
        ElementalEquipmentProfile sourceProfile,
        ElementalEquipmentProfile targetProfile,
        ElementalEffectExecutionTuning tuning,
        long appliedDirectDamage,
        out ElementalEffectApplication application)
    {
        ArgumentNullException.ThrowIfNull(sourceProfile);
        ArgumentNullException.ThrowIfNull(targetProfile);
        application = default;
        var effect = ElementalAttributeCatalog.EffectFor(element).Effect;
        if (effect == ElementalEffectKind.Gale ||
            !combatEvent.IsCommittedDirectHit ||
            appliedDirectDamage <= 0)
        {
            return false;
        }

        var targetTotals = targetProfile.EffectsFor(element);
        if (element == ElementKind.Water)
        {
            // Defensive Wind is authored as slow resistance. Drench is the
            // hostile slow producer, so the stronger Water/Wind channel wins;
            // they never stack into a second mitigation pass.
            targetTotals = targetTotals with
            {
                EffectResistanceBasisPoints = Math.Max(
                    targetTotals.EffectResistanceBasisPoints,
                    targetProfile.EffectsFor(ElementKind.Wind)
                        .EffectResistanceBasisPoints)
            };
        }

        return TryPlan(
            combatEvent,
            element,
            sourceProfile.EffectsFor(element),
            targetTotals,
            tuning,
            appliedDirectDamage,
            out application);
    }

    public static bool TryPlanMovementApplication(
        DeterministicCombatEventContext movementEvent,
        ElementalEquipmentProfile sourceProfile,
        ElementalEffectExecutionTuning tuning,
        out ElementalEffectApplication application)
    {
        ArgumentNullException.ThrowIfNull(sourceProfile);
        application = default;
        if (!movementEvent.IsAcceptedMovement)
        {
            return false;
        }

        return TryPlan(
            movementEvent,
            ElementKind.Wind,
            sourceProfile.EffectsFor(ElementKind.Wind),
            default,
            tuning,
            appliedDirectDamage: 0,
            out application);
    }

    internal static int DeterministicRollBasisPoints(
        DeterministicCombatEventContext combatEvent,
        ElementKind element,
        uint domain = 0)
    {
        var value = 14695981039346656037UL;
        Mix(ref value, combatEvent.EventId);
        Mix(ref value, unchecked((ulong)combatEvent.MapId));
        Mix(ref value, unchecked((ulong)combatEvent.SourceCharacterId));
        Mix(ref value, unchecked((ulong)combatEvent.TargetCharacterId));
        Mix(ref value, (byte)combatEvent.Provenance);
        Mix(ref value, (byte)element);
        Mix(ref value, domain);
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        value *= 0xc4ceb9fe1a85ec53UL;
        value ^= value >> 33;
        return checked((int)(value % ElementalBasisPointMath.Denominator));
    }

    private static bool TryPlan(
        DeterministicCombatEventContext combatEvent,
        ElementKind element,
        ElementalEffectTotals source,
        ElementalEffectTotals target,
        ElementalEffectExecutionTuning tuning,
        long appliedDirectDamage,
        out ElementalEffectApplication application)
    {
        application = default;
        if (!combatEvent.IsValid)
        {
            return false;
        }

        var limits = ResolveLimits(combatEvent);
        if (!limits.HasValue || !tuning.IsValid(limits.Value))
        {
            return false;
        }

        var cap = limits.Value;
        var chance = ElementalBasisPointMath.ClampBasisPoints(
            source.ApplicationChanceBasisPoints,
            cap.MaximumApplicationChanceBasisPoints);
        if (chance <= 0 ||
            DeterministicRollBasisPoints(combatEvent, element) >= chance)
        {
            return false;
        }

        var potency = ElementalBasisPointMath.ClampBasisPoints(
            source.EffectPotencyBasisPoints,
            cap.MaximumPotencyBasisPoints);
        var resistance = element == ElementKind.Wind
            ? 0
            : ElementalBasisPointMath.ClampBasisPoints(
                target.EffectResistanceBasisPoints,
                cap.MaximumResistanceBasisPoints);
        var effectivePotency = checked((int)(
            ((long)potency *
             (ElementalBasisPointMath.Denominator - resistance)) /
            ElementalBasisPointMath.Denominator));
        if (effectivePotency <= 0)
        {
            return false;
        }

        var effect = ElementalAttributeCatalog.EffectFor(element).Effect;
        var duration = tuning.DurationFor(effect);
        if (effect == ElementalEffectKind.Shock)
        {
            duration = checked((int)ElementalBasisPointMath.Portion(
                duration,
                effectivePotency));
        }

        duration = Math.Clamp(
            duration,
            1,
            cap.MaximumStatusDurationMilliseconds);
        var periodicTotal = effect == ElementalEffectKind.Burn
            ? ElementalBasisPointMath.Portion(
                appliedDirectDamage,
                effectivePotency)
            : 0;
        if (effect == ElementalEffectKind.Burn && periodicTotal <= 0)
        {
            return false;
        }

        var periodicTicks = effect == ElementalEffectKind.Burn
            ? Math.Min(tuning.BurnTickCount, checked((int)Math.Min(
                periodicTotal,
                int.MaxValue)))
            : 0;
        application = new ElementalEffectApplication(
            element,
            effect,
            combatEvent.SourceCharacterId,
            combatEvent.TargetCharacterId,
            combatEvent.EventId,
            combatEvent.AuthoritativeTimeMilliseconds,
            checked(combatEvent.AuthoritativeTimeMilliseconds + duration),
            effectivePotency,
            chance,
            resistance,
            periodicTotal,
            periodicTicks,
            CombatEventProvenance.ElementalStatus);
        return true;
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

    private static void Mix(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
