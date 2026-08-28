using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime : IMonsterMapRuntime
{
    internal const int TicksPerSecond = 12;
    internal const float MovementStep = 0.38f;
    internal const float MaximumRoamRadius = 8f;
    // Monster.ini's Range field is model collision, not AI distance. Combat
    // needs a much larger boundary than idle roaming so a monster can sustain
    // a useful chase before the authoritative replacement reset fires.
    internal const float CombatLeashRadius = 32f;
    internal const int MinimumMovementTicks = 1;
    internal const int MaximumMovementTicks = 21;
    internal const int MinimumIdleTicks = 15 * TicksPerSecond;
    internal const int MaximumIdleTicks = 20 * TicksPerSecond;
    internal const float CombatRange = MonsterAttackRangePolicy.MeleeRange;
    internal const float AggroDetectionRadius =
        MonsterAggroPolicy.DetectionRadius;
    internal const int AttackCooldownTicks = 23;
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1d / TicksPerSecond);
    internal static readonly TimeSpan AttackCooldown = TimeSpan.FromTicks(TickInterval.Ticks * AttackCooldownTicks);
    internal static readonly TimeSpan DefaultCorpseDespawnDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultRespawnDelay = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<uint, MonsterRuntimeState> _monsters;
    private readonly Queue<MonsterRuntimeUpdate> _pendingUpdates = new();
    private readonly TimeSpan _corpseDespawnDelay;
    private readonly TimeSpan? _respawnDelay;
    private readonly Guid _runtimeInstanceId;
    private readonly WorldBossCatalog _worldBossCatalog;

    public MonsterMapRuntime(
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
        _worldBossCatalog = worldBossCatalog ?? WorldBossCatalog.Empty;
        var resolvedCombatProfiles = monsterCombatProfiles ??
            MonsterCombatProfileCatalog.Empty;
        _corpseDespawnDelay = corpseDespawnDelay ?? DefaultCorpseDespawnDelay;
        if (_corpseDespawnDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(corpseDespawnDelay),
                "Monster corpse-despawn delay must be positive.");
        }

        _respawnDelay = MonsterRespawnPolicyRules.ResolveOrdinaryDelay(
            respawnPolicy,
            _corpseDespawnDelay,
            respawnDelay);
        MonsterRespawnPolicyRules.RejectTimedWorldBossConfiguration(
            respawnPolicy,
            mapId,
            capturedDefinitions,
            activeWorldBossRespawn,
            _worldBossCatalog);

        _monsters = capturedDefinitions
            .OrderBy(definition => definition.ObjectId)
            .ToDictionary(
                definition => definition.ObjectId,
                definition => CreateState(
                    mapId,
                    definition,
                    initializedAt,
                    activeWorldBossRespawn,
                    _runtimeInstanceId,
                    respawnPolicy,
                    MonsterAttackRangePolicy.Resolve(
                        resolvedCombatProfiles.Resolve(definition),
                        definition)));
    }

    public byte MapId { get; }

    public int Count => _monsters.Count;

    public IReadOnlyList<MonsterRuntimeSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _monsters.Values
                .OrderBy(monster => monster.Definition.ObjectId)
                .Select(CreateSnapshot)
                .ToArray();
        }
    }

    public bool TryGetSnapshot(uint objectId, out MonsterRuntimeSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!_monsters.TryGetValue(objectId, out var monster))
            {
                snapshot = default!;
                return false;
            }

            snapshot = CreateSnapshot(monster);
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
        out MonsterDamageResult result)
    {
        return TryApplyDamage(
            objectId,
            damage,
            attackerCharacterId,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

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
            if (!_monsters.TryGetValue(objectId, out var monster) ||
                expectedSpawnGeneration is { } expectedGeneration &&
                monster.SpawnGeneration != expectedGeneration)
            {
                result = default!;
                return false;
            }

            var beforeHealth = monster.CurrentHealth;
            var beforeHealthRevision = monster.HealthRevision;
            if (!monster.IsAlive ||
                !monster.IsSpawned ||
                damage == 0 ||
                !periodic &&
                monster.CombatPhase is (
                    MonsterCombatPhase.Returning or
                    MonsterCombatPhase.AwaitingRetirement))
            {
                result = new MonsterDamageResult(
                    objectId,
                    beforeHealth,
                    beforeHealth,
                    false,
                    CreateSnapshot(monster),
                    HealthMutation: null,
                    monster.FirstHitCharacterId);
                return true;
            }

            var afterHealth = damage >= beforeHealth
                ? 0
                : beforeHealth - damage;
            var afterHealthRevision = checked(beforeHealthRevision + 1);
            var killed = afterHealth == 0;
            var firstHitCharacterId = monster.FirstHitCharacterId;
            var claimEstablished =
                !periodic &&
                attackerCharacterId is > 0 &&
                firstHitCharacterId is null;
            if (!periodic && attackerCharacterId is > 0)
            {
                firstHitCharacterId ??= attackerCharacterId;
            }
            Dictionary<int, ulong>? nextThreat = null;
            var nextAggroCharacterId =
                monster.AggroCharacterId.GetValueOrDefault();
            if (!killed && !periodic && attackerCharacterId is > 0)
            {
                nextThreat = MonsterAggroPolicy.RecordDamage(
                    monster.DamageThreat,
                    attackerCharacterId.Value,
                    beforeHealth - afterHealth,
                    monster.AggroCharacterId,
                    out var leaderCharacterId);
                nextAggroCharacterId = leaderCharacterId;
            }

            monster.CurrentHealth = afterHealth;
            monster.HealthRevision = afterHealthRevision;
            monster.FirstHitCharacterId = firstHitCharacterId;
            if (killed)
            {
                ResetCombat(monster, now);
                monster.IsAlive = false;
                monster.IsMoving = false;
                monster.VelocityX = 0;
                monster.VelocityZ = 0;
                monster.MovementTicks = 0;
                monster.RemainingMovementTicks = 0;
                monster.DespawnAt = now + _corpseDespawnDelay;
                monster.RespawnAt = monster.RespawnPolicy switch
                {
                    MonsterRespawnPolicy.Timed => now +
                        _worldBossCatalog.ResolveRespawnInterval(
                            MapId,
                            monster.Definition.TemplateKey,
                            _respawnDelay!.Value),
                    MonsterRespawnPolicy.Never => null,
                    _ => throw new InvalidOperationException(
                        "Monster state contains an unsupported respawn policy.")
                };
                _pendingUpdates.Enqueue(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Died,
                    CreateSnapshot(monster)));
            }
            else if (nextThreat is not null)
            {
                monster.DamageThreat = nextThreat;
                if (monster.AggroCharacterId != nextAggroCharacterId)
                {
                    SetAggroTarget(
                        monster,
                        nextAggroCharacterId,
                        now);
                }
            }

            result = new MonsterDamageResult(
                objectId,
                beforeHealth,
                monster.CurrentHealth,
                killed,
                CreateSnapshot(monster),
                new MonsterHealthMutation(
                    objectId,
                    monster.SpawnGeneration,
                    beforeHealthRevision,
                    monster.HealthRevision),
                firstHitCharacterId,
                claimEstablished);
            return true;
        }
    }

    public bool TryApplyStun(
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        DateTimeOffset now,
        out MonsterStunResult result)
    {
        return TryApplyStun(
            objectId,
            attackerCharacterId,
            duration,
            expectedSpawnGeneration: null,
            now,
            out result);
    }

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
            if (!_monsters.TryGetValue(objectId, out var monster) ||
                expectedSpawnGeneration is { } expectedGeneration &&
                monster.SpawnGeneration != expectedGeneration)
            {
                result = default!;
                return false;
            }

            // A dead, despawned, or leashing monster cannot accept a fresh
            // combat status. Return movement is authoritative and cannot be
            // interrupted or held away from home by control effects.
            if (!monster.IsAlive ||
                !monster.IsSpawned ||
                monster.CombatPhase is MonsterCombatPhase.Returning or
                    MonsterCombatPhase.AwaitingRetirement)
            {
                result = new MonsterStunResult(
                    objectId,
                    Applied: false,
                    monster.StunnedUntil,
                    CreateSnapshot(monster));
                return true;
            }

            var stunnedUntil = now + duration;
            var wasMoving = monster.IsMoving;
            if (wasMoving)
            {
                StopCombatMovement(monster);
            }

            // A hostile control spell establishes retaliation aggro without
            // allowing the monster to move or attack until the status expires.
            // Resetting the phase also prevents a pre-stun attack timestamp
            // from being replayed as a catch-up strike after expiry.
            monster.AggroCharacterId =
                MonsterAggroPolicy.SelectLeader(
                    monster.DamageThreat,
                    monster.AggroCharacterId) ??
                attackerCharacterId;
            monster.CombatPhase = MonsterCombatPhase.None;
            monster.HasSentInitialChase = false;
            monster.StunnedUntil = stunnedUntil;
            monster.NextAttackAt = stunnedUntil + TickInterval;
            monster.NextMovementStepAt =
                stunnedUntil + ElementalMovementInterval(monster);

            if (wasMoving)
            {
                _pendingUpdates.Enqueue(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Arrived,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
            }

            result = new MonsterStunResult(
                objectId,
                Applied: true,
                stunnedUntil,
                CreateSnapshot(monster));
            return true;
        }
    }

    public void ClearAggroForCharacter(int characterId, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var monster in _monsters.Values)
            {
                monster.DamageThreat.Remove(characterId);
                if (monster.AggroCharacterId != characterId)
                {
                    continue;
                }

                var nextTarget = MonsterAggroPolicy.SelectLeader(
                    monster.DamageThreat,
                    currentTargetCharacterId: null);
                if (nextTarget.HasValue)
                {
                    SetAggroTarget(monster, nextTarget.Value, now);
                }
                else
                {
                    _pendingUpdates.Enqueue(BeginReturnHome(monster, now));
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
            foreach (var pair in _monsters)
            {
                var monster = pair.Value;
                monster.DamageThreat.Remove(characterId);
                if (monster.AggroCharacterId != characterId)
                {
                    continue;
                }

                var nextTarget = MonsterAggroPolicy.SelectLeader(
                    monster.DamageThreat,
                    currentTargetCharacterId: null);
                if (nextTarget.HasValue)
                {
                    SetAggroTarget(monster, nextTarget.Value, now);
                }
                else
                {
                    _ = BeginReturnHomeState(monster, now);
                }
            }
        }
    }
}
