using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public IReadOnlyList<MonsterRuntimeSnapshot>
        GetMapMonsterSnapshots(byte mapId) =>
        GetMapMonsterSnapshotsCore(
            mapId,
            routingSession: null);

    public IReadOnlyList<MonsterRuntimeSnapshot>
        GetMapMonsterSnapshots(
            ClientSession routingSession,
            byte mapId) =>
        GetMapMonsterSnapshotsCore(
            mapId,
            routingSession);

    private IReadOnlyList<MonsterRuntimeSnapshot>
        GetMapMonsterSnapshotsCore(
            byte mapId,
            ClientSession? routingSession)
    {
        return TryResolveWorldInstance(
                mapId,
                routingSession,
                out var runtime)
            ? InvokeWorldOwner(
                runtime,
                static map => map.SnapshotMonsters())
            : [];
    }

    public bool TryGetMonsterSnapshot(
        byte mapId,
        uint objectId,
        out MonsterRuntimeSnapshot snapshot) =>
        TryGetMonsterSnapshotCore(
            mapId,
            objectId,
            routingSession: null,
            out snapshot);

    public bool TryGetMonsterSnapshot(
        ClientSession routingSession,
        byte mapId,
        uint objectId,
        out MonsterRuntimeSnapshot snapshot) =>
        TryGetMonsterSnapshotCore(
            mapId,
            objectId,
            routingSession,
            out snapshot);

    internal bool TryGetMonsterSnapshot(
        byte mapId,
        uint objectId,
        int routingCharacterId,
        out MonsterRuntimeSnapshot snapshot)
    {
        if (!TryResolveWorldInstance(
                mapId,
                routingCharacterId,
                out var runtime))
        {
            snapshot = default!;
            return false;
        }

        return TryGetMonsterSnapshotCore(
            runtime,
            objectId,
            out snapshot);
    }

    private bool TryGetMonsterSnapshotCore(
        byte mapId,
        uint objectId,
        ClientSession? routingSession,
        out MonsterRuntimeSnapshot snapshot)
    {
        if (!TryResolveWorldInstance(
                mapId,
                routingSession,
                out var runtime))
        {
            snapshot = default!;
            return false;
        }

        return TryGetMonsterSnapshotCore(
            runtime,
            objectId,
            out snapshot);
    }

    private bool TryGetMonsterSnapshotCore(
        WorldInstances.WorldInstanceRuntime runtime,
        uint objectId,
        out MonsterRuntimeSnapshot snapshot)
    {
        var attempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var found = map.TryGetMonsterSnapshot(
                    objectId,
                    out var value);
                return (Found: found, Value: value);
            });
        snapshot = attempt.Value;
        return attempt.Found;
    }

    public bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            DateTimeOffset.UtcNow,
            out result);
    }

    public bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int attackerCharacterId,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration: null,
            DateTimeOffset.UtcNow,
            out result);
    }

    public bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int attackerCharacterId,
        uint expectedSpawnGeneration,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration,
            DateTimeOffset.UtcNow,
            out result);
    }

    internal bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId: null,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    internal bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(
            mapId,
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    internal bool TryApplyMonsterDamage(
        byte mapId,
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        var routed = attackerCharacterId is { } characterId
            ? TryResolveWorldInstance(
                mapId,
                characterId,
                out var runtime)
            : TryResolveWorldInstance(
                mapId,
                routingSession: null,
                out runtime);
        if (!routed)
        {
            result = default!;
            return false;
        }

        var attempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var applied = map.TryApplyMonsterDamage(
                    objectId,
                    damage,
                    attackerCharacterId,
                    expectedSpawnGeneration,
                    now,
                    out var value);
                return (Applied: applied, Value: value);
            });
        result = attempt.Value;
        return attempt.Applied;
    }

    internal bool TryApplyMonsterDamageGuarded(
        byte mapId,
        uint objectId,
        uint damage,
        int attackerCharacterId,
        uint expectedSpawnGeneration,
        ulong expectedHealthRevision,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        if (!TryResolveWorldInstance(
                mapId,
                attackerCharacterId,
                out var runtime))
        {
            result = default!;
            return false;
        }

        var attempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var applied = map.TryApplyMonsterDamageGuarded(
                    objectId,
                    damage,
                    attackerCharacterId,
                    expectedSpawnGeneration,
                    expectedHealthRevision,
                    now,
                    out var value);
                return (Applied: applied, Value: value);
            });
        result = attempt.Value;
        return attempt.Applied;
    }

    internal bool TryApplyMonsterStun(
        byte mapId,
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        return TryApplyMonsterStun(
            mapId,
            objectId,
            attackerCharacterId,
            duration,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    internal bool TryApplyMonsterStun(
        byte mapId,
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        if (!TryResolveWorldInstance(
                mapId,
                attackerCharacterId,
                out var runtime))
        {
            result = default!;
            return false;
        }

        var attempt = InvokeWorldOwner(
            runtime,
            map =>
            {
                var applied = map.TryApplyMonsterStun(
                    objectId,
                    attackerCharacterId,
                    duration,
                    expectedSpawnGeneration,
                    now,
                    out var value);
                return (Applied: applied, Value: value);
            });
        result = attempt.Value;
        return attempt.Applied;
    }

    public ValueTask<MonsterVisibilityTransition?> BeginMonsterVisibilityTransitionAsync(
        ClientSession session,
        byte mapId,
        float playerX,
        float playerZ,
        CancellationToken cancellationToken,
        bool forceRefreshVisible = false)
    {
        return TryResolveWorldInstance(
                mapId,
                session,
                out var runtime)
            ? runtime.Map.BeginMonsterVisibilityTransitionAsync(
                session,
                playerX,
                playerZ,
                cancellationToken,
                forceRefreshVisible)
            : ValueTask.FromResult<MonsterVisibilityTransition?>(null);
    }

    public bool IsMonsterVisibleTo(ClientSession session, uint objectId)
    {
        return _sessions.TryGetValue(session, out var context) &&
               TryGetWorldInstance(context, out var runtime) &&
               runtime.Map.IsMonsterVisibleTo(session, objectId);
    }

    public bool IsMonsterVisibleTo(
        ClientSession session,
        uint objectId,
        uint spawnGeneration)
    {
        return _sessions.TryGetValue(session, out var context) &&
               TryGetWorldInstance(context, out var runtime) &&
               runtime.Map.IsMonsterVisibleTo(
                   session,
                   objectId,
                   spawnGeneration);
    }

}
