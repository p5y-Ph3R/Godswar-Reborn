using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class MapInstance
{
    private readonly ConcurrentDictionary<ClientSession, GameSessionContext> _sessions = [];
    private readonly ConcurrentDictionary<ClientSession, MonsterViewerState> _monsterViewers = [];
    private readonly object _monsterRuntimeGate = new();
    private MonsterMapRuntime? _monsterRuntime;

    public MapInstance(byte mapId)
    {
        MapId = mapId;
    }

    public byte MapId { get; }

    public int Population => _sessions.Count;

    public void AddOrUpdate(GameSessionContext context)
    {
        _sessions[context.Session] = context;
    }

    public bool Remove(ClientSession session, out GameSessionContext? context)
    {
        _monsterViewers.TryRemove(session, out _);
        return _sessions.TryRemove(session, out context);
    }

    public IReadOnlyList<GameSessionContext> Snapshot()
    {
        return _sessions.Values.ToArray();
    }

    public MonsterMapRuntime InitializeMonsters(
        IReadOnlyList<CapturedMonsterSpawn> definitions,
        DateTimeOffset initializedAt,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        lock (_monsterRuntimeGate)
        {
            return _monsterRuntime ??= new MonsterMapRuntime(
                MapId,
                definitions,
                initializedAt,
                activeWorldBossRespawn: activeWorldBossRespawn);
        }
    }

    public IReadOnlyList<MonsterRuntimeSnapshot> SnapshotMonsters()
    {
        lock (_monsterRuntimeGate)
        {
            return _monsterRuntime?.Snapshot() ?? [];
        }
    }

    public bool TryGetMonsterSnapshot(uint objectId, out MonsterRuntimeSnapshot snapshot)
    {
        lock (_monsterRuntimeGate)
        {
            if (_monsterRuntime is not null &&
                _monsterRuntime.TryGetSnapshot(objectId, out snapshot))
            {
                return true;
            }

            snapshot = default!;
            return false;
        }
    }

    public bool TryApplyMonsterDamage(
        uint objectId,
        uint damage,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        return TryApplyMonsterDamage(objectId, damage, attackerCharacterId: null, now, out result);
    }

    public bool TryApplyMonsterDamage(
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        lock (_monsterRuntimeGate)
        {
            if (_monsterRuntime is not null &&
                _monsterRuntime.TryApplyDamage(objectId, damage, attackerCharacterId, now, out result))
            {
                return true;
            }

            result = default!;
            return false;
        }
    }

    public MonsterRuntimeTick AdvanceMonsters(DateTimeOffset now)
    {
        var combatTargets = Snapshot()
            .Where(context => context.WorldReady)
            .Select(context => new MonsterCombatTarget(
                context.CharacterId,
                context.Character.PositionX,
                context.Character.PositionZ,
                context.Character.CurrentHp > 0))
            .ToArray();
        lock (_monsterRuntimeGate)
        {
            return _monsterRuntime?.Advance(now, combatTargets) ?? new MonsterRuntimeTick(false, []);
        }
    }

    public void ClearMonsterAggroForCharacter(int characterId, DateTimeOffset now)
    {
        lock (_monsterRuntimeGate)
        {
            _monsterRuntime?.ClearAggroForCharacter(characterId, now);
        }
    }

    public async ValueTask<MonsterVisibilityTransition?> BeginMonsterVisibilityTransitionAsync(
        ClientSession session,
        float playerX,
        float playerZ,
        CancellationToken cancellationToken)
    {
        var viewer = _monsterViewers.GetOrAdd(session, static _ => new MonsterViewerState());
        await viewer.TransitionGate.WaitAsync(cancellationToken);
        try
        {
            if (!WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(
                    playerX,
                    playerZ,
                    out var playerCell))
            {
                viewer.TransitionGate.Release();
                return null;
            }

            var desired = SnapshotMonsters()
                .Where(monster =>
                    monster.IsSpawned &&
                    WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(
                        monster.X,
                        monster.Z,
                        out var monsterCell) &&
                    WorldSectorVisibilityTracker<CapturedMonsterSpawn>.IsNeighbor(
                        playerCell,
                        monsterCell))
                .OrderBy(monster => monster.ObjectId)
                .ToArray();
            var desiredObjectIds = desired
                .Select(monster => monster.ObjectId)
                .ToHashSet();
            var entering = desired
                .Where(monster => !viewer.VisibleObjectIds.ContainsKey(monster.ObjectId))
                .ToArray();
            var leaving = viewer.VisibleObjectIds.Keys
                .Where(objectId => !desiredObjectIds.Contains(objectId))
                .OrderBy(objectId => objectId)
                .ToArray();
            return new MonsterVisibilityTransition(
                viewer,
                new MonsterVisibilityDelta(playerCell, entering, leaving),
                desiredObjectIds);
        }
        catch
        {
            viewer.TransitionGate.Release();
            throw;
        }
    }

    public bool IsMonsterVisibleTo(ClientSession session, uint objectId)
    {
        return _monsterViewers.TryGetValue(session, out var viewer) &&
               viewer.VisibleObjectIds.ContainsKey(objectId);
    }
}

internal sealed record MonsterVisibilityDelta(
    WorldGridCell PlayerCell,
    IReadOnlyList<MonsterRuntimeSnapshot> Entering,
    IReadOnlyList<uint> Leaving);

internal sealed class MonsterVisibilityTransition : IAsyncDisposable
{
    private MonsterViewerState? _viewer;
    private readonly IReadOnlySet<uint> _desiredObjectIds;

    public MonsterVisibilityTransition(
        MonsterViewerState viewer,
        MonsterVisibilityDelta delta,
        IReadOnlySet<uint> desiredObjectIds)
    {
        _viewer = viewer;
        Delta = delta;
        _desiredObjectIds = desiredObjectIds;
    }

    public MonsterVisibilityDelta Delta { get; }

    public bool IsDesiredVisible(uint objectId)
    {
        return _desiredObjectIds.Contains(objectId);
    }

    public void Commit()
    {
        var viewer = _viewer ?? throw new ObjectDisposedException(nameof(MonsterVisibilityTransition));
        foreach (var objectId in viewer.VisibleObjectIds.Keys)
        {
            if (!_desiredObjectIds.Contains(objectId))
            {
                viewer.VisibleObjectIds.TryRemove(objectId, out _);
            }
        }

        foreach (var objectId in _desiredObjectIds)
        {
            viewer.VisibleObjectIds[objectId] = 0;
        }
    }

    public ValueTask DisposeAsync()
    {
        var viewer = Interlocked.Exchange(ref _viewer, null);
        viewer?.TransitionGate.Release();
        return ValueTask.CompletedTask;
    }
}

internal sealed class MonsterViewerState
{
    public SemaphoreSlim TransitionGate { get; } = new(1, 1);

    public ConcurrentDictionary<uint, byte> VisibleObjectIds { get; } = [];
}
