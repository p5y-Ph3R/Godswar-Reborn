using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal PlayerMonsterDamageEcsDecision
        ResolvePlayerVitalsDamageEcs(
            ClientSession session,
            GameCharacter character,
            uint playerObjectId,
            in PlayerMonsterDamageEcsRequest request,
            Action? beforeLethalCommit = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
        {
            throw new InvalidOperationException(
                "The live player-vitals damage ECS adapter is disabled in Legacy mode.");
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(
                    session,
                    out var context) ||
                !ReferenceEquals(context.Character, character) ||
                context.CharacterId != character.Id ||
                context.ObjectId != playerObjectId)
            {
                throw new InvalidOperationException(
                    "Incoming player damage requires the joined character identity.");
            }

            if (!_playerLifeRevisions.TryGetValue(
                    session,
                    out var lifeRevision))
            {
                throw new InvalidOperationException(
                    "Incoming player damage requires an established life authority.");
            }
            var nextRecoveryAt =
                request.ResolvedAt + PlayerRecoveryInterval;
            var recoveryDeadline =
                GetOrCreatePlayerRecoveryDeadlineLocked(session);
            var decision = GetPlayerRuntimeEcs(session)
                .IncomingDamage.Apply(
                    character,
                    playerObjectId,
                    lifeRevision,
                    request,
                    beforeLethalCommit);
            if (decision.Applied &&
                decision.Killed)
            {
                var committedLifeRevision = checked(lifeRevision + 1);
                if (!_playerLifeRevisions.TryUpdate(
                        session,
                        committedLifeRevision,
                        lifeRevision))
                {
                    throw new InvalidOperationException(
                        "Established player life authority changed while " +
                        "the registry gate was held.");
                }
                if (committedLifeRevision !=
                    decision.AfterLifeRevision)
                {
                    throw new InvalidOperationException(
                        "Incoming damage ECS and registry life revisions diverged.");
                }

                ApplyPlayerLifeAdvanceSideEffectsLocked(
                    session,
                    nextRecoveryAt,
                    recoveryDeadline,
                    request.ResolvedAt,
                    resetIncomingDamage: true);
            }
            else if (decision.AfterLifeRevision !=
                     lifeRevision)
            {
                throw new InvalidOperationException(
                    "Nonlethal incoming damage changed the player life revision.");
            }

            return decision;
        }
    }
}
