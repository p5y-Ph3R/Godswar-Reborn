using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal static partial class ElementalResonanceExecutionPolicy
{
    public static ResonanceMovementResult ProcessAcceptedMovement(
        DeterministicCombatEventContext movementEvent,
        ElementalEquipmentProfile profile,
        ElementalResonanceState state,
        long acceptedDistanceMillimeters)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        if (!movementEvent.IsAcceptedMovement ||
            movementEvent.SourceCharacterId != state.OwnerCharacterId ||
            acceptedDistanceMillimeters < 0)
        {
            return new(acceptedDistanceMillimeters, false, 0);
        }

        state.Reconcile(profile);
        if (!state.TryAccept(movementEvent, ResonanceEventPhase.Movement) ||
            !TryGetParameters<MomentumParameters>(
                profile,
                ElementalResonanceEffectKind.AeolusMomentum,
                out var momentum))
        {
            return new(acceptedDistanceMillimeters, false, 0);
        }

        return state.AcceptMovement(
            acceptedDistanceMillimeters,
            movementEvent.AuthoritativeTimeMilliseconds,
            momentum);
    }

    public static ResonanceRecoveryResult ProcessRecoveryPulse(
        DeterministicCombatEventContext recoveryEvent,
        ElementalEquipmentProfile profile,
        ElementalResonanceState state,
        long requestedHealth,
        long requestedMana,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        if (!IsRecoveryEvent(recoveryEvent, state) ||
            requestedHealth < 0 ||
            requestedMana < 0 ||
            !HasValidResources(
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana))
        {
            return default;
        }

        state.Reconcile(profile);
        if (!state.TryAccept(recoveryEvent, ResonanceEventPhase.Recovery))
        {
            return default;
        }

        return ApplyRecovery(
            profile,
            state,
            requestedHealth,
            requestedMana,
            currentHealth,
            currentMana,
            maximumHealth,
            maximumMana);
    }

    /// <summary>
    /// Resolves one server-admitted six-second recovery pulse as one atomic
    /// resonance event. Authored order is base recovery plus Poseidon, Apollo
    /// amplification, Dark Wither received-healing reduction, resource clamp,
    /// then Apollo overheal conversion.
    /// </summary>
    public static ResonanceRecoveryResult ProcessAuthoritativeRecoveryPulse(
        DeterministicCombatEventContext recoveryEvent,
        ElementalEquipmentProfile profile,
        ElementalResonanceState state,
        ElementalStatusState statuses,
        long baseRequestedHealth,
        long baseRequestedMana,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(statuses);
        if (!IsRecoveryEvent(recoveryEvent, state) ||
            statuses.OwnerCharacterId != state.OwnerCharacterId ||
            baseRequestedHealth < 0 ||
            baseRequestedMana < 0 ||
            !HasValidResources(
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana))
        {
            return default;
        }

        state.Reconcile(profile);
        if (!state.TryAccept(recoveryEvent, ResonanceEventPhase.Recovery))
        {
            return default;
        }

        var requestedHealth = baseRequestedHealth;
        var requestedMana = baseRequestedMana;
        if (TryGetParameters<PeriodicMaxResourceRecoveryParameters>(
                profile,
                ElementalResonanceEffectKind.PoseidonRecoveryPulse,
                out var poseidon))
        {
            // This entry point is called only by the already-admitted
            // six-second recovery clock. Do not introduce a second timer that
            // would delay Poseidon's first pulse or halve its live cadence.
            requestedHealth = checked(requestedHealth +
                ElementalBasisPointMath.Portion(
                    maximumHealth,
                    poseidon.MaximumHealthBasisPoints));
            requestedMana = checked(requestedMana +
                ElementalBasisPointMath.Portion(
                    maximumMana,
                    poseidon.MaximumManaBasisPoints));
        }

        if (TryGetParameters<RecoveryPulseAmplificationParameters>(
                profile,
                ElementalResonanceEffectKind.ApolloRecovery,
                out var apollo))
        {
            requestedHealth = ElementalBasisPointMath.ScaleUp(
                requestedHealth,
                apollo.RecoveryBasisPoints);
            requestedMana = ElementalBasisPointMath.ScaleUp(
                requestedMana,
                apollo.RecoveryBasisPoints);
        }

        requestedHealth = statuses.ApplyAdjustments(
            recoveryEvent.AuthoritativeTimeMilliseconds,
            movementSpeed: 0,
            physicalDefense: 0,
            magicDefense: 0,
            hitRating: 0,
            healingReceived: requestedHealth).HealingReceived;

        var appliedHealth = Math.Min(
            requestedHealth,
            maximumHealth - currentHealth);
        var appliedMana = Math.Min(
            requestedMana,
            maximumMana - currentMana);
        var barrierAdded = 0L;
        var overheal = checked(requestedHealth - appliedHealth);
        if (overheal > 0 &&
            TryGetParameters<OverhealBarrierParameters>(
                profile,
                ElementalResonanceEffectKind.ApolloBarrier,
                out var barrier))
        {
            barrierAdded = state.AddBarrier(
                ElementalBasisPointMath.Portion(
                    overheal,
                    barrier.OverhealBasisPoints),
                maximumHealth,
                barrier.MaximumHealthCapBasisPoints);
        }

        return new(
            requestedHealth,
            requestedMana,
            appliedHealth,
            appliedMana,
            barrierAdded,
            state.Barrier);
    }

    public static ResonanceRecoveryResult ProcessPeriodicRecovery(
        DeterministicCombatEventContext recoveryEvent,
        ElementalEquipmentProfile profile,
        ElementalResonanceState state,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        if (!IsRecoveryEvent(recoveryEvent, state) ||
            !HasValidResources(
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana))
        {
            return default;
        }

        state.Reconcile(profile);
        if (!TryGetParameters<PeriodicMaxResourceRecoveryParameters>(
                profile,
                ElementalResonanceEffectKind.PoseidonRecoveryPulse,
                out var poseidon) ||
            !state.TryAccept(recoveryEvent, ResonanceEventPhase.Recovery) ||
            !state.TryOpenPeriodicRecovery(
                ElementKind.Water,
                recoveryEvent.AuthoritativeTimeMilliseconds,
                poseidon.IntervalMilliseconds))
        {
            return default;
        }

        var requestedHealth = ElementalBasisPointMath.Portion(
            maximumHealth,
            poseidon.MaximumHealthBasisPoints);
        var requestedMana = ElementalBasisPointMath.Portion(
            maximumMana,
            poseidon.MaximumManaBasisPoints);
        return ApplyRecovery(
            profile,
            state,
            requestedHealth,
            requestedMana,
            currentHealth,
            currentMana,
            maximumHealth,
            maximumMana);
    }

    public static ResonanceRecoveryResult ProcessCreditedKill(
        DeterministicCombatEventContext killEvent,
        ElementalEquipmentProfile profile,
        ElementalResonanceState state,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(state);
        if (!killEvent.IsValid ||
            !killEvent.Committed ||
            killEvent.Provenance != CombatEventProvenance.CreditedKill ||
            !IsOwnedSource(killEvent, state) ||
            !HasValidResources(
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana))
        {
            return default;
        }

        state.Reconcile(profile);
        if (!TryGetParameters<KillResourceRestorationParameters>(
                profile,
                ElementalResonanceEffectKind.HadesKillRestoration,
                out var restoration) ||
            !state.TryAccept(killEvent, ResonanceEventPhase.Kill))
        {
            return default;
        }

        var requestedHealth = ElementalBasisPointMath.Portion(
            maximumHealth,
            restoration.MaximumHealthBasisPoints);
        var requestedMana = ElementalBasisPointMath.Portion(
            maximumMana,
            restoration.MaximumManaBasisPoints);
        return new ResonanceRecoveryResult(
            requestedHealth,
            requestedMana,
            Math.Min(requestedHealth, maximumHealth - currentHealth),
            Math.Min(requestedMana, maximumMana - currentMana),
            0,
            state.Barrier);
    }

    private static ResonanceRecoveryResult ApplyRecovery(
        ElementalEquipmentProfile profile,
        ElementalResonanceState state,
        long requestedHealth,
        long requestedMana,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana)
    {
        if (TryGetParameters<RecoveryPulseAmplificationParameters>(
                profile,
                ElementalResonanceEffectKind.ApolloRecovery,
                out var apollo))
        {
            requestedHealth = ElementalBasisPointMath.ScaleUp(
                requestedHealth,
                apollo.RecoveryBasisPoints);
            requestedMana = ElementalBasisPointMath.ScaleUp(
                requestedMana,
                apollo.RecoveryBasisPoints);
        }

        var appliedHealth = Math.Min(
            requestedHealth,
            maximumHealth - currentHealth);
        var appliedMana = Math.Min(
            requestedMana,
            maximumMana - currentMana);
        var barrierAdded = 0L;
        var overheal = checked(requestedHealth - appliedHealth);
        if (overheal > 0 &&
            TryGetParameters<OverhealBarrierParameters>(
                profile,
                ElementalResonanceEffectKind.ApolloBarrier,
                out var barrier))
        {
            var converted = ElementalBasisPointMath.Portion(
                overheal,
                barrier.OverhealBasisPoints);
            barrierAdded = state.AddBarrier(
                converted,
                maximumHealth,
                barrier.MaximumHealthCapBasisPoints);
        }

        return new ResonanceRecoveryResult(
            requestedHealth,
            requestedMana,
            appliedHealth,
            appliedMana,
            barrierAdded,
            state.Barrier);
    }

    private static bool IsRecoveryEvent(
        DeterministicCombatEventContext recoveryEvent,
        ElementalResonanceState state) =>
        recoveryEvent.IsValid &&
        recoveryEvent.Committed &&
        recoveryEvent.Provenance == CombatEventProvenance.Recovery &&
        recoveryEvent.SourceCharacterId == state.OwnerCharacterId &&
        recoveryEvent.TargetCharacterId == state.OwnerCharacterId;

    private static bool HasValidResources(
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana) =>
        maximumHealth > 0 &&
        maximumMana >= 0 &&
        currentHealth is >= 0 && currentHealth <= maximumHealth &&
        currentMana is >= 0 && currentMana <= maximumMana;
}
