using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal static partial class ElementalResonanceExecutionPolicy
{
    public static OutgoingResonanceAdjustment AdjustOutgoingDirectDamage(
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile sourceProfile,
        ElementalResonanceState sourceState,
        long originalDamage,
        long targetCurrentHealth,
        long targetMaximumHealth,
        ulong momentumReservationScopeId = 0)
    {
        ArgumentNullException.ThrowIfNull(sourceProfile);
        ArgumentNullException.ThrowIfNull(sourceState);
        if (!combatEvent.IsDirectAttempt ||
            !IsOwnedSource(combatEvent, sourceState) ||
            originalDamage < 0 ||
            targetMaximumHealth <= 0 ||
            targetCurrentHealth < 0 ||
            targetCurrentHealth > targetMaximumHealth)
        {
            return new(originalDamage, originalDamage, false, false);
        }

        sourceState.Reconcile(sourceProfile);
        if (!sourceState.TryAccept(
                combatEvent,
                ResonanceEventPhase.OutgoingPreResolution))
        {
            return new(originalDamage, originalDamage, false, false);
        }

        var adjusted = originalDamage;
        var executeApplied = false;
        if (adjusted > 0 &&
            TryGetParameters<LowHealthDamageParameters>(
                sourceProfile,
                ElementalResonanceEffectKind.HadesExecute,
                out var execute) &&
            ((decimal)targetCurrentHealth *
                ElementalBasisPointMath.Denominator) <
            ((decimal)targetMaximumHealth *
                execute.TargetHealthThresholdBasisPoints))
        {
            adjusted = ElementalBasisPointMath.ScaleUp(
                adjusted,
                execute.DamageBasisPoints);
            executeApplied = true;
        }

        var momentumPending = false;
        if (adjusted > 0 &&
            TryGetParameters<MomentumParameters>(
                sourceProfile,
                ElementalResonanceEffectKind.AeolusMomentum,
                out var momentum) &&
            sourceState.TryReserveMomentum(
                momentumReservationScopeId == 0
                    ? combatEvent.EventId
                    : momentumReservationScopeId,
                combatEvent.EventId,
                combatEvent.TargetCharacterId,
                combatEvent.AuthoritativeTimeMilliseconds))
        {
            momentumPending = true;
            adjusted = ElementalBasisPointMath.ScaleUp(
                adjusted,
                momentum.NextHitDamageBasisPoints);
        }

        return new(
            originalDamage,
            adjusted,
            executeApplied,
            momentumPending);
    }

    public static ResonancePostCommitResult ProcessCommittedDirectHit(
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile sourceProfile,
        ElementalResonanceState sourceState,
        ElementalStatusState targetStatuses,
        long appliedDirectDamage,
        long sourceMaximumHealth,
        bool primaryTargetIsBoss,
        IEnumerable<ResonanceTargetCandidate> additionalTargets)
    {
        ArgumentNullException.ThrowIfNull(sourceProfile);
        ArgumentNullException.ThrowIfNull(sourceState);
        ArgumentNullException.ThrowIfNull(targetStatuses);
        ArgumentNullException.ThrowIfNull(additionalTargets);
        var empty = new ResonancePostCommitResult([], [], 0, 0, false, false, 0);
        if (!combatEvent.IsCommittedDirectHit ||
            !IsOwnedSource(combatEvent, sourceState) ||
            targetStatuses.OwnerCharacterId != combatEvent.TargetCharacterId ||
            appliedDirectDamage <= 0 ||
            sourceMaximumHealth <= 0)
        {
            return empty;
        }

        var limits = ResolveLimits(combatEvent);
        if (!limits.HasValue)
        {
            return empty;
        }

        sourceState.Reconcile(sourceProfile);
        if (!sourceState.TryAccept(
                combatEvent,
                ResonanceEventPhase.PostCommit))
        {
            return empty;
        }

        var damage = new List<ResonanceDamageIntent>(4);
        var controls = new List<ResonanceControlIntent>(1);
        var burnApplied = false;
        var burnDetonated = false;
        var detonatedDamage = 0L;

        if (TryGetParameters<BurnParameters>(
                sourceProfile,
                ElementalResonanceEffectKind.PrometheusBurn,
                out var burn))
        {
            var fireHit = sourceState.AdvanceOutgoingHit(ElementKind.Fire);
            if (TryGetParameters<DetonationParameters>(
                    sourceProfile,
                    ElementalResonanceEffectKind.PrometheusDetonation,
                    out var detonation) &&
                fireHit % detonation.EveryCommittedDirectHit == 0)
            {
                var remaining = detonation.DetonateRemainingBurn
                    ? targetStatuses.ConsumeRemainingBurn(
                        combatEvent.AuthoritativeTimeMilliseconds)
                    : 0;
                var triggering = CappedPortionOfAppliedHit(
                    appliedDirectDamage,
                    detonation.TriggeringHitDamageBasisPoints,
                    limits.Value);
                detonatedDamage = checked(remaining + triggering);
                if (detonatedDamage > 0)
                {
                    burnDetonated = true;
                    damage.Add(new ResonanceDamageIntent(
                        ResonanceDamageKind.PrometheusDetonation,
                        combatEvent.SourceCharacterId,
                        combatEvent.TargetCharacterId,
                        combatEvent.EventId,
                        detonatedDamage,
                        CombatEventProvenance.Resonance));
                }
            }

            if (!burnDetonated ||
                !TryGetParameters<DetonationParameters>(
                    sourceProfile,
                    ElementalResonanceEffectKind.PrometheusDetonation,
                    out var fireTen) ||
                fireTen.ReapplyBurn)
            {
                burnApplied = TryApplyResonanceBurn(
                    combatEvent,
                    targetStatuses,
                    burn,
                    appliedDirectDamage);
            }
        }

        ProcessLightning(
            combatEvent,
            sourceProfile,
            sourceState,
            appliedDirectDamage,
            primaryTargetIsBoss,
            additionalTargets,
            limits.Value,
            damage,
            controls);

        if (TryGetParameters<MomentumParameters>(
                sourceProfile,
                ElementalResonanceEffectKind.AeolusMomentum,
                out var momentum) &&
            momentum.ConsumeOnHit)
        {
            sourceState.CommitMomentumReservation(
                combatEvent.EventId,
                combatEvent.TargetCharacterId,
                combatEvent.AuthoritativeTimeMilliseconds);
        }

        var healing = 0L;
        if (TryGetParameters<AppliedDamageHealingParameters>(
                sourceProfile,
                ElementalResonanceEffectKind.HadesLifeSteal,
                out var lifeSteal))
        {
            var requested = ElementalBasisPointMath.Portion(
                appliedDirectDamage,
                lifeSteal.AppliedDamageBasisPoints);
            var perHitCap = ElementalBasisPointMath.Portion(
                sourceMaximumHealth,
                lifeSteal.MaximumHealthCapPerHitBasisPoints);
            healing = Math.Min(requested, perHitCap);
        }

        return new(
            damage.AsReadOnly(),
            controls.AsReadOnly(),
            healing,
            0,
            burnApplied,
            burnDetonated,
            detonatedDamage);
    }

    private static bool TryApplyResonanceBurn(
        DeterministicCombatEventContext combatEvent,
        ElementalStatusState targetStatuses,
        BurnParameters burn,
        long appliedDirectDamage)
    {
        var total = ElementalBasisPointMath.Portion(
            appliedDirectDamage,
            burn.TotalDamageBasisPoints);
        if (total <= 0)
        {
            return false;
        }

        var ticks = Math.Min(
            burn.TickCount,
            checked((int)Math.Min(total, int.MaxValue)));
        return targetStatuses.TryApply(new ElementalEffectApplication(
            ElementKind.Fire,
            ElementalEffectKind.Burn,
            combatEvent.SourceCharacterId,
            combatEvent.TargetCharacterId,
            combatEvent.EventId,
            combatEvent.AuthoritativeTimeMilliseconds,
            checked(combatEvent.AuthoritativeTimeMilliseconds +
                burn.DurationMilliseconds),
            burn.TotalDamageBasisPoints,
            ElementalBasisPointMath.Denominator,
            0,
            total,
            ticks,
            CombatEventProvenance.Resonance));
    }

    private static void ProcessLightning(
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile profile,
        ElementalResonanceState state,
        long appliedDamage,
        bool primaryTargetIsBoss,
        IEnumerable<ResonanceTargetCandidate> candidates,
        ElementalExecutionLimits limits,
        ICollection<ResonanceDamageIntent> damage,
        ICollection<ResonanceControlIntent> controls)
    {
        if (!TryGetParameters<TriggeredDirectDamageParameters>(
                profile,
                ElementalResonanceEffectKind.ZeusBolt,
                out var bolt))
        {
            return;
        }

        var lightningHit = state.AdvanceOutgoingHit(ElementKind.Lightning);
        if (lightningHit % bolt.EveryCommittedDirectHit == 0)
        {
            AddDamage(
                damage,
                ResonanceDamageKind.ZeusBolt,
                combatEvent,
                combatEvent.TargetCharacterId,
                CappedPortionOfAppliedHit(
                    appliedDamage,
                    bolt.AppliedDamageBasisPoints,
                    limits));
        }

        if (!TryGetParameters<ChainDamageParameters>(
                profile,
                ElementalResonanceEffectKind.ZeusChain,
                out var chain))
        {
            return;
        }

        var admitted = candidates
            .Where(value =>
                value.MapId == combatEvent.MapId &&
                value.TargetId != combatEvent.TargetCharacterId &&
                value.DistanceMillimeters <= chain.RangeMillimeters &&
                value.IsAdmitted(combatEvent.SourceCharacterId))
            .OrderBy(static value => value.DistanceMillimeters)
            .ThenBy(static value => value.TargetId)
            .ToArray();
        var firstIndex = chain.AdditionalTargetOrdinal - 1;
        if (firstIndex >= 0 && firstIndex < admitted.Length)
        {
            AddDamage(
                damage,
                ResonanceDamageKind.ZeusChain,
                combatEvent,
                admitted[firstIndex].TargetId,
                CappedPortionOfAppliedHit(
                    appliedDamage,
                    chain.OriginalAppliedHitBasisPoints,
                    limits));
        }

        if (!TryGetParameters<StormCrownParameters>(
                profile,
                ElementalResonanceEffectKind.ZeusStormCrown,
                out var storm))
        {
            return;
        }

        var secondIndex = storm.AdditionalTargetOrdinal - 1;
        if (secondIndex >= 0 && secondIndex < admitted.Length)
        {
            AddDamage(
                damage,
                ResonanceDamageKind.ZeusStormCrown,
                combatEvent,
                admitted[secondIndex].TargetId,
                CappedPortionOfAppliedHit(
                    appliedDamage,
                    storm.OriginalAppliedHitBasisPoints,
                    limits));
        }

        if (!primaryTargetIsBoss)
        {
            controls.Add(new ResonanceControlIntent(
                combatEvent.SourceCharacterId,
                combatEvent.TargetCharacterId,
                combatEvent.EventId,
                Math.Min(
                    storm.PrimaryNonBossStunMilliseconds,
                    limits.MaximumStatusDurationMilliseconds),
                CombatEventProvenance.Resonance));
        }
    }

    private static void AddDamage(
        ICollection<ResonanceDamageIntent> intents,
        ResonanceDamageKind kind,
        DeterministicCombatEventContext combatEvent,
        long targetId,
        long damage)
    {
        if (damage > 0)
        {
            intents.Add(new ResonanceDamageIntent(
                kind,
                combatEvent.SourceCharacterId,
                targetId,
                combatEvent.EventId,
                damage,
                CombatEventProvenance.Resonance));
        }
    }
}
