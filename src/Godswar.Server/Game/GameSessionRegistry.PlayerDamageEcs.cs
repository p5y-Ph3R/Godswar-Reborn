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

            var lifeRevision =
                _playerLifeRevisions.GetOrAdd(session, 0);
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
                var committedLifeRevision =
                    _playerLifeRevisions.AddOrUpdate(
                        session,
                        1,
                        static (_, revision) =>
                            checked(revision + 1));
                if (committedLifeRevision !=
                    decision.AfterLifeRevision)
                {
                    throw new InvalidOperationException(
                        "Incoming damage ECS and registry life revisions diverged.");
                }
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
