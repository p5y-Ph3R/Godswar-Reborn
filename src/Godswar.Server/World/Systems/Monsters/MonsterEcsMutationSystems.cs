using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

internal static class MonsterEcsDamageSystem
{
    public static bool TryApply(
        EcsWorld world,
        EntityId entity,
        uint damage,
        int? attackerCharacterId,
        uint? expectedSpawnGeneration,
        bool periodic,
        DateTimeOffset now,
        Queue<MonsterRuntimeUpdate> pendingUpdates,
        out MonsterDamageResult result)
    {
        ref var vitals = ref world.Get<MonsterVitalsComponent>(entity);
        if (expectedSpawnGeneration is { } expectedGeneration &&
            vitals.SpawnGeneration != expectedGeneration)
        {
            result = default!;
            return false;
        }

        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        ref var lifecycle = ref world.Get<MonsterLifecycleComponent>(entity);
        ref var random = ref world.Get<MonsterRandomComponent>(entity);
        var beforeHealth = vitals.CurrentHealth;
        var beforeHealthRevision = vitals.HealthRevision;
        var beforeSnapshot = MonsterEcsState.Snapshot(world, entity);

        if (!vitals.IsAlive ||
            !vitals.IsSpawned ||
            damage == 0 ||
            !periodic &&
            combat.Phase is (
                MonsterCombatPhase.Returning or
                MonsterCombatPhase.AwaitingRetirement))
        {
            result = new MonsterDamageResult(
                world.Get<MonsterIdentityComponent>(entity).Definition.ObjectId,
                beforeHealth,
                beforeHealth,
                false,
                beforeSnapshot,
                HealthMutation: null,
                combat.FirstHitCharacterId);
            return true;
        }

        // Everything that can allocate, overflow, or reject lifecycle policy
        // is prepared before the first ECS scalar is transferred. The commit
        // below is assignments plus an enqueue into reserved storage only.
        var afterHealth = damage >= beforeHealth
            ? 0
            : beforeHealth - damage;
        var afterHealthRevision = checked(beforeHealthRevision + 1);
        var killed = afterHealth == 0;
        var firstHitCharacterId = combat.FirstHitCharacterId;
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
            combat.AggroCharacterId.GetValueOrDefault();
        if (!killed && !periodic && attackerCharacterId is > 0)
        {
            nextThreat = MonsterAggroPolicy.RecordDamage(
                combat.DamageThreat,
                attackerCharacterId.Value,
                beforeHealth - afterHealth,
                combat.AggroCharacterId,
                out var leaderCharacterId);
            nextAggroCharacterId = leaderCharacterId;
        }
        var changesAggro = nextThreat is not null &&
            combat.AggroCharacterId != nextAggroCharacterId;
        DateTimeOffset? despawnAt = null;
        DateTimeOffset? respawnAt = null;
        var nextMovementAt = movement.NextMovementAt;
        var randomAfter = random;
        if (killed)
        {
            nextMovementAt = now +
                MonsterEcsRandom.NextIdleDelay(ref randomAfter);
            despawnAt = now + lifecycle.CorpseDespawnDelay;
            respawnAt = lifecycle.RespawnPolicy switch
            {
                MonsterRespawnPolicy.Timed => now +
                    lifecycle.RespawnDelay!.Value,
                MonsterRespawnPolicy.Never => null,
                _ => throw new InvalidOperationException(
                    "Monster lifecycle contains an unsupported respawn policy.")
            };
        }
        var afterSnapshot = beforeSnapshot with
        {
            CurrentHealth = afterHealth,
            IsAlive = !killed,
            IsMoving = killed || changesAggro
                ? false
                : beforeSnapshot.IsMoving,
            VelocityX = killed || changesAggro
                ? 0
                : beforeSnapshot.VelocityX,
            VelocityZ = killed || changesAggro
                ? 0
                : beforeSnapshot.VelocityZ,
            MovementTicks = killed || changesAggro
                ? 0
                : beforeSnapshot.MovementTicks,
            RemainingMovementTicks = killed || changesAggro
                ? 0
                : beforeSnapshot.RemainingMovementTicks,
            NextMovementAt = killed
                ? nextMovementAt
                : beforeSnapshot.NextMovementAt,
            DespawnAt = killed ? despawnAt : beforeSnapshot.DespawnAt,
            RespawnAt = killed ? respawnAt : beforeSnapshot.RespawnAt,
            CombatPhase = killed || changesAggro
                ? MonsterCombatPhase.None
                : beforeSnapshot.CombatPhase,
            StunnedUntil = killed
                ? null
                : beforeSnapshot.StunnedUntil,
            HealthRevision = afterHealthRevision
        };
        var mutation = new MonsterHealthMutation(
            beforeSnapshot.ObjectId,
            vitals.SpawnGeneration,
            beforeHealthRevision,
            afterHealthRevision);
        var preparedResult = new MonsterDamageResult(
            beforeSnapshot.ObjectId,
            beforeHealth,
            afterHealth,
            killed,
            afterSnapshot,
            mutation,
            firstHitCharacterId,
            claimEstablished);
        MonsterRuntimeUpdate? died = null;
        if (killed)
        {
            died = new(
                MonsterRuntimeUpdateKind.Died,
                afterSnapshot);
            _ = pendingUpdates.EnsureCapacity(
                checked(pendingUpdates.Count + 1));
        }

        vitals.CurrentHealth = afterHealth;
        vitals.HealthRevision = afterHealthRevision;
        combat.FirstHitCharacterId = firstHitCharacterId;
        if (killed)
        {
            vitals.IsAlive = false;
            combat.StunnedUntil = null;
            combat.AggroCharacterId = null;
            combat.FirstHitCharacterId = null;
            combat.DamageThreat = null;
            combat.Phase = MonsterCombatPhase.None;
            combat.HasSentInitialChase = false;
            combat.NextAttackAt = default;
            movement.IsMoving = false;
            movement.VelocityX = 0;
            movement.VelocityZ = 0;
            movement.MovementTicks = 0;
            movement.RemainingMovementTicks = 0;
            movement.NextMovementAt = nextMovementAt;
            random = randomAfter;
            lifecycle.DespawnAt = despawnAt;
            lifecycle.RespawnAt = respawnAt;
            pendingUpdates.Enqueue(died!);
        }
        else if (nextThreat is not null)
        {
            combat.DamageThreat = nextThreat;
            if (changesAggro)
            {
                MonsterEcsState.SetAggroTarget(
                    ref movement,
                    ref combat,
                    nextAggroCharacterId,
                    now);
            }
        }

        result = preparedResult;
        return true;
    }
}

internal static class MonsterEcsStunSystem
{
    public static bool TryApply(
        EcsWorld world,
        EntityId entity,
        int attackerCharacterId,
        TimeSpan duration,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        Queue<MonsterRuntimeUpdate> pendingUpdates,
        out MonsterStunResult result)
    {
        ref var vitals = ref world.Get<MonsterVitalsComponent>(entity);
        if (expectedSpawnGeneration is { } expectedGeneration &&
            vitals.SpawnGeneration != expectedGeneration)
        {
            result = default!;
            return false;
        }

        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        var objectId = world.Get<MonsterIdentityComponent>(entity)
            .Definition.ObjectId;
        if (!vitals.IsAlive ||
            !vitals.IsSpawned ||
            combat.Phase is MonsterCombatPhase.Returning or
                MonsterCombatPhase.AwaitingRetirement)
        {
            result = new MonsterStunResult(
                objectId,
                Applied: false,
                combat.StunnedUntil,
                MonsterEcsState.Snapshot(world, entity));
            return true;
        }

        var stunnedUntil = now + duration;
        var wasMoving = movement.IsMoving;
        if (wasMoving)
        {
            MonsterEcsState.StopCombatMovement(ref movement);
        }

        combat.AggroCharacterId =
            MonsterAggroPolicy.SelectLeader(
                combat.DamageThreat,
                combat.AggroCharacterId) ??
            attackerCharacterId;
        combat.Phase = MonsterCombatPhase.None;
        combat.HasSentInitialChase = false;
        combat.StunnedUntil = stunnedUntil;
        combat.NextAttackAt =
            stunnedUntil + MonsterEcsRules.TickInterval;
        movement.NextMovementStepAt =
            stunnedUntil + MonsterEcsState.ElementalMovementInterval(
                in movement);

        if (wasMoving)
        {
            pendingUpdates.Enqueue(new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Arrived,
                MonsterEcsState.Snapshot(world, entity),
                MovementEndField: 1));
        }

        result = new MonsterStunResult(
            objectId,
            Applied: true,
            stunnedUntil,
            MonsterEcsState.Snapshot(world, entity));
        return true;
    }
}
