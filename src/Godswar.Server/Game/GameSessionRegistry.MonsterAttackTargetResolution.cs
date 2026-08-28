using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private IReadOnlyList<GameSessionContext>
        SnapshotMonsterAttackMembers(
            WorldInstanceRuntime runtime) =>
        InvokeWorldOwner(
            runtime,
            static map => map.Snapshot());

    private GameSessionContext? ResolveCurrentMonsterAttackTarget(
        WorldInstanceRuntime runtime,
        IReadOnlyList<GameSessionContext> members,
        int targetCharacterId,
        MonsterRuntimeUpdate attack)
    {
        var candidate = members.FirstOrDefault(context =>
            context.WorldReady &&
            context.CharacterId == targetCharacterId);
        if (candidate is null ||
            !_sessions.TryGetValue(
                candidate.Session,
                out var current) ||
            current.WorldInstanceId != runtime.InstanceId ||
            current.CharacterId != targetCharacterId ||
            !current.WorldReady ||
            attack.TargetObjectId is { } emittedObjectId &&
            current.ObjectId != emittedObjectId ||
            attack.TargetOwnership is { } emittedOwnership &&
            current.Ownership != emittedOwnership ||
            attack.TargetWorldInstanceId is { } emittedWorldInstanceId &&
            current.WorldInstanceId != emittedWorldInstanceId ||
            attack.TargetWorldRevision is { } emittedWorldRevision &&
            current.WorldRevision != emittedWorldRevision ||
            attack.TargetWorldMembershipEpoch is { } emittedEpoch &&
            current.WorldMembershipEpoch != emittedEpoch ||
            attack.TargetLifeRevision is { } emittedLifeRevision &&
            (!_playerLifeRevisions.TryGetValue(
                 current.Session,
                 out var currentLifeRevision) ||
             currentLifeRevision != emittedLifeRevision))
        {
            return null;
        }

        return current;
    }

    private static bool HasExactEmittedMonsterTarget(
        MonsterRuntimeUpdate attack) =>
        attack.TargetObjectId is > 0 &&
        attack.TargetLifeRevision is >= 0 &&
        attack.TargetOwnership is { IsValid: true } &&
        attack.TargetWorldInstanceId is { IsValid: true } &&
        attack.TargetWorldRevision is >= 0 &&
        attack.TargetWorldMembershipEpoch is > 0;

    private void ClearMonsterAttackAggro(
        WorldInstanceRuntime runtime,
        int targetCharacterId,
        DateTimeOffset now) =>
        InvokeWorldOwner(
            runtime,
            map => map.ClearMonsterAggroForCharacter(
                targetCharacterId,
                now));
}
