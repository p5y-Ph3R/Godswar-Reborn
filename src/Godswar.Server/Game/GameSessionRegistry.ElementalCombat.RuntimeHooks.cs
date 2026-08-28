using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryGetElementalStatusAdjustment(
        ClientSession session,
        ElementalCombatSessionFence fence,
        long authoritativeTimeMilliseconds,
        long movementSpeed,
        long physicalDefense,
        long magicDefense,
        long hitRating,
        long healingReceived,
        out ElementalStatusAdjustment adjustment)
    {
        adjustment = default;
        if (!TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            adjustment = state.Statuses.ApplyAdjustments(
                authoritativeTimeMilliseconds,
                movementSpeed,
                physicalDefense,
                magicDefense,
                hitRating,
                healingReceived);
            return true;
        }
    }

    private bool TryPreviewElementalStatusAdjustment(
        ClientSession session,
        ElementalCombatSessionFence fence,
        long authoritativeTimeMilliseconds,
        long movementSpeed,
        long physicalDefense,
        long magicDefense,
        long hitRating,
        long healingReceived,
        out ElementalStatusAdjustment adjustment)
    {
        adjustment = default;
        if (!TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            adjustment = state.Statuses.PreviewAdjustments(
                authoritativeTimeMilliseconds,
                movementSpeed,
                physicalDefense,
                magicDefense,
                hitRating,
                healingReceived);
            return true;
        }
    }

    internal bool TryProcessAcceptedElementalMovement(
        ClientSession session,
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext movementEvent,
        ElementalEquipmentProfile profile,
        ElementalEffectExecutionTuning tuning,
        long acceptedDistanceMillimeters,
        long baseMovementSpeed,
        out ElementalMovementHookResult result)
    {
        result = default;
        if (!movementEvent.IsAcceptedMovement ||
            movementEvent.MapId != fence.MapId ||
            movementEvent.SourceCharacterId != fence.CharacterId ||
            !TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            var before = state.Statuses.ApplyAdjustments(
                movementEvent.AuthoritativeTimeMilliseconds,
                baseMovementSpeed,
                0,
                0,
                0,
                0);
            if (!before.MovementAllowed)
            {
                result = new(
                    Accepted: false,
                    ShockBlocked: true,
                    GaleApplied: false,
                    before,
                    default);
                return true;
            }

            var galeApplied =
                ElementalEffectExecutionPolicy.TryPlanMovementApplication(
                    movementEvent,
                    profile,
                    tuning,
                    out var gale) &&
                state.Statuses.TryApply(gale);
            var resonance = ElementalResonanceExecutionPolicy
                .ProcessAcceptedMovement(
                    movementEvent,
                    profile,
                    state.Resonance,
                    acceptedDistanceMillimeters);
            var after = state.Statuses.ApplyAdjustments(
                movementEvent.AuthoritativeTimeMilliseconds,
                baseMovementSpeed,
                0,
                0,
                0,
                0);
            result = new(
                Accepted: true,
                ShockBlocked: false,
                galeApplied,
                after,
                resonance);
            return true;
        }
    }

    internal bool TryProcessElementalRecoveryPulse(
        ClientSession session,
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext recoveryEvent,
        ElementalEquipmentProfile profile,
        long requestedHealth,
        long requestedMana,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana,
        out ResonanceRecoveryResult result)
    {
        result = default;
        if (!IsSelfEvent(fence, recoveryEvent) ||
            !TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            result = ElementalResonanceExecutionPolicy.ProcessRecoveryPulse(
                recoveryEvent,
                profile,
                state.Resonance,
                requestedHealth,
                requestedMana,
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana);
            return true;
        }
    }

    internal bool TryProcessElementalPeriodicRecovery(
        ClientSession session,
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext recoveryEvent,
        ElementalEquipmentProfile profile,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana,
        out ResonanceRecoveryResult result)
    {
        result = default;
        if (!IsSelfEvent(fence, recoveryEvent) ||
            !TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            result = ElementalResonanceExecutionPolicy.ProcessPeriodicRecovery(
                recoveryEvent,
                profile,
                state.Resonance,
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana);
            return true;
        }
    }

    internal bool TryProcessElementalCreditedKill(
        ClientSession session,
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext killEvent,
        ElementalEquipmentProfile profile,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana,
        out ResonanceRecoveryResult result)
    {
        result = default;
        if (!IsSourceEvent(fence, killEvent) ||
            !TryGetElementalCombatSession(session, fence, out var state))
        {
            return false;
        }

        lock (state.Gate)
        {
            result = ElementalResonanceExecutionPolicy.ProcessCreditedKill(
                killEvent,
                profile,
                state.Resonance,
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana);
            return true;
        }
    }

    private static bool IsSelfEvent(
        ElementalCombatSessionFence fence,
        DeterministicCombatEventContext combatEvent) =>
        combatEvent.IsValid &&
        combatEvent.MapId == fence.MapId &&
        combatEvent.SourceCharacterId == fence.CharacterId &&
        combatEvent.TargetCharacterId == fence.CharacterId;
}
