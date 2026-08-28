using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

/// <summary>
/// Map-runtime adapter around the typed ECS monster simulation. It returns the
/// shared immutable DTOs so shadow parity and the reversible live cutover use
/// the same AOI and packet-replication path as the legacy runtime.
/// </summary>
internal sealed class EcsMonsterMapRuntime : IMonsterMapRuntime
{
    private readonly object _gate = new();
    private readonly EcsWorld _world = new();
    private readonly Dictionary<uint, EntityId> _entities = [];
    private readonly Queue<MonsterRuntimeUpdate> _pendingUpdates = [];
    private readonly MonsterEcsSimulationFrame _frame = new();
    private readonly EcsSystemScheduler _scheduler;
    private readonly Guid _runtimeInstanceId;
    private DateTimeOffset _lastAdvanceAt;

    public EcsMonsterMapRuntime(
        byte mapId,
        IEnumerable<CapturedMonsterSpawn> definitions,
        DateTimeOffset initializedAt,
        TimeSpan? corpseDespawnDelay = null,
        TimeSpan? respawnDelay = null,
        WorldBossRespawnState? activeWorldBossRespawn = null,
        Guid? runtimeInstanceId = null,
        WorldBossCatalog? worldBossCatalog = null,
        MonsterRespawnPolicy respawnPolicy = MonsterRespawnPolicy.Timed,
        MonsterCombatProfileCatalog? monsterCombatProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var capturedDefinitions = definitions.ToArray();
        MapId = mapId;
        _runtimeInstanceId =
            MonsterRuntimeIdentity.Resolve(runtimeInstanceId);
        var corpseDelay =
            corpseDespawnDelay ??
            MonsterMapRuntime.DefaultCorpseDespawnDelay;
        if (corpseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(corpseDespawnDelay),
                "Monster corpse-despawn delay must be positive.");
        }

        var ordinaryRespawnDelay =
            MonsterRespawnPolicyRules.ResolveOrdinaryDelay(
                respawnPolicy,
                corpseDelay,
                respawnDelay);
        var resolvedWorldBossCatalog =
            worldBossCatalog ?? WorldBossCatalog.Empty;
        MonsterRespawnPolicyRules.RejectTimedWorldBossConfiguration(
            respawnPolicy,
            mapId,
            capturedDefinitions,
            activeWorldBossRespawn,
            resolvedWorldBossCatalog);

        CapturedMonsterSpawnHydrator.RegisterComponents(_world);
        foreach (var definition in capturedDefinitions.OrderBy(
                     definition => definition.ObjectId))
        {
            var entity = CapturedMonsterSpawnHydrator.Hydrate(
                _world,
                mapId,
                definition,
                initializedAt,
                corpseDelay,
                ordinaryRespawnDelay,
                activeWorldBossRespawn,
                _runtimeInstanceId,
                resolvedWorldBossCatalog,
                respawnPolicy,
                monsterCombatProfiles);
            _entities.Add(definition.ObjectId, entity);
        }

        _scheduler = new EcsSystemScheduler(_world);
        _scheduler.AddSystem(new MonsterEcsSimulationSystem(_frame));
        _lastAdvanceAt = initializedAt;
    }

    public byte MapId { get; }

    public int Count => _entities.Count;

    internal EcsWorld World => _world;

    public IReadOnlyList<MonsterRuntimeSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _entities
                .OrderBy(pair => pair.Key)
                .Select(pair =>
                    MonsterEcsState.Snapshot(_world, pair.Value))
                .ToArray();
        }
    }

    public bool TryGetSnapshot(
        uint objectId,
        out MonsterRuntimeSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_entities.TryGetValue(objectId, out var entity))
            {
                snapshot = default!;
                return false;
            }

            snapshot = MonsterEcsState.Snapshot(_world, entity);
            return true;
        }
    }

    public bool TryApplyDamage(
        uint objectId,
        uint damage,
        DateTimeOffset now,
        out MonsterDamageResult result) =>
        TryApplyDamage(
            objectId,
            damage,
            attackerCharacterId: null,
            now,
            out result);

    public bool TryApplyDamage(
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        DateTimeOffset now,
        out MonsterDamageResult result) =>
        TryApplyDamage(
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration: null,
            now,
            out result);

    public bool TryApplyDamage(
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterDamageResult result)
        => TryApplyDamageCore(
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration,
            periodic: false,
            now,
            out result);

    public bool TryApplyPeriodicDamage(
        uint objectId,
        uint damage,
        int sourceCharacterId,
        uint expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterDamageResult result) =>
        TryApplyDamageCore(
            objectId,
            damage,
            sourceCharacterId,
            expectedSpawnGeneration,
            periodic: true,
            now,
            out result);

    public bool TrySetMovementSpeedBasisPoints(
        uint objectId,
        uint expectedSpawnGeneration,
        int speedBasisPoints)
    {
        if (speedBasisPoints is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedBasisPoints));
        }

        lock (_gate)
        {
            if (!_entities.TryGetValue(objectId, out var entity))
            {
                return false;
            }

            ref var vitals =
                ref _world.Get<MonsterVitalsComponent>(entity);
            if (vitals.SpawnGeneration != expectedSpawnGeneration)
            {
                return false;
            }

            ref var movement =
                ref _world.Get<MonsterMovementComponent>(entity);
            movement.MovementSpeedBasisPoints = speedBasisPoints;
            return true;
        }
    }

    public bool TrySetCorpseDespawnAt(
        uint objectId,
        uint expectedSpawnGeneration,
        DateTimeOffset? despawnAt)
    {
        lock (_gate)
        {
            if (!_entities.TryGetValue(objectId, out var entity))
            {
                return false;
            }

            ref var vitals =
                ref _world.Get<MonsterVitalsComponent>(entity);
            ref var combat =
                ref _world.Get<MonsterCombatComponent>(entity);
            if (vitals.SpawnGeneration != expectedSpawnGeneration ||
                vitals.IsAlive ||
                !vitals.IsSpawned ||
                combat.Phase != MonsterCombatPhase.AwaitingRetirement)
            {
                return false;
            }

            ref var lifecycle =
                ref _world.Get<MonsterLifecycleComponent>(entity);
            lifecycle.DespawnAt = despawnAt?.ToUniversalTime();
            return true;
        }
    }

    private bool TryApplyDamageCore(
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        uint? expectedSpawnGeneration,
        bool periodic,
        DateTimeOffset now,
        out MonsterDamageResult result)
    {
        lock (_gate)
        {
            if (!_entities.TryGetValue(objectId, out var entity))
            {
                result = default!;
                return false;
            }

            return MonsterEcsDamageSystem.TryApply(
                _world,
                entity,
                damage,
                attackerCharacterId,
                expectedSpawnGeneration,
                periodic,
                now,
                _pendingUpdates,
                out result);
        }
    }

    public bool TryApplyStun(
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        DateTimeOffset now,
        out MonsterStunResult result) =>
        TryApplyStun(
            objectId,
            attackerCharacterId,
            duration,
            expectedSpawnGeneration: null,
            now,
            out result);

    public bool TryApplyStun(
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        if (attackerCharacterId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attackerCharacterId),
                attackerCharacterId,
                "A monster stun requires an authoritative attacking character.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "A monster stun duration must be positive.");
        }

        lock (_gate)
        {
            if (!_entities.TryGetValue(objectId, out var entity))
            {
                result = default!;
                return false;
            }

            return MonsterEcsStunSystem.TryApply(
                _world,
                entity,
                attackerCharacterId,
                duration,
                expectedSpawnGeneration,
                now,
                _pendingUpdates,
                out result);
        }
    }

    public void ClearAggroForCharacter(
        int characterId,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var entity in _entities
                         .OrderBy(pair => pair.Key)
                         .Select(pair => pair.Value))
            {
                ref var combat =
                    ref _world.Get<MonsterCombatComponent>(entity);
                combat.DamageThreat?.Remove(characterId);
                if (combat.AggroCharacterId != characterId)
                {
                    continue;
                }

                var nextTarget = MonsterAggroPolicy.SelectLeader(
                    combat.DamageThreat,
                    currentTargetCharacterId: null);
                if (nextTarget.HasValue)
                {
                    ref var movement = ref _world.Get<
                        MonsterMovementComponent>(entity);
                    MonsterEcsState.SetAggroTarget(
                        ref movement,
                        ref combat,
                        nextTarget.Value,
                        now);
                }
                else
                {
                    _pendingUpdates.Enqueue(
                        MonsterEcsState.BeginReturnHome(
                            _world,
                            entity,
                            now));
                }
            }
        }
    }

    public void ClearAggroForCharacterStateOnly(
        int characterId,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var pair in _entities)
            {
                var entity = pair.Value;
                ref var combat =
                    ref _world.Get<MonsterCombatComponent>(entity);
                combat.DamageThreat?.Remove(characterId);
                if (combat.AggroCharacterId != characterId)
                {
                    continue;
                }

                var nextTarget = MonsterAggroPolicy.SelectLeader(
                    combat.DamageThreat,
                    currentTargetCharacterId: null);
                if (nextTarget.HasValue)
                {
                    ref var movement = ref _world.Get<
                        MonsterMovementComponent>(entity);
                    MonsterEcsState.SetAggroTarget(
                        ref movement,
                        ref combat,
                        nextTarget.Value,
                        now);
                }
                else
                {
                    _ = MonsterEcsState.BeginReturnHomeState(
                        _world,
                        entity,
                        now);
                }
            }
        }
    }

    public MonsterRuntimeTick Advance(
        DateTimeOffset now,
        IReadOnlyList<MonsterCombatTarget>? combatTargets = null)
    {
        lock (_gate)
        {
            var pending = _pendingUpdates.ToArray();
            _pendingUpdates.Clear();
            _frame.Prepare(now, combatTargets, pending);
            var deltaTime = now >= _lastAdvanceAt
                ? now - _lastAdvanceAt
                : TimeSpan.Zero;
            _scheduler.RunTick(deltaTime);
            _lastAdvanceAt = now;

            var eventSpan =
                _scheduler.Events.Read<MonsterEcsUpdateEvent>();
            var updates = new MonsterRuntimeUpdate[eventSpan.Length];
            for (var index = 0; index < eventSpan.Length; index++)
            {
                updates[index] = eventSpan[index].Update;
            }

            return new MonsterRuntimeTick(
                _frame.PositionsChanged,
                updates);
        }
    }
}
