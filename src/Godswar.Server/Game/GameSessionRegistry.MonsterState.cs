using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public IReadOnlyList<MonsterRuntimeSnapshot> GetMapMonsterSnapshots(byte mapId)
    {
        return _maps.TryGetValue(mapId, out var map)
            ? map.SnapshotMonsters()
            : [];
    }

    public bool TryGetMonsterSnapshot(
        byte mapId,
        uint objectId,
        out MonsterRuntimeSnapshot snapshot)
    {
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryGetMonsterSnapshot(objectId, out snapshot))
        {
            return true;
        }

        snapshot = default!;
        return false;
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
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryApplyMonsterDamage(
                objectId,
                damage,
                attackerCharacterId,
                expectedSpawnGeneration,
                now,
                out result))
        {
            return true;
        }

        result = default!;
        return false;
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
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryApplyMonsterDamageGuarded(
                objectId,
                damage,
                attackerCharacterId,
                expectedSpawnGeneration,
                expectedHealthRevision,
                now,
                out result))
        {
            return true;
        }

        result = default!;
        return false;
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
        if (_maps.TryGetValue(mapId, out var map) &&
            map.TryApplyMonsterStun(
                objectId,
                attackerCharacterId,
                duration,
                expectedSpawnGeneration,
                now,
                out result))
        {
            return true;
        }

        result = default!;
        return false;
    }

    public ValueTask<MonsterVisibilityTransition?> BeginMonsterVisibilityTransitionAsync(
        ClientSession session,
        byte mapId,
        float playerX,
        float playerZ,
        CancellationToken cancellationToken,
        bool forceRefreshVisible = false)
    {
        return _maps.TryGetValue(mapId, out var map)
            ? map.BeginMonsterVisibilityTransitionAsync(
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
               _maps.TryGetValue(context.MapId, out var map) &&
               map.IsMonsterVisibleTo(session, objectId);
    }

    public bool IsMonsterVisibleTo(
        ClientSession session,
        uint objectId,
        uint spawnGeneration)
    {
        return _sessions.TryGetValue(session, out var context) &&
               _maps.TryGetValue(context.MapId, out var map) &&
               map.IsMonsterVisibleTo(session, objectId, spawnGeneration);
    }

}
