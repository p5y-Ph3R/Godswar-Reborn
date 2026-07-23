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
        var beforeHealth = vitals.CurrentHealth;
        var beforeHealthRevision = vitals.HealthRevision;

        if (!vitals.IsAlive ||
            !vitals.IsSpawned ||
            damage == 0 ||
            combat.Phase is MonsterCombatPhase.Returning or
                MonsterCombatPhase.AwaitingRetirement)
        {
            result = new MonsterDamageResult(
                world.Get<MonsterIdentityComponent>(entity).Definition.ObjectId,
                beforeHealth,
                beforeHealth,
                false,
                MonsterEcsState.Snapshot(world, entity),
                HealthMutation: null);
            return true;
        }

        vitals.CurrentHealth = damage >= beforeHealth
            ? 0
            : beforeHealth - damage;
        vitals.HealthRevision = checked(beforeHealthRevision + 1);
        var killed = vitals.CurrentHealth == 0;
        if (killed)
        {
            MonsterEcsState.ResetCombat(world, entity, now);
            vitals.IsAlive = false;
            movement.IsMoving = false;
            movement.VelocityX = 0;
            movement.VelocityZ = 0;
            movement.MovementTicks = 0;
            movement.RemainingMovementTicks = 0;
            lifecycle.DespawnAt = now + lifecycle.CorpseDespawnDelay;
            lifecycle.RespawnAt = now + lifecycle.RespawnDelay;
            pendingUpdates.Enqueue(new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Died,
                MonsterEcsState.Snapshot(world, entity)));
        }
        else if (attackerCharacterId is > 0 &&
                 combat.AggroCharacterId != attackerCharacterId)
        {
            combat.AggroCharacterId = attackerCharacterId;
            combat.Phase = MonsterCombatPhase.None;
            combat.HasSentInitialChase = false;
            movement.IsMoving = false;
            movement.VelocityX = 0;
            movement.VelocityZ = 0;
            movement.MovementTicks = 0;
            movement.RemainingMovementTicks = 0;
            combat.NextAttackAt = now + MonsterEcsRules.TickInterval;
        }

        var objectId = world.Get<MonsterIdentityComponent>(entity)
            .Definition.ObjectId;
        result = new MonsterDamageResult(
            objectId,
            beforeHealth,
            vitals.CurrentHealth,
            killed,
            MonsterEcsState.Snapshot(world, entity),
            new MonsterHealthMutation(
                objectId,
                vitals.SpawnGeneration,
                beforeHealthRevision,
                vitals.HealthRevision));
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

        combat.AggroCharacterId = attackerCharacterId;
        combat.Phase = MonsterCombatPhase.None;
        combat.HasSentInitialChase = false;
        combat.StunnedUntil = stunnedUntil;
        combat.NextAttackAt =
            stunnedUntil + MonsterEcsRules.TickInterval;
        movement.NextMovementStepAt =
            stunnedUntil + MonsterEcsRules.TickInterval;

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
