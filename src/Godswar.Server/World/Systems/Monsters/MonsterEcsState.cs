using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

internal static class MonsterEcsState
{
    public static MonsterRuntimeSnapshot Snapshot(
        EcsWorld world,
        EntityId entity)
    {
        ref var identity = ref world.Get<MonsterIdentityComponent>(entity);
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var vitals = ref world.Get<MonsterVitalsComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        ref var lifecycle = ref world.Get<MonsterLifecycleComponent>(entity);

        return new MonsterRuntimeSnapshot(
            identity.Definition,
            transform.HomeX,
            transform.HomeZ,
            transform.X,
            transform.Y,
            transform.Z,
            transform.Facing,
            vitals.CurrentHealth,
            vitals.MaximumHealth,
            vitals.IsAlive,
            vitals.IsSpawned,
            movement.IsMoving,
            movement.VelocityX,
            0f,
            movement.VelocityZ,
            movement.MovementTicks,
            movement.RemainingMovementTicks,
            movement.NextMovementAt,
            lifecycle.DespawnAt,
            lifecycle.RespawnAt,
            combat.Phase,
            combat.StunnedUntil,
            vitals.SpawnGeneration,
            vitals.HealthRevision,
            identity.RuntimeInstanceId);
    }

    public static void StopCombatMovement(
        ref MonsterMovementComponent movement)
    {
        movement.IsMoving = false;
        movement.VelocityX = 0;
        movement.VelocityZ = 0;
        movement.MovementTicks = 1;
        movement.RemainingMovementTicks = 0;
    }

    public static void SetMovement(
        ref MonsterTransformComponent transform,
        ref MonsterMovementComponent movement,
        DateTimeOffset now,
        int ticks,
        float velocityX,
        float velocityZ,
        float targetX,
        float targetZ)
    {
        movement.IsMoving = true;
        movement.MovementTicks = checked((uint)ticks);
        movement.RemainingMovementTicks = checked((uint)ticks);
        movement.VelocityX = velocityX;
        movement.VelocityZ = velocityZ;
        movement.TargetX = targetX;
        movement.TargetZ = targetZ;
        transform.Facing = MathF.Atan2(velocityX, velocityZ);
        movement.NextMovementStepAt =
            now + ElementalMovementInterval(in movement);
    }

    public static TimeSpan ElementalMovementInterval(
        in MonsterMovementComponent movement)
    {
        var configured = movement.MovementSpeedBasisPoints;
        var scale = configured <= 0
            ? 10_000
            : Math.Clamp(configured, 1, 10_000);
        return TimeSpan.FromTicks(checked(
            (MonsterEcsRules.TickInterval.Ticks * 10_000L) / scale));
    }

    public static void SetCombatVelocity(
        ref MonsterTransformComponent transform,
        ref MonsterMovementComponent movement,
        MonsterCombatTarget target,
        float step = MonsterEcsRules.MovementStep)
    {
        var deltaX = target.X - transform.X;
        var deltaZ = target.Z - transform.Z;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (distance <= double.Epsilon)
        {
            movement.VelocityX = 0;
            movement.VelocityZ = 0;
            return;
        }

        movement.VelocityX = (float)((deltaX / distance) * step);
        movement.VelocityZ = (float)((deltaZ / distance) * step);
        transform.Facing = MathF.Atan2(
            movement.VelocityX,
            movement.VelocityZ);
    }

    public static void ResetCombat(
        EcsWorld world,
        EntityId entity,
        DateTimeOffset now)
    {
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        ref var random = ref world.Get<MonsterRandomComponent>(entity);

        combat.StunnedUntil = null;
        combat.AggroCharacterId = null;
        combat.Phase = MonsterCombatPhase.None;
        combat.HasSentInitialChase = false;
        StopCombatMovement(ref movement);
        movement.MovementTicks = 0;
        combat.NextAttackAt = default;
        movement.NextMovementAt =
            now + MonsterEcsRandom.NextIdleDelay(ref random);
    }

    public static MonsterRuntimeUpdate BeginReturnHome(
        EcsWorld world,
        EntityId entity,
        DateTimeOffset now)
    {
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        ref var lifecycle = ref world.Get<MonsterLifecycleComponent>(entity);

        combat.StunnedUntil = null;
        combat.AggroCharacterId = null;
        combat.HasSentInitialChase = false;
        combat.NextAttackAt = default;
        lifecycle.DespawnAt = null;
        lifecycle.RespawnAt = null;

        var deltaX = transform.HomeX - transform.X;
        var deltaZ = transform.HomeZ - transform.Z;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (distance <= 0.0001d)
        {
            CompleteReturnHome(world, entity, now);
            return new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Returned,
                Snapshot(world, entity),
                MovementEndField: 1);
        }

        combat.Phase = MonsterCombatPhase.Returning;
        var movementTicks = Math.Max(
            1,
            checked((int)Math.Ceiling(
                distance / MonsterEcsRules.MovementStep)));
        var movementStep = distance / movementTicks;
        SetMovement(
            ref transform,
            ref movement,
            now,
            movementTicks,
            (float)((deltaX / distance) * movementStep),
            (float)((deltaZ / distance) * movementStep),
            transform.HomeX,
            transform.HomeZ);
        return new MonsterRuntimeUpdate(
            MonsterRuntimeUpdateKind.Started,
            Snapshot(world, entity),
            MovementMode: 0);
    }

    public static void CompleteReturnHome(
        EcsWorld world,
        EntityId entity,
        DateTimeOffset now)
    {
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        ref var random = ref world.Get<MonsterRandomComponent>(entity);

        combat.StunnedUntil = null;
        combat.AggroCharacterId = null;
        combat.Phase = MonsterCombatPhase.AwaitingRetirement;
        combat.HasSentInitialChase = false;
        combat.NextAttackAt = default;
        transform.X = transform.HomeX;
        transform.Z = transform.HomeZ;
        transform.Facing = transform.HomeFacing;
        StopCombatMovement(ref movement);
        movement.MovementTicks = 0;
        movement.TargetX = transform.HomeX;
        movement.TargetZ = transform.HomeZ;
        movement.NextMovementAt =
            now + MonsterEcsRandom.NextIdleDelay(ref random);
    }

    public static MonsterRuntimeUpdate RetireReturnedMonster(
        EcsWorld world,
        EntityId entity,
        DateTimeOffset now)
    {
        ref var vitals = ref world.Get<MonsterVitalsComponent>(entity);
        ref var lifecycle = ref world.Get<MonsterLifecycleComponent>(entity);
        vitals.IsAlive = false;
        vitals.IsSpawned = false;
        lifecycle.DespawnAt = null;
        lifecycle.RespawnAt = now + MonsterEcsRules.TickInterval;
        return new MonsterRuntimeUpdate(
            MonsterRuntimeUpdateKind.Despawned,
            Snapshot(world, entity));
    }

    public static double DistanceSquared(
        float x1,
        float z1,
        float x2,
        float z2)
    {
        var deltaX = (double)x2 - x1;
        var deltaZ = (double)z2 - z1;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }
}
