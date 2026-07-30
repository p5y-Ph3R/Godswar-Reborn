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
    internal const float CombatRange = 3f;
    internal const int AttackCooldownTicks = 21;
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1d / TicksPerSecond);
    internal static readonly TimeSpan AttackCooldown = TimeSpan.FromTicks(TickInterval.Ticks * AttackCooldownTicks);
    internal static readonly TimeSpan DefaultCorpseDespawnDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultRespawnDelay = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<uint, MonsterRuntimeState> _monsters;
    private readonly Queue<MonsterRuntimeUpdate> _pendingUpdates = new();
    private readonly TimeSpan _corpseDespawnDelay;
    private readonly TimeSpan _respawnDelay;
    private readonly Guid _runtimeInstanceId;

    public MonsterMapRuntime(
        byte mapId,
        IEnumerable<CapturedMonsterSpawn> definitions,
        DateTimeOffset initializedAt,
        TimeSpan? corpseDespawnDelay = null,
        TimeSpan? respawnDelay = null,
        WorldBossRespawnState? activeWorldBossRespawn = null,
        Guid? runtimeInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        MapId = mapId;
        _runtimeInstanceId =
            MonsterRuntimeIdentity.Resolve(runtimeInstanceId);
        _corpseDespawnDelay = corpseDespawnDelay ?? DefaultCorpseDespawnDelay;
        _respawnDelay = respawnDelay ?? DefaultRespawnDelay;
        if (_corpseDespawnDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(corpseDespawnDelay),
                "Monster corpse-despawn delay must be positive.");
        }

        if (_respawnDelay <= _corpseDespawnDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(respawnDelay),
                "Monster respawn delay must be later than corpse despawn.");
        }

        _monsters = definitions
            .OrderBy(definition => definition.ObjectId)
            .ToDictionary(
                definition => definition.ObjectId,
                definition => CreateState(
                    mapId,
                    definition,
                    initializedAt,
                    activeWorldBossRespawn,
                    _runtimeInstanceId));
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
        out MonsterDamageResult result)
    {
        return TryApplyDamage(objectId, damage, attackerCharacterId: null, now, out result);
    }

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
                monster.CombatPhase is MonsterCombatPhase.Returning or
                    MonsterCombatPhase.AwaitingRetirement)
            {
                result = new MonsterDamageResult(
                    objectId,
                    beforeHealth,
                    beforeHealth,
                    false,
                    CreateSnapshot(monster),
                    HealthMutation: null);
                return true;
            }

            monster.CurrentHealth = damage >= beforeHealth
                ? 0
                : beforeHealth - damage;
            monster.HealthRevision = checked(beforeHealthRevision + 1);
            var killed = monster.CurrentHealth == 0;
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
                var respawnDelay = WorldBossCatalog.Default.IsWorldBoss(
                    MapId,
                    monster.Definition.TemplateKey)
                    ? WorldBossCatalog.Default.RespawnInterval
                    : _respawnDelay;
                monster.RespawnAt = now + respawnDelay;
                _pendingUpdates.Enqueue(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Died,
                    CreateSnapshot(monster)));
            }
            else if (attackerCharacterId is > 0 &&
                     monster.AggroCharacterId != attackerCharacterId)
            {
                monster.AggroCharacterId = attackerCharacterId;
                monster.CombatPhase = MonsterCombatPhase.None;
                monster.HasSentInitialChase = false;
                monster.IsMoving = false;
                monster.VelocityX = 0;
                monster.VelocityZ = 0;
                monster.MovementTicks = 0;
                monster.RemainingMovementTicks = 0;
                monster.NextAttackAt = now + TickInterval;
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
                    monster.HealthRevision));
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
            monster.AggroCharacterId = attackerCharacterId;
            monster.CombatPhase = MonsterCombatPhase.None;
            monster.HasSentInitialChase = false;
            monster.StunnedUntil = stunnedUntil;
            monster.NextAttackAt = stunnedUntil + TickInterval;
            monster.NextMovementStepAt = stunnedUntil + TickInterval;

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
            foreach (var monster in _monsters.Values.Where(monster => monster.AggroCharacterId == characterId))
            {
                _pendingUpdates.Enqueue(BeginReturnHome(monster, now));
            }
        }
    }
}
