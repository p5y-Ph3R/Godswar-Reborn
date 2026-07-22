using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class MonsterMapRuntime
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

    public MonsterMapRuntime(
        byte mapId,
        IEnumerable<CapturedMonsterSpawn> definitions,
        DateTimeOffset initializedAt,
        TimeSpan? corpseDespawnDelay = null,
        TimeSpan? respawnDelay = null,
        WorldBossRespawnState? activeWorldBossRespawn = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        MapId = mapId;
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
                    activeWorldBossRespawn));
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

    public MonsterRuntimeTick Advance(
        DateTimeOffset now,
        IReadOnlyList<MonsterCombatTarget>? combatTargets = null)
    {
        lock (_gate)
        {
            var targetsByCharacterId = (combatTargets ?? [])
                .GroupBy(target => target.CharacterId)
                .ToDictionary(group => group.Key, group => group.Last());
            var updates = new List<MonsterRuntimeUpdate>(_pendingUpdates.Count + 4);
            var deathsAnnouncedThisTick = new HashSet<uint>();
            var returnStartsAnnouncedThisTick = new HashSet<uint>();
            while (_pendingUpdates.TryDequeue(out var pendingUpdate))
            {
                updates.Add(pendingUpdate);
                if (pendingUpdate.Kind == MonsterRuntimeUpdateKind.Died)
                {
                    deathsAnnouncedThisTick.Add(pendingUpdate.Monster.ObjectId);
                }
                else if (pendingUpdate.Kind == MonsterRuntimeUpdateKind.Started &&
                         pendingUpdate.Monster.CombatPhase == MonsterCombatPhase.Returning)
                {
                    returnStartsAnnouncedThisTick.Add(pendingUpdate.Monster.ObjectId);
                }
            }

            var positionsChanged = false;
            List<MonsterRuntimeState>? respawnedStates = null;
            foreach (var monster in _monsters.Values.OrderBy(monster => monster.Definition.ObjectId))
            {
                if (!monster.IsAlive)
                {
                    if (deathsAnnouncedThisTick.Contains(monster.Definition.ObjectId))
                    {
                        continue;
                    }

                    if (monster.IsSpawned &&
                        monster.DespawnAt is { } despawnAt &&
                        now >= despawnAt)
                    {
                        monster.IsSpawned = false;
                        updates.Add(new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Despawned,
                            CreateSnapshot(monster)));
                        continue;
                    }

                    if (!monster.IsSpawned &&
                        monster.RespawnAt is { } respawnAt &&
                        now >= respawnAt)
                    {
                        var respawned = CreateRespawnedState(monster, now);
                        (respawnedStates ??= []).Add(respawned);
                        positionsChanged = true;
                        updates.Add(new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Respawned,
                            CreateSnapshot(respawned)));
                    }

                    continue;
                }

                if (monster.StunnedUntil is { } stunnedUntil)
                {
                    if (now < stunnedUntil)
                    {
                        continue;
                    }

                    monster.StunnedUntil = null;
                    monster.NextAttackAt = now + TickInterval;
                    monster.NextMovementStepAt = now + TickInterval;
                }

                if (monster.AggroCharacterId is { } aggroCharacterId)
                {
                    if (targetsByCharacterId.TryGetValue(aggroCharacterId, out var combatTarget) &&
                        combatTarget.IsAlive &&
                        DistanceSquared(monster.HomeX, monster.HomeZ, combatTarget.X, combatTarget.Z) <=
                        (CombatLeashRadius + CombatRange) * (CombatLeashRadius + CombatRange))
                    {
                        positionsChanged |= AdvanceCombat(monster, combatTarget, now, updates);
                        continue;
                    }

                    AddReturnStart(monster, now, updates);
                    continue;
                }

                if (monster.CombatPhase == MonsterCombatPhase.Returning)
                {
                    if (!returnStartsAnnouncedThisTick.Contains(monster.Definition.ObjectId))
                    {
                        positionsChanged |= AdvanceReturnHome(monster, now, updates);
                    }

                    continue;
                }

                if (monster.CombatPhase == MonsterCombatPhase.AwaitingRetirement)
                {
                    updates.Add(RetireReturnedMonster(monster, now));
                    continue;
                }

                if (monster.IsMoving)
                {
                    while (monster.IsMoving && now >= monster.NextMovementStepAt)
                    {
                        var stepAt = monster.NextMovementStepAt;
                        monster.CurrentX += monster.VelocityX;
                        monster.CurrentZ += monster.VelocityZ;
                        monster.RemainingMovementTicks--;
                        monster.NextMovementStepAt += TickInterval;
                        positionsChanged = true;

                        if (monster.RemainingMovementTicks != 0)
                        {
                            continue;
                        }

                        // Pin the final coordinates to the accepted target so float
                        // accumulation can never drift beyond the home-radius bound.
                        monster.CurrentX = monster.TargetX;
                        monster.CurrentZ = monster.TargetZ;
                        monster.IsMoving = false;
                        monster.NextMovementAt = stepAt + NextIdleDelay(monster);
                        updates.Add(new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Arrived,
                            CreateSnapshot(monster)));
                    }

                    continue;
                }

                if (now < monster.NextMovementAt)
                {
                    continue;
                }

                StartMovement(monster, now);
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Started,
                    CreateSnapshot(monster)));
            }

            if (respawnedStates is not null)
            {
                foreach (var respawned in respawnedStates)
                {
                    _monsters[respawned.Definition.ObjectId] = respawned;
                }
            }

            return new MonsterRuntimeTick(positionsChanged, updates);
        }
    }

    private static bool AdvanceCombat(
        MonsterRuntimeState monster,
        MonsterCombatTarget target,
        DateTimeOffset now,
        List<MonsterRuntimeUpdate> updates)
    {
        var positionsChanged = false;
        var distance = Math.Sqrt(DistanceSquared(monster.CurrentX, monster.CurrentZ, target.X, target.Z));
        if (distance <= CombatRange)
        {
            if (monster.CombatPhase == MonsterCombatPhase.Chasing || monster.IsMoving)
            {
                StopCombatMovement(monster);
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = now + TickInterval;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Arrived,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                return false;
            }

            if (monster.CombatPhase != MonsterCombatPhase.Attacking)
            {
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = now + TickInterval;
                return false;
            }

            if (now >= monster.NextAttackAt)
            {
                monster.NextAttackAt = now + AttackCooldown;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Attacked,
                    CreateSnapshot(monster),
                    TargetCharacterId: target.CharacterId,
                    TargetX: target.X,
                    TargetZ: target.Z));
            }

            return false;
        }

        if (monster.CombatPhase != MonsterCombatPhase.Chasing)
        {
            monster.CombatPhase = MonsterCombatPhase.Chasing;
            monster.HasSentInitialChase = true;
            monster.IsMoving = true;
            monster.MovementTicks = 1;
            monster.RemainingMovementTicks = 1;
            SetCombatVelocity(monster, target);
            monster.NextMovementStepAt = now + TickInterval;
            updates.Add(new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Started,
                CreateSnapshot(monster),
                MovementMode: 0));
            return false;
        }

        while (now >= monster.NextMovementStepAt)
        {
            var stepAt = monster.NextMovementStepAt;
            distance = Math.Sqrt(DistanceSquared(monster.CurrentX, monster.CurrentZ, target.X, target.Z));
            var remainingDistance = Math.Max(0d, distance - CombatRange);
            if (remainingDistance <= double.Epsilon)
            {
                StopCombatMovement(monster);
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = stepAt + TickInterval;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Arrived,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                break;
            }

            SetCombatVelocity(monster, target, Math.Min(MovementStep, (float)remainingDistance));
            var nextX = monster.CurrentX + monster.VelocityX;
            var nextZ = monster.CurrentZ + monster.VelocityZ;
            if (DistanceSquared(monster.HomeX, monster.HomeZ, nextX, nextZ) >
                CombatLeashRadius * CombatLeashRadius)
            {
                AddReturnStart(monster, stepAt, updates);
                break;
            }

            monster.CurrentX = nextX;
            monster.CurrentZ = nextZ;
            monster.NextMovementStepAt += TickInterval;
            positionsChanged = true;

            distance = Math.Sqrt(DistanceSquared(monster.CurrentX, monster.CurrentZ, target.X, target.Z));
            if (distance <= CombatRange + 0.0001d)
            {
                StopCombatMovement(monster);
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = stepAt + TickInterval;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Arrived,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                break;
            }

            SetCombatVelocity(monster, target);
            updates.Add(new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Started,
                CreateSnapshot(monster),
                MovementMode: 1));
        }

        return positionsChanged;
    }

    private static bool AdvanceReturnHome(
        MonsterRuntimeState monster,
        DateTimeOffset now,
        List<MonsterRuntimeUpdate> updates)
    {
        var positionsChanged = false;
        while (monster.CombatPhase == MonsterCombatPhase.Returning &&
               now >= monster.NextMovementStepAt)
        {
            var stepAt = monster.NextMovementStepAt;
            if (monster.RemainingMovementTicks <= 1)
            {
                positionsChanged |= DistanceSquared(
                    monster.CurrentX,
                    monster.CurrentZ,
                    monster.HomeX,
                    monster.HomeZ) > double.Epsilon;
                CompleteReturnHome(monster, stepAt);
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Returned,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                updates.Add(RetireReturnedMonster(monster, stepAt));
                break;
            }

            monster.CurrentX += monster.VelocityX;
            monster.CurrentZ += monster.VelocityZ;
            monster.RemainingMovementTicks--;
            monster.NextMovementStepAt += TickInterval;
            positionsChanged = true;
        }

        return positionsChanged;
    }

    private static void SetCombatVelocity(
        MonsterRuntimeState monster,
        MonsterCombatTarget target,
        float step = MovementStep)
    {
        var deltaX = target.X - monster.CurrentX;
        var deltaZ = target.Z - monster.CurrentZ;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (distance <= double.Epsilon)
        {
            monster.VelocityX = 0;
            monster.VelocityZ = 0;
            return;
        }

        monster.VelocityX = (float)((deltaX / distance) * step);
        monster.VelocityZ = (float)((deltaZ / distance) * step);
        monster.Facing = MathF.Atan2(monster.VelocityX, monster.VelocityZ);
    }

    private static void StopCombatMovement(MonsterRuntimeState monster)
    {
        monster.IsMoving = false;
        monster.VelocityX = 0;
        monster.VelocityZ = 0;
        monster.MovementTicks = 1;
        monster.RemainingMovementTicks = 0;
    }

    private static MonsterRuntimeUpdate BeginReturnHome(
        MonsterRuntimeState monster,
        DateTimeOffset now)
    {
        monster.StunnedUntil = null;
        monster.AggroCharacterId = null;
        monster.HasSentInitialChase = false;
        monster.NextAttackAt = default;
        monster.DespawnAt = null;
        monster.RespawnAt = null;
        var deltaX = monster.HomeX - monster.CurrentX;
        var deltaZ = monster.HomeZ - monster.CurrentZ;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (distance <= 0.0001d)
        {
            CompleteReturnHome(monster, now);
            return new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Returned,
                CreateSnapshot(monster),
                MovementEndField: 1);
        }

        monster.CombatPhase = MonsterCombatPhase.Returning;
        var movementTicks = Math.Max(1, checked((int)Math.Ceiling(distance / MovementStep)));
        var movementStep = distance / movementTicks;
        SetMovement(
            monster,
            now,
            movementTicks,
            (float)((deltaX / distance) * movementStep),
            (float)((deltaZ / distance) * movementStep),
            monster.HomeX,
            monster.HomeZ);
        return new MonsterRuntimeUpdate(
            MonsterRuntimeUpdateKind.Started,
            CreateSnapshot(monster),
            MovementMode: 0);
    }

    private static void AddReturnStart(
        MonsterRuntimeState monster,
        DateTimeOffset now,
        List<MonsterRuntimeUpdate> updates)
    {
        var returnUpdate = BeginReturnHome(monster, now);
        updates.Add(returnUpdate);
        if (returnUpdate.Kind == MonsterRuntimeUpdateKind.Returned)
        {
            updates.Add(RetireReturnedMonster(monster, now));
        }
    }

    private static void CompleteReturnHome(MonsterRuntimeState monster, DateTimeOffset now)
    {
        monster.StunnedUntil = null;
        monster.AggroCharacterId = null;
        monster.CombatPhase = MonsterCombatPhase.AwaitingRetirement;
        monster.HasSentInitialChase = false;
        monster.NextAttackAt = default;
        monster.CurrentX = monster.HomeX;
        monster.CurrentZ = monster.HomeZ;
        monster.Facing = monster.HomeFacing;
        StopCombatMovement(monster);
        monster.MovementTicks = 0;
        monster.TargetX = monster.HomeX;
        monster.TargetZ = monster.HomeZ;
        monster.NextMovementAt = now + NextIdleDelay(monster);
    }

    private static MonsterRuntimeUpdate RetireReturnedMonster(
        MonsterRuntimeState monster,
        DateTimeOffset now)
    {
        // Keep the damaged entity visible through its immutable exact-home
        // Returned snapshot, then retire it later in the same ordered update
        // batch. The following world tick publishes a new full-health runtime
        // generation through the normal spawn path.
        monster.IsAlive = false;
        monster.IsSpawned = false;
        monster.DespawnAt = null;
        monster.RespawnAt = now + TickInterval;
        return new MonsterRuntimeUpdate(
            MonsterRuntimeUpdateKind.Despawned,
            CreateSnapshot(monster));
    }

    private static void ResetCombat(MonsterRuntimeState monster, DateTimeOffset now)
    {
        monster.StunnedUntil = null;
        monster.AggroCharacterId = null;
        monster.CombatPhase = MonsterCombatPhase.None;
        monster.HasSentInitialChase = false;
        StopCombatMovement(monster);
        monster.MovementTicks = 0;
        monster.NextAttackAt = default;
        monster.NextMovementAt = now + NextIdleDelay(monster);
    }

    private static MonsterRuntimeState CreateState(
        byte mapId,
        CapturedMonsterSpawn definition,
        DateTimeOffset initializedAt,
        WorldBossRespawnState? activeWorldBossRespawn)
    {
        if (definition.MapId != mapId)
        {
            throw new ArgumentException(
                $"Monster {definition.ObjectId} belongs to map {definition.MapId}, not runtime map {mapId}.",
                nameof(definition));
        }

        var packet = definition.Packet;
        if (packet.Length < 44)
        {
            throw new ArgumentException(
                $"Monster {definition.ObjectId} appearance packet is too short.",
                nameof(definition));
        }

        var monster = new MonsterRuntimeState(
            definition,
            definition.AppearanceX,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(32, 4)),
            definition.AppearanceZ,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(40, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24, 4)),
            CreateSeed(mapId, definition.ObjectId),
            spawnGeneration: 1);
        if (activeWorldBossRespawn is not null &&
            activeWorldBossRespawn.MapId == mapId &&
            activeWorldBossRespawn.RespawnAt > initializedAt &&
            string.Equals(
                activeWorldBossRespawn.BossTemplateKey,
                definition.TemplateKey,
                StringComparison.Ordinal))
        {
            monster.CurrentHealth = 0;
            monster.IsAlive = false;
            monster.IsSpawned = false;
            monster.RespawnAt = activeWorldBossRespawn.RespawnAt;
        }

        monster.NextMovementAt = initializedAt + NextIdleDelay(monster);
        return monster;
    }

    private static void StartMovement(MonsterRuntimeState monster, DateTimeOffset now)
    {
        var selected = false;
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var ticks = MinimumMovementTicks +
                        (int)(NextRandom(monster) %
                              (MaximumMovementTicks - MinimumMovementTicks + 1));
            var angle = NextUnit(monster) * Math.Tau;
            var velocityX = (float)(Math.Sin(angle) * MovementStep);
            var velocityZ = (float)(Math.Cos(angle) * MovementStep);
            var targetX = monster.CurrentX + (velocityX * ticks);
            var targetZ = monster.CurrentZ + (velocityZ * ticks);
            if (DistanceSquared(monster.HomeX, monster.HomeZ, targetX, targetZ) >
                (MaximumRoamRadius * MaximumRoamRadius))
            {
                continue;
            }

            SetMovement(monster, now, ticks, velocityX, velocityZ, targetX, targetZ);
            selected = true;
            break;
        }

        if (selected)
        {
            return;
        }

        // A valid inward one-step move always exists, including from the radius
        // boundary. This fallback also makes the bound independent of RNG quality.
        var towardHomeX = monster.HomeX - monster.CurrentX;
        var towardHomeZ = monster.HomeZ - monster.CurrentZ;
        var distance = Math.Sqrt((towardHomeX * towardHomeX) + (towardHomeZ * towardHomeZ));
        var velocityXFallback = distance > double.Epsilon
            ? (float)((towardHomeX / distance) * MovementStep)
            : MovementStep;
        var velocityZFallback = distance > double.Epsilon
            ? (float)((towardHomeZ / distance) * MovementStep)
            : 0f;
        SetMovement(
            monster,
            now,
            MinimumMovementTicks,
            velocityXFallback,
            velocityZFallback,
            monster.CurrentX + velocityXFallback,
            monster.CurrentZ + velocityZFallback);
    }

    private static void SetMovement(
        MonsterRuntimeState monster,
        DateTimeOffset now,
        int ticks,
        float velocityX,
        float velocityZ,
        float targetX,
        float targetZ)
    {
        monster.IsMoving = true;
        monster.MovementTicks = checked((uint)ticks);
        monster.RemainingMovementTicks = checked((uint)ticks);
        monster.VelocityX = velocityX;
        monster.VelocityZ = velocityZ;
        monster.TargetX = targetX;
        monster.TargetZ = targetZ;
        monster.Facing = MathF.Atan2(velocityX, velocityZ);
        monster.NextMovementStepAt = now + TickInterval;
    }

    private static MonsterRuntimeState CreateRespawnedState(
        MonsterRuntimeState retired,
        DateTimeOffset now)
    {
        var respawned = new MonsterRuntimeState(
            retired.Definition,
            retired.HomeX,
            retired.CurrentY,
            retired.HomeZ,
            retired.HomeFacing,
            retired.MaximumHealth,
            retired.MaximumHealth,
            retired.RandomState,
            checked(retired.SpawnGeneration + 1));
        respawned.NextMovementAt = now + NextIdleDelay(respawned);
        return respawned;
    }

    private static MonsterRuntimeSnapshot CreateSnapshot(MonsterRuntimeState monster)
    {
        return new MonsterRuntimeSnapshot(
            monster.Definition,
            monster.HomeX,
            monster.HomeZ,
            monster.CurrentX,
            monster.CurrentY,
            monster.CurrentZ,
            monster.Facing,
            monster.CurrentHealth,
            monster.MaximumHealth,
            monster.IsAlive,
            monster.IsSpawned,
            monster.IsMoving,
            monster.VelocityX,
            0f,
            monster.VelocityZ,
            monster.MovementTicks,
            monster.RemainingMovementTicks,
            monster.NextMovementAt,
            monster.DespawnAt,
            monster.RespawnAt,
            monster.CombatPhase,
            monster.StunnedUntil,
            monster.SpawnGeneration,
            monster.HealthRevision);
    }

    private static TimeSpan NextIdleDelay(MonsterRuntimeState monster)
    {
        var idleTicks = MinimumIdleTicks +
                        (int)(NextRandom(monster) %
                              (MaximumIdleTicks - MinimumIdleTicks + 1));
        return TimeSpan.FromSeconds(idleTicks / (double)TicksPerSecond);
    }

    private static uint CreateSeed(byte mapId, uint objectId)
    {
        var seed = unchecked((objectId * 0x9E3779B9u) ^ ((uint)mapId << 24) ^ 0xA341316Cu);
        return seed == 0 ? 0x6D2B79F5u : seed;
    }

    private static uint NextRandom(MonsterRuntimeState monster)
    {
        var value = monster.RandomState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        monster.RandomState = value;
        return value;
    }

    private static double NextUnit(MonsterRuntimeState monster)
    {
        return NextRandom(monster) / (uint.MaxValue + 1d);
    }

    private static double DistanceSquared(float x1, float z1, float x2, float z2)
    {
        var deltaX = (double)x2 - x1;
        var deltaZ = (double)z2 - z1;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private sealed class MonsterRuntimeState
    {
        public MonsterRuntimeState(
            CapturedMonsterSpawn definition,
            float homeX,
            float currentY,
            float homeZ,
            float homeFacing,
            uint currentHealth,
            uint maximumHealth,
            uint randomState,
            uint spawnGeneration)
        {
            Definition = definition;
            HomeX = homeX;
            CurrentX = homeX;
            CurrentY = currentY;
            HomeZ = homeZ;
            CurrentZ = homeZ;
            HomeFacing = homeFacing;
            Facing = homeFacing;
            CurrentHealth = currentHealth;
            MaximumHealth = maximumHealth;
            RandomState = randomState;
            SpawnGeneration = spawnGeneration;
        }

        public CapturedMonsterSpawn Definition { get; }
        public float HomeX { get; }
        public float CurrentY { get; }
        public float HomeZ { get; }
        public float HomeFacing { get; }
        public float CurrentX { get; set; }
        public float CurrentZ { get; set; }
        public float Facing { get; set; }
        public uint CurrentHealth { get; set; }
        public uint MaximumHealth { get; }
        public bool IsAlive { get; set; } = true;
        public bool IsSpawned { get; set; } = true;
        public bool IsMoving { get; set; }
        public float VelocityX { get; set; }
        public float VelocityZ { get; set; }
        public float TargetX { get; set; }
        public float TargetZ { get; set; }
        public uint MovementTicks { get; set; }
        public uint RemainingMovementTicks { get; set; }
        public DateTimeOffset NextMovementAt { get; set; }
        public DateTimeOffset NextMovementStepAt { get; set; }
        public DateTimeOffset? DespawnAt { get; set; }
        public DateTimeOffset? RespawnAt { get; set; }
        public uint RandomState { get; set; }
        public uint SpawnGeneration { get; }
        public ulong HealthRevision { get; set; }
        public int? AggroCharacterId { get; set; }
        public MonsterCombatPhase CombatPhase { get; set; }
        public bool HasSentInitialChase { get; set; }
        public DateTimeOffset NextAttackAt { get; set; }
        public DateTimeOffset? StunnedUntil { get; set; }
    }
}

internal readonly record struct MonsterCombatTarget(
    int CharacterId,
    float X,
    float Z,
    bool IsAlive);

internal enum MonsterCombatPhase
{
    None,
    Chasing,
    Attacking,
    Returning,
    AwaitingRetirement
}

internal sealed record MonsterRuntimeSnapshot(
    CapturedMonsterSpawn Definition,
    float HomeX,
    float HomeZ,
    float X,
    float Y,
    float Z,
    float Facing,
    uint CurrentHealth,
    uint MaximumHealth,
    bool IsAlive,
    bool IsSpawned,
    bool IsMoving,
    float VelocityX,
    float VelocityY,
    float VelocityZ,
    uint MovementTicks,
    uint RemainingMovementTicks,
    DateTimeOffset NextMovementAt,
    DateTimeOffset? DespawnAt,
    DateTimeOffset? RespawnAt,
    MonsterCombatPhase CombatPhase,
    DateTimeOffset? StunnedUntil,
    uint SpawnGeneration,
    ulong HealthRevision)
{
    public uint ObjectId => Definition.ObjectId;

    public bool IsStunned => StunnedUntil is not null;

    public MonsterAppearanceVersion AppearanceVersion => new(
        SpawnGeneration,
        HealthRevision);

    public CapturedMonsterAppearanceState Appearance => new(
        Definition,
        X,
        Z,
        Facing,
        CurrentHealth,
        MaximumHealth);
}

internal enum MonsterRuntimeUpdateKind
{
    Started,
    Arrived,
    Attacked,
    Returned,
    Died,
    Despawned,
    Respawned
}

internal sealed record MonsterRuntimeUpdate(
    MonsterRuntimeUpdateKind Kind,
    MonsterRuntimeSnapshot Monster,
    uint MovementMode = 1,
    uint? MovementEndField = null,
    int? TargetCharacterId = null,
    float TargetX = 0,
    float TargetZ = 0);

internal sealed record MonsterRuntimeTick(
    bool PositionsChanged,
    IReadOnlyList<MonsterRuntimeUpdate> Updates);

internal sealed record MonsterDamageResult(
    uint ObjectId,
    uint BeforeHealth,
    uint AfterHealth,
    bool Killed,
    MonsterRuntimeSnapshot Monster,
    MonsterHealthMutation? HealthMutation);

internal readonly record struct MonsterAppearanceVersion(
    uint SpawnGeneration,
    ulong HealthRevision);

internal readonly record struct MonsterHealthMutation(
    uint ObjectId,
    uint SpawnGeneration,
    ulong BeforeHealthRevision,
    ulong AfterHealthRevision)
{
    public MonsterAppearanceVersion BeforeVersion => new(
        SpawnGeneration,
        BeforeHealthRevision);

    public MonsterAppearanceVersion AfterVersion => new(
        SpawnGeneration,
        AfterHealthRevision);
}

internal sealed record MonsterStunResult(
    uint ObjectId,
    bool Applied,
    DateTimeOffset? StunnedUntil,
    MonsterRuntimeSnapshot Monster);
