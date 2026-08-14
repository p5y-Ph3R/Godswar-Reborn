using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private ElementalRecoveryCommit ApplyAuthoritativeRecoveryPulseLocked(
        GameSessionContext context,
        DateTimeOffset acceptedAt,
        long baseRequestedHealth,
        long baseRequestedMana)
    {
        var character = context.Character;
        lock (character.VitalsSync)
        {
            if (character.CurrentHp <= 0)
            {
                return default;
            }

            var maximumHealth = Math.Max(1, character.MaxHp);
            var maximumMana = Math.Max(0, character.MaxMp);
            var recovery = ResolveElementalRecoveryLocked(
                context,
                acceptedAt,
                baseRequestedHealth,
                baseRequestedMana,
                character.CurrentHp,
                character.CurrentMp,
                maximumHealth,
                maximumMana,
                out var recoveryRevision,
                out var eventId);
            var nextHealth = checked((int)Math.Min(
                maximumHealth,
                character.CurrentHp + recovery.AppliedHealth));
            var nextMana = checked((int)Math.Min(
                maximumMana,
                character.CurrentMp + recovery.AppliedMana));
            var vitalsChanged =
                nextHealth != character.CurrentHp ||
                nextMana != character.CurrentMp;
            if (vitalsChanged)
            {
                character.CurrentHp = nextHealth;
                character.CurrentMp = nextMana;
                character.MarkVitalsChanged();
            }

            return new(
                PulseAccepted: true,
                vitalsChanged,
                recoveryRevision,
                eventId,
                recovery);
        }
    }

    internal long GetElementalRecoveryRevisionForDiagnostics(
        Godswar.Server.Networking.ClientSession session)
    {
        if (!_elementalCombatSessions.TryGetValue(session, out var state))
        {
            return 0;
        }

        lock (state.Gate)
        {
            return state.RecoveryRevision;
        }
    }

    private ResonanceRecoveryResult ResolveElementalRecoveryLocked(
        GameSessionContext context,
        DateTimeOffset acceptedAt,
        long baseRequestedHealth,
        long baseRequestedMana,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana,
        out long recoveryRevision,
        out ulong eventId)
    {
        recoveryRevision = 0;
        eventId = 0;
        var fence = new ElementalCombatSessionFence(
            context.CharacterId,
            context.MapId,
            context.Ownership);
        if (!fence.IsValid ||
            !TryGetElementalCombatSession(
                context.Session,
                fence,
                out var state))
        {
            return VanillaRecovery(
                baseRequestedHealth,
                baseRequestedMana,
                currentHealth,
                currentMana,
                maximumHealth,
                maximumMana);
        }

        lock (state.Gate)
        {
            recoveryRevision = state.AcceptRecoveryPulse();
            var recoveryEvent = AuthoredElementalCombatV1.RecoveryEvent(
                context.CharacterId,
                context.MapId,
                recoveryRevision,
                acceptedAt);
            eventId = recoveryEvent.EventId;
            return ElementalResonanceExecutionPolicy
                .ProcessAuthoritativeRecoveryPulse(
                    recoveryEvent,
                    context.Character.ElementalEquipment,
                    state.Resonance,
                    state.Statuses,
                    baseRequestedHealth,
                    baseRequestedMana,
                    currentHealth,
                    currentMana,
                    maximumHealth,
                    maximumMana);
        }
    }

    private static ResonanceRecoveryResult VanillaRecovery(
        long requestedHealth,
        long requestedMana,
        long currentHealth,
        long currentMana,
        long maximumHealth,
        long maximumMana) =>
        new(
            requestedHealth,
            requestedMana,
            Math.Min(requestedHealth, maximumHealth - currentHealth),
            Math.Min(requestedMana, maximumMana - currentMana),
            BarrierAdded: 0,
            BarrierTotal: 0);
}
