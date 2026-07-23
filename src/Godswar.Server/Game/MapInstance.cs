using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Maps;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private readonly ConcurrentDictionary<ClientSession, GameSessionContext> _sessions = [];
    private readonly ConcurrentDictionary<ClientSession, MonsterViewerState> _monsterViewers = [];
    private readonly object _membershipGate = new();
    private readonly object _monsterRuntimeGate = new();
    private readonly MonsterRuntimeMode _monsterRuntimeMode;
    private readonly PlayerRuntimeMode _playerRuntimeMode;
    private IMonsterMapRuntime? _monsterRuntime;

    public MapInstance(
        byte mapId,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs)
    {
        MapId = mapId;
        _monsterRuntimeMode = monsterRuntimeMode;
        _playerRuntimeMode = playerRuntimeMode;
        _ecsShadow = new MapEcsShadow(mapId);
    }

    public byte MapId { get; }

    public int Population => _playerRuntimeMode == PlayerRuntimeMode.Ecs
        ? _ecsShadow.PlayerCount
        : _sessions.Count;

    public void AddOrUpdate(GameSessionContext context)
    {
        lock (_membershipGate)
        {
            lock (_monsterRuntimeGate)
            {
                EnsurePlayerObjectIdDoesNotCollideWithNpcs(context);
            }

            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                if (!_ecsShadow.TryAddOrUpdatePlayer(context))
                {
                    _ecsShadow.ClearPlayerFault(context.Session);
                    throw new InvalidOperationException(
                        $"ECS rejected player {context.ObjectId} on map {MapId}.");
                }

                _sessions[context.Session] = context;
                return;
            }

            _sessions[context.Session] = context;
            _ecsShadow.TryAddOrUpdatePlayer(context);
        }
    }

    public bool Remove(ClientSession session, out GameSessionContext? context)
    {
        if (!_monsterViewers.TryGetValue(session, out var viewer))
        {
            return RemoveSessionAndShadow(session, out context);
        }

        // Map moves/removal must wait for a leased monster send to finish. The
        // registry never calls Remove while retaining a viewer lease itself.
        viewer.TransitionGate.Wait();
        try
        {
            _monsterViewers.TryRemove(
                new KeyValuePair<ClientSession, MonsterViewerState>(session, viewer));
            return RemoveSessionAndShadow(session, out context);
        }
        finally
        {
            viewer.TransitionGate.Release();
        }
    }

    public IReadOnlyList<GameSessionContext> Snapshot()
    {
        lock (_membershipGate)
        {
            if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
            {
                return _sessions.Values.ToArray();
            }

            return _ecsShadow.SnapshotPlayerSessions()
                .Select(session => _sessions.TryGetValue(
                    session,
                    out var context)
                    ? context
                    : null)
                .Where(static context => context is not null)
                .Select(static context => context!)
                .ToArray();
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
        return TryApplyMonsterDamage(
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    public bool TryApplyMonsterDamage(
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        lock (_monsterRuntimeGate)
        {
            if (_monsterRuntime is not null &&
                _monsterRuntime.TryApplyDamage(
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
    }

    internal bool TryApplyMonsterDamageGuarded(
        uint objectId,
        uint damage,
        int attackerCharacterId,
        uint expectedSpawnGeneration,
        ulong expectedHealthRevision,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        lock (_monsterRuntimeGate)
        {
            if (_monsterRuntime is not null &&
                _monsterRuntime.TryGetSnapshot(
                    objectId,
                    out var snapshot) &&
                snapshot.SpawnGeneration ==
                    expectedSpawnGeneration &&
                snapshot.HealthRevision ==
                    expectedHealthRevision &&
                _monsterRuntime.TryApplyDamage(
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
    }

    public bool TryApplyMonsterStun(
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        return TryApplyMonsterStun(
            objectId,
            attackerCharacterId,
            duration,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

    public bool TryApplyMonsterStun(
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        lock (_monsterRuntimeGate)
        {
            if (_monsterRuntime is not null &&
                _monsterRuntime.TryApplyStun(
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
    }

    public MonsterRuntimeTick AdvanceMonsters(
        DateTimeOffset now,
        Func<ClientSession, long>? lifeRevisionResolver = null)
    {
        MonsterCombatTarget[] combatTargets;
        if (lifeRevisionResolver is null)
        {
            // Preserve the original Legacy/runtime-test target projection.
            combatTargets = Snapshot()
                .Where(context => context.WorldReady)
                .Select(context => new MonsterCombatTarget(
                    context.CharacterId,
                    context.Character.PositionX,
                    context.Character.PositionZ,
                    context.Character.CurrentHp > 0))
                .ToArray();
        }
        else
        {
            combatTargets = Snapshot()
                .Where(context => context.WorldReady)
                .Select(context =>
                {
                    var lifeRevision =
                        lifeRevisionResolver(context.Session);
                    lock (context.Character.VitalsSync)
                    {
                        return new MonsterCombatTarget(
                            context.CharacterId,
                            context.Character.PositionX,
                            context.Character.PositionZ,
                            context.Character.CurrentHp > 0,
                            context.ObjectId,
                            lifeRevision);
                    }
                })
                .ToArray();
        }

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
        CancellationToken cancellationToken,
        bool forceRefreshVisible = false)
    {
        if (!ContainsPlayer(session))
        {
            return null;
        }

        var viewer = _monsterViewers.GetOrAdd(session, static _ => new MonsterViewerState());
        await viewer.TransitionGate.WaitAsync(cancellationToken);
        try
        {
            if (!ContainsPlayer(session) ||
                !_monsterViewers.TryGetValue(session, out var currentViewer) ||
                !ReferenceEquals(currentViewer, viewer))
            {
                _monsterViewers.TryRemove(
                    new KeyValuePair<ClientSession, MonsterViewerState>(session, viewer));
                viewer.TransitionGate.Release();
                return null;
            }

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
            var desiredVersions = desired
                .ToDictionary(
                    monster => monster.ObjectId,
                    monster => monster.AppearanceVersion);
            var entering = desired
                .Where(monster =>
                    forceRefreshVisible ||
                    !viewer.VisibleMonsterVersions.TryGetValue(
                        monster.ObjectId,
                        out var visibleVersion) ||
                    visibleVersion.SpawnGeneration != monster.SpawnGeneration)
                .ToArray();
            var leaving = viewer.VisibleMonsterVersions
                .Where(entry =>
                    !desiredVersions.TryGetValue(entry.Key, out var desiredVersion) ||
                    desiredVersion.SpawnGeneration != entry.Value.SpawnGeneration ||
                    forceRefreshVisible)
                .Select(entry => entry.Key)
                .OrderBy(objectId => objectId)
                .ToArray();
            return new MonsterVisibilityTransition(
                viewer,
                new MonsterVisibilityDelta(playerCell, entering, leaving),
                desiredVersions);
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
               viewer.VisibleMonsterVersions.ContainsKey(objectId);
    }

    public bool IsMonsterVisibleTo(
        ClientSession session,
        uint objectId,
        uint spawnGeneration)
    {
        return _monsterViewers.TryGetValue(session, out var viewer) &&
               viewer.VisibleMonsterVersions.TryGetValue(objectId, out var version) &&
               version.SpawnGeneration == spawnGeneration;
    }

    public async ValueTask<MonsterViewerDeliveryLease?> AcquireMonsterViewerDeliveryLeaseAsync(
        ClientSession session,
        uint objectId,
        CancellationToken cancellationToken)
    {
        return await AcquireMonsterViewerDeliveryLeaseCoreAsync(
            session,
            objectId,
            ordinarySpawnGeneration: null,
            healthMutations: null,
            cancellationToken);
    }

    public async ValueTask<MonsterViewerDeliveryLease?> AcquireMonsterViewerDeliveryLeaseAsync(
        ClientSession session,
        uint objectId,
        uint expectedSpawnGeneration,
        CancellationToken cancellationToken)
    {
        return await AcquireMonsterViewerDeliveryLeaseCoreAsync(
            session,
            objectId,
            expectedSpawnGeneration,
            healthMutations: null,
            cancellationToken);
    }

    public async ValueTask<MonsterViewerDeliveryLease?> AcquireMonsterViewerHealthDeliveryLeaseAsync(
        ClientSession session,
        IReadOnlyList<MonsterHealthMutation> healthMutations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(healthMutations);
        if (healthMutations.Count == 0)
        {
            return null;
        }

        return await AcquireMonsterViewerDeliveryLeaseCoreAsync(
            session,
            ordinaryObjectId: null,
            ordinarySpawnGeneration: null,
            healthMutations,
            cancellationToken);
    }

    private async ValueTask<MonsterViewerDeliveryLease?> AcquireMonsterViewerDeliveryLeaseCoreAsync(
        ClientSession session,
        uint? ordinaryObjectId,
        uint? ordinarySpawnGeneration,
        IReadOnlyList<MonsterHealthMutation>? healthMutations,
        CancellationToken cancellationToken)
    {
        if (!_monsterViewers.TryGetValue(session, out var viewer))
        {
            return null;
        }

        // A visibility transition keeps this gate through its remove/spawn sends
        // and commit. Runtime delivery must queue behind it, then decide against
        // the committed state so an entering viewer cannot miss an intervening hit.
        await viewer.TransitionGate.WaitAsync(cancellationToken);
        if (!_monsterViewers.TryGetValue(session, out var currentViewer) ||
            !ReferenceEquals(currentViewer, viewer) ||
            !ContainsPlayer(session))
        {
            _monsterViewers.TryRemove(
                new KeyValuePair<ClientSession, MonsterViewerState>(session, viewer));
            viewer.TransitionGate.Release();
            return null;
        }

        if (ordinaryObjectId is { } objectId)
        {
            if (!viewer.VisibleMonsterVersions.TryGetValue(objectId, out var visibleVersion) ||
                ordinarySpawnGeneration is { } expectedGeneration &&
                visibleVersion.SpawnGeneration != expectedGeneration)
            {
                viewer.TransitionGate.Release();
                return null;
            }

            return new MonsterViewerDeliveryLease(viewer, [], [], []);
        }

        var directMutations = new List<MonsterHealthMutation>(healthMutations!.Count);
        var reconciliationObjectIds = new List<uint>();
        var seenObjectIds = new HashSet<uint>();
        foreach (var mutation in healthMutations)
        {
            if (!seenObjectIds.Add(mutation.ObjectId))
            {
                viewer.TransitionGate.Release();
                throw new ArgumentException(
                    $"Duplicate monster health mutation for object {mutation.ObjectId}.",
                    nameof(healthMutations));
            }

            if (mutation.AfterHealthRevision != mutation.BeforeHealthRevision + 1)
            {
                viewer.TransitionGate.Release();
                throw new ArgumentException(
                    $"Monster health mutation {mutation.ObjectId} is not a single revision step.",
                    nameof(healthMutations));
            }

            if (!viewer.VisibleMonsterVersions.TryGetValue(
                    mutation.ObjectId,
                    out var visibleVersion) ||
                visibleVersion.SpawnGeneration != mutation.SpawnGeneration ||
                visibleVersion.HealthRevision >= mutation.AfterHealthRevision)
            {
                continue;
            }

            if (visibleVersion == mutation.BeforeVersion)
            {
                directMutations.Add(mutation);
            }
            else
            {
                // The viewer missed at least one older delta. Applying this one
                // would preserve a wrong HP total, so replace the appearance.
                reconciliationObjectIds.Add(mutation.ObjectId);
            }
        }

        if (directMutations.Count == 0 && reconciliationObjectIds.Count == 0)
        {
            viewer.TransitionGate.Release();
            return null;
        }

        var reconciliationMonsters = new List<MonsterRuntimeSnapshot>(
            reconciliationObjectIds.Count);
        if (reconciliationObjectIds.Count > 0)
        {
            var currentMonsters = SnapshotMonsters()
                .Where(monster => reconciliationObjectIds.Contains(monster.ObjectId))
                .ToDictionary(monster => monster.ObjectId);
            foreach (var reconciliationObjectId in reconciliationObjectIds)
            {
                if (currentMonsters.TryGetValue(reconciliationObjectId, out var monster) &&
                    monster.IsSpawned &&
                    viewer.PlayerCell is { } playerCell &&
                    WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(
                        monster.X,
                        monster.Z,
                        out var monsterCell) &&
                    WorldSectorVisibilityTracker<CapturedMonsterSpawn>.IsNeighbor(
                        playerCell,
                        monsterCell))
                {
                    reconciliationMonsters.Add(monster);
                }
            }
        }

        return new MonsterViewerDeliveryLease(
            viewer,
            directMutations,
            reconciliationObjectIds,
            reconciliationMonsters);
    }
}
