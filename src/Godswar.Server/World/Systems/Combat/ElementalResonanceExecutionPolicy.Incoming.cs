using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal static partial class ElementalResonanceExecutionPolicy
{
    public static IncomingResonanceAdjustment AdjustIncomingDirectDamage(
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile targetProfile,
        ElementalResonanceState targetState,
        long originalDamage,
        long currentHealth,
        long maximumHealth,
        long maximumMana)
    {
        ArgumentNullException.ThrowIfNull(targetProfile);
        ArgumentNullException.ThrowIfNull(targetState);
        if (!combatEvent.IsDirectAttempt ||
            !IsOwnedTarget(combatEvent, targetState) ||
            originalDamage < 0 ||
            maximumHealth <= 0 ||
            maximumMana < 0 ||
            currentHealth <= 0 ||
            currentHealth > maximumHealth)
        {
            return UnchangedIncoming(originalDamage, currentHealth);
        }

        var limits = ResolveLimits(combatEvent);
        if (!limits.HasValue)
        {
            return UnchangedIncoming(originalDamage, currentHealth);
        }

        targetState.Reconcile(targetProfile);
        if (!targetState.TryAccept(
                combatEvent,
                ResonanceEventPhase.IncomingPreResolution))
        {
            return UnchangedIncoming(originalDamage, currentHealth);
        }

        var windCount = 0;
        var hasEvasion = TryGetParameters<IncomingHitEvasionParameters>(
            targetProfile,
            ElementalResonanceEffectKind.AeolusEvasion,
            out var evasion);
        if (hasEvasion)
        {
            windCount = targetState.AdvanceIncomingHit(ElementKind.Wind);
        }

        if (hasEvasion && windCount % evasion.EveryIncomingHit == 0)
        {
            return new IncomingResonanceAdjustment(
                originalDamage,
                0,
                originalDamage,
                currentHealth,
                Evaded: true,
                PoseidonGuardApplied: false,
                ApolloLethalProtectionApplied: false,
                ConsumedBarrier: 0,
                GuardHealthRecovery: 0,
                GuardManaRecovery: 0);
        }

        var waterCount = 0;
        var hasGuard = TryGetParameters<IncomingHitGuardParameters>(
            targetProfile,
            ElementalResonanceEffectKind.PoseidonFifthHitGuard,
            out var guard);
        if (hasGuard)
        {
            waterCount = targetState.AdvanceIncomingHit(ElementKind.Water);
        }

        var adjusted = originalDamage;
        if (TryGetParameters<IncomingDamageMitigationParameters>(
                targetProfile,
                ElementalResonanceEffectKind.GaiaMitigation,
                out var gaia))
        {
            adjusted = ElementalBasisPointMath.ScaleDown(
                adjusted,
                gaia.FinalDamageReductionBasisPoints);
        }

        var guardApplied = hasGuard &&
            waterCount % guard.EveryIncomingDirectHit == 0;
        var beforeGuard = adjusted;
        if (guardApplied)
        {
            adjusted = ElementalBasisPointMath.ScaleDown(
                adjusted,
                guard.DamageReductionBasisPoints);
        }

        var guardPrevented = checked(beforeGuard - adjusted);
        var consumedBarrier = 0L;
        var lethalProtection = false;
        if (adjusted >= currentHealth &&
            targetState.Barrier > 0 &&
            TryGetParameters<LethalBarrierParameters>(
                targetProfile,
                ElementalResonanceEffectKind.ApolloLethalProtection,
                out var lethal) &&
            (!lethal.RequiresBarrier || targetState.Barrier > 0))
        {
            if (lethal.ConsumeBarrier)
            {
                consumedBarrier = targetState.ConsumeBarrier();
            }

            adjusted = Math.Max(
                0,
                checked(currentHealth - lethal.RemainingHealthPoints));
            lethalProtection = true;
        }

        var healthRecovery = 0L;
        var manaRecovery = 0L;
        if (guardApplied && guardPrevented > 0 &&
            TryGetParameters<PreventedDamageRecoveryParameters>(
                targetProfile,
                ElementalResonanceEffectKind.PoseidonGuardRecovery,
                out var recovery))
        {
            healthRecovery = CappedRecovery(
                guardPrevented,
                recovery.HealthRecoveryBasisPoints,
                maximumHealth,
                recovery.MaximumHealthCapBasisPoints,
                limits.Value);
            manaRecovery = CappedRecovery(
                guardPrevented,
                recovery.ManaRecoveryBasisPoints,
                maximumMana,
                recovery.MaximumManaCapBasisPoints,
                limits.Value);
        }

        return new IncomingResonanceAdjustment(
            originalDamage,
            adjusted,
            checked(originalDamage - adjusted),
            Math.Max(0, checked(currentHealth - adjusted)),
            Evaded: false,
            guardApplied,
            lethalProtection,
            consumedBarrier,
            healthRecovery,
            manaRecovery);
    }

    public static ResonanceDamageIntent? PlanCommittedReflection(
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile targetProfile,
        ElementalResonanceState targetState,
        long postMitigationAppliedDamage,
        long attackerMaximumHealth)
    {
        ArgumentNullException.ThrowIfNull(targetProfile);
        ArgumentNullException.ThrowIfNull(targetState);
        if (!combatEvent.IsCommittedDirectHit ||
            !IsOwnedTarget(combatEvent, targetState) ||
            postMitigationAppliedDamage <= 0 ||
            attackerMaximumHealth <= 0)
        {
            return null;
        }

        var limits = ResolveLimits(combatEvent);
        if (!limits.HasValue)
        {
            return null;
        }

        targetState.Reconcile(targetProfile);
        if (!TryGetParameters<ReflectionParameters>(
                targetProfile,
                ElementalResonanceEffectKind.GaiaReflection,
                out var reflection) ||
            reflection.CanTriggerReflection ||
            !targetState.TryAccept(
                combatEvent,
                ResonanceEventPhase.PostCommit))
        {
            return null;
        }

        var reflected = ElementalBasisPointMath.Portion(
            postMitigationAppliedDamage,
            reflection.PostMitigationDamageBasisPoints);
        var capBasisPoints = Math.Min(
            reflection.AttackerMaximumHealthCapBasisPoints,
            limits.Value.MaximumReflectionBasisPointsOfAttackerMaximumHealth);
        var maximum = ElementalBasisPointMath.Portion(
            attackerMaximumHealth,
            capBasisPoints);
        reflected = Math.Min(reflected, maximum);
        if (reflected <= 0)
        {
            return null;
        }

        return new ResonanceDamageIntent(
            ResonanceDamageKind.GaiaReflection,
            combatEvent.TargetCharacterId,
            combatEvent.SourceCharacterId,
            combatEvent.EventId,
            reflected,
            CombatEventProvenance.Reflection);
    }

    private static IncomingResonanceAdjustment UnchangedIncoming(
        long damage,
        long currentHealth) =>
        new(
            damage,
            damage,
            0,
            Math.Max(0, currentHealth - Math.Max(0, damage)),
            false,
            false,
            false,
            0,
            0,
            0);

    private static long CappedRecovery(
        long preventedDamage,
        int recoveryBasisPoints,
        long maximumResource,
        int authoredCapBasisPoints,
        ElementalExecutionLimits limits)
    {
        var requested = ElementalBasisPointMath.Portion(
            preventedDamage,
            recoveryBasisPoints);
        var capBasisPoints = Math.Min(
            authoredCapBasisPoints,
            limits.MaximumResourceEffectBasisPointsOfMaximum);
        return Math.Min(
            requested,
            ElementalBasisPointMath.Portion(
                maximumResource,
                capBasisPoints));
    }
}
