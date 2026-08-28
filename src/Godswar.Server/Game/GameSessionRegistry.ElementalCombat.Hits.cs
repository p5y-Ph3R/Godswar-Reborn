using Godswar.Server.Application.Characters;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryAdjustElementalOutgoingHit(
        ClientSession session,
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile sourceProfile,
        long originalDamage,
        long targetCurrentHealth,
        long targetMaximumHealth,
        out OutgoingResonanceAdjustment adjustment)
    {
        adjustment = default;
        if (!IsSourceEvent(fence, combatEvent) ||
            !TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            adjustment = ElementalResonanceExecutionPolicy
                .AdjustOutgoingDirectDamage(
                    combatEvent,
                    sourceProfile,
                    state.Resonance,
                    originalDamage,
                    targetCurrentHealth,
                    targetMaximumHealth);
            return true;
        }
    }

    internal bool TryAdjustElementalIncomingHit(
        ClientSession session,
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile targetProfile,
        long originalDamage,
        long currentHealth,
        long maximumHealth,
        long maximumMana,
        out IncomingResonanceAdjustment adjustment)
    {
        adjustment = default;
        if (!IsTargetEvent(fence, combatEvent) ||
            !TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            adjustment = ElementalResonanceExecutionPolicy
                .AdjustIncomingDirectDamage(
                    combatEvent,
                    targetProfile,
                    state.Resonance,
                    originalDamage,
                    currentHealth,
                    maximumHealth,
                    maximumMana);
            return true;
        }
    }

    private bool TryReserveElementalIncomingHit(
        ClientSession session,
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile targetProfile,
        long originalDamage,
        long currentHealth,
        long maximumHealth,
        long maximumMana,
        out IncomingResonanceAdjustment adjustment,
        out ElementalIncomingMutationReservation? reservation)
    {
        adjustment = default;
        reservation = null;
        if (!IsTargetEvent(fence, combatEvent) ||
            !TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        Monitor.Enter(state.Gate);
        ElementalResonanceState.TransactionSnapshot? snapshot = null;
        try
        {
            snapshot = state.Resonance.CaptureTransactionSnapshot();
            adjustment = ElementalResonanceExecutionPolicy
                .AdjustIncomingDirectDamage(
                    combatEvent,
                    targetProfile,
                    state.Resonance,
                    originalDamage,
                    currentHealth,
                    maximumHealth,
                    maximumMana);
            reservation = new(
                state,
                snapshot!);
            return true;
        }
        catch
        {
            if (snapshot is { } captured)
            {
                state.Resonance.RestoreTransactionSnapshot(captured);
            }
            Monitor.Exit(state.Gate);
            throw;
        }
    }

    private sealed class ElementalIncomingMutationReservation
    {
        private readonly ElementalCombatSessionState _state;
        private readonly ElementalResonanceState.TransactionSnapshot
            _snapshot;
        private bool _completed;

        public bool IsCompleted => _completed;

        public ElementalIncomingMutationReservation(
            ElementalCombatSessionState state,
            ElementalResonanceState.TransactionSnapshot snapshot)
        {
            _state = state;
            _snapshot = snapshot;
        }

        public void Commit()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            Monitor.Exit(_state.Gate);
        }

        public void RollBack()
        {
            if (_completed)
            {
                return;
            }

            _state.Resonance.RestoreTransactionSnapshot(_snapshot);
            _completed = true;
            Monitor.Exit(_state.Gate);
        }
    }

    // The caller must invoke this on the target actor's serialized lane. The
    // registry owns the source resonance state but intentionally performs no
    // target discovery or HP mutation.
    internal bool TryProcessCommittedElementalHitOnTargetOwnerLane(
        ClientSession sourceSession,
        ElementalCombatSessionFence sourceFence,
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile sourceProfile,
        ElementalEquipmentProfile targetProfile,
        ElementalStatusState targetStatuses,
        ElementKind? authoredElement,
        ElementalEffectExecutionTuning tuning,
        long appliedDirectDamage,
        long sourceMaximumHealth,
        bool primaryTargetIsBoss,
        IEnumerable<ResonanceTargetCandidate> additionalTargets,
        out ElementalCommittedHitResult result)
    {
        result = default;
        if (!IsSourceEvent(sourceFence, combatEvent) ||
            !combatEvent.IsCommittedDirectHit ||
            targetStatuses.OwnerCharacterId !=
                combatEvent.TargetCharacterId ||
            !TryGetElementalCombatSession(
                sourceSession,
                sourceFence,
                out var sourceState))
        {
            return false;
        }

        lock (sourceState.Gate)
        {
            var committed = ElementalDirectHitCommitPolicy.Commit(
                    combatEvent,
                    sourceProfile,
                    sourceState.Resonance,
                    targetProfile,
                    targetStatuses,
                    authoredElement,
                    tuning,
                    appliedDirectDamage,
                    sourceMaximumHealth,
                    primaryTargetIsBoss,
                    additionalTargets);
            result = new(
                committed.ElementalApplication,
                committed.ElementalApplicationAccepted,
                committed.Resonance);
            return true;
        }
    }

    internal bool TryApplyElementalApplication(
        ClientSession targetSession,
        ElementalCombatSessionFence targetFence,
        DeterministicCombatEventContext combatEvent,
        ElementalEffectApplication application)
    {
        if (!IsTargetEvent(targetFence, combatEvent) ||
            application.SourceCharacterId != combatEvent.SourceCharacterId ||
            application.TargetCharacterId != combatEvent.TargetCharacterId ||
            application.SourceEventId != combatEvent.EventId ||
            !TryGetElementalCombatSession(
                targetSession,
                targetFence,
                out var targetState))
        {
            return false;
        }

        lock (targetState.Gate)
        {
            return targetState.Statuses.TryApply(application);
        }
    }

    internal bool TryPlanCommittedElementalReflection(
        ClientSession targetSession,
        ElementalCombatSessionFence targetFence,
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile targetProfile,
        long postMitigationAppliedDamage,
        long attackerMaximumHealth,
        out ResonanceDamageIntent? reflection)
    {
        reflection = null;
        if (!IsTargetEvent(targetFence, combatEvent) ||
            !TryGetElementalCombatSession(
                targetSession,
                targetFence,
                out var targetState))
        {
            return false;
        }

        lock (targetState.Gate)
        {
            reflection = ElementalResonanceExecutionPolicy
                .PlanCommittedReflection(
                    combatEvent,
                    targetProfile,
                    targetState.Resonance,
                    postMitigationAppliedDamage,
                    attackerMaximumHealth);
            return true;
        }
    }

    internal bool TryCollectDueElementalDamage(
        ClientSession targetSession,
        ElementalCombatSessionFence targetFence,
        long authoritativeTimeMilliseconds,
        out IReadOnlyList<ElementalPeriodicDamageIntent> intents)
    {
        intents = [];
        if (!TryGetElementalCombatSession(
                targetSession,
                targetFence,
                out var targetState))
        {
            return false;
        }

        lock (targetState.Gate)
        {
            intents = targetState.Statuses.CollectDuePeriodicDamage(
                authoritativeTimeMilliseconds);
            return true;
        }
    }

    private static bool IsSourceEvent(
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext combatEvent) =>
        combatEvent.IsValid &&
        combatEvent.MapId == fence.MapId &&
        combatEvent.SourceCharacterId == fence.CharacterId;

    private static bool IsTargetEvent(
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext combatEvent) =>
        combatEvent.IsValid &&
        combatEvent.MapId == fence.MapId &&
        combatEvent.TargetCharacterId == fence.CharacterId;
}
