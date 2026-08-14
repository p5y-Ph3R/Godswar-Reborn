using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal PlayerCombatEcsDecision ResolvePlayerCombatEcs(
        ClientSession session,
        GameCharacter character,
        uint objectId,
        DateTimeOffset nextBasicAttackAt,
        in PlayerCombatEcsRequest request,
        Action? onAdmittedAttempt = null)
    {
        if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
        {
            throw new InvalidOperationException(
                "The live combat ECS adapter is disabled in Legacy mode.");
        }

        return GetPlayerRuntimeEcs(session).Combat.Execute(
            this,
            session,
            character,
            objectId,
            nextBasicAttackAt,
            request,
            onAdmittedAttempt);
    }

    internal PlayerCombatEcsProjectionDecision
        ProjectCommittedMonsterKillProgressionEcs(
            ClientSession session,
            MonsterDamageResult damageResult,
            CharacterProgressionResult committed)
    {
        if (_playerRuntimeMode != PlayerRuntimeMode.Ecs ||
            !_playerRuntimeEcs.TryGetValue(session, out var adapters))
        {
            return default;
        }

        return adapters.Combat.ProjectCommittedProgression(
            damageResult,
            committed);
    }

    internal PlayerCombatEcsDecision?
        GetPlayerCombatEcsDiagnostics(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerRuntimeEcs.TryGetValue(
            session,
            out var adapters)
            ? adapters.Combat.Snapshot()
            : null;
    }

    internal PlayerCombatEcsProjectionDecision?
        GetPlayerCombatProjectionEcsDiagnostics(
            ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _playerRuntimeEcs.TryGetValue(
            session,
            out var adapters)
            ? adapters.Combat.ProjectionSnapshot()
            : null;
    }
}
