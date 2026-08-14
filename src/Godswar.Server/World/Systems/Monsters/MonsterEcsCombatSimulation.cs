using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

internal static class MonsterEcsCombatSimulation
{
    public static bool Advance(
        EcsWorld world,
        EntityId entity,
        MonsterCombatTarget target,
        DateTimeOffset now,
        EcsEventBuffer events)
    {
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        var positionsChanged = false;
        var distance = Math.Sqrt(MonsterEcsState.DistanceSquared(
            transform.X,
            transform.Z,
            target.X,
            target.Z));

        if (distance <= MonsterEcsRules.CombatRange)
        {
            if (combat.Phase == MonsterCombatPhase.Chasing ||
                movement.IsMoving)
            {
                MonsterEcsState.StopCombatMovement(ref movement);
                combat.Phase = MonsterCombatPhase.Attacking;
                combat.NextAttackAt =
                    now + MonsterEcsRules.TickInterval;
                Publish(
                    events,
                    new MonsterRuntimeUpdate(
                        MonsterRuntimeUpdateKind.Arrived,
                        MonsterEcsState.Snapshot(world, entity),
                        MovementEndField: 1));
                return false;
            }

            if (combat.Phase != MonsterCombatPhase.Attacking)
            {
                combat.Phase = MonsterCombatPhase.Attacking;
                combat.NextAttackAt =
                    now + MonsterEcsRules.TickInterval;
                return false;
            }

            if (now >= combat.NextAttackAt)
            {
                combat.NextAttackAt =
                    now + MonsterEcsRules.AttackCooldown;
                Publish(
                    events,
                    new MonsterRuntimeUpdate(
                        MonsterRuntimeUpdateKind.Attacked,
                        MonsterEcsState.Snapshot(world, entity),
                        TargetCharacterId: target.CharacterId,
                        TargetX: target.X,
                        TargetZ: target.Z,
                        TargetObjectId: target.ObjectId == 0
                            ? null
                            : target.ObjectId,
                        TargetLifeRevision: target.ObjectId == 0
                            ? null
                            : target.LifeRevision));
            }

            return false;
        }

        if (combat.Phase != MonsterCombatPhase.Chasing)
        {
            combat.Phase = MonsterCombatPhase.Chasing;
            combat.HasSentInitialChase = true;
            movement.IsMoving = true;
            movement.MovementTicks = 1;
            movement.RemainingMovementTicks = 1;
            MonsterEcsState.SetCombatVelocity(
                ref transform,
                ref movement,
                target);
            movement.NextMovementStepAt =
                now + MonsterEcsState.ElementalMovementInterval(
                    in movement);
            Publish(
                events,
                new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Started,
                    MonsterEcsState.Snapshot(world, entity),
                    MovementMode: 0));
            return false;
        }

        while (now >= movement.NextMovementStepAt)
        {
            var stepAt = movement.NextMovementStepAt;
            distance = Math.Sqrt(MonsterEcsState.DistanceSquared(
                transform.X,
                transform.Z,
                target.X,
                target.Z));
            var remainingDistance = Math.Max(
                0d,
                distance - MonsterEcsRules.CombatRange);
            if (remainingDistance <= double.Epsilon)
            {
                MonsterEcsState.StopCombatMovement(ref movement);
                combat.Phase = MonsterCombatPhase.Attacking;
                combat.NextAttackAt =
                    stepAt + MonsterEcsRules.TickInterval;
                Publish(
                    events,
                    new MonsterRuntimeUpdate(
                        MonsterRuntimeUpdateKind.Arrived,
                        MonsterEcsState.Snapshot(world, entity),
                        MovementEndField: 1));
                break;
            }

            MonsterEcsState.SetCombatVelocity(
                ref transform,
                ref movement,
                target,
                Math.Min(
                    MonsterEcsRules.MovementStep,
                    (float)remainingDistance));
            var nextX = transform.X + movement.VelocityX;
            var nextZ = transform.Z + movement.VelocityZ;
            if (MonsterEcsState.DistanceSquared(
                    transform.HomeX,
                    transform.HomeZ,
                    nextX,
                    nextZ) >
                MonsterEcsRules.CombatLeashRadius *
                MonsterEcsRules.CombatLeashRadius)
            {
                AddReturnStart(world, entity, stepAt, events);
                break;
            }

            transform.X = nextX;
            transform.Z = nextZ;
            movement.NextMovementStepAt +=
                MonsterEcsState.ElementalMovementInterval(in movement);
            positionsChanged = true;

            distance = Math.Sqrt(MonsterEcsState.DistanceSquared(
                transform.X,
                transform.Z,
                target.X,
                target.Z));
            if (distance <= MonsterEcsRules.CombatRange + 0.0001d)
            {
                MonsterEcsState.StopCombatMovement(ref movement);
                combat.Phase = MonsterCombatPhase.Attacking;
                combat.NextAttackAt =
                    stepAt + MonsterEcsRules.TickInterval;
                Publish(
                    events,
                    new MonsterRuntimeUpdate(
                        MonsterRuntimeUpdateKind.Arrived,
                        MonsterEcsState.Snapshot(world, entity),
                        MovementEndField: 1));
                break;
            }

            MonsterEcsState.SetCombatVelocity(
                ref transform,
                ref movement,
                target);
            Publish(
                events,
                new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Started,
                    MonsterEcsState.Snapshot(world, entity),
                    MovementMode: 1));
        }

        return positionsChanged;
    }

    public static bool AdvanceReturnHome(
        EcsWorld world,
        EntityId entity,
        DateTimeOffset now,
        EcsEventBuffer events)
    {
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var combat = ref world.Get<MonsterCombatComponent>(entity);
        var positionsChanged = false;

        while (combat.Phase == MonsterCombatPhase.Returning &&
               now >= movement.NextMovementStepAt)
        {
            var stepAt = movement.NextMovementStepAt;
            if (movement.RemainingMovementTicks <= 1)
            {
                positionsChanged |= MonsterEcsState.DistanceSquared(
                    transform.X,
                    transform.Z,
                    transform.HomeX,
                    transform.HomeZ) > double.Epsilon;
                MonsterEcsState.CompleteReturnHome(
                    world,
                    entity,
                    stepAt);
                Publish(
                    events,
                    new MonsterRuntimeUpdate(
                        MonsterRuntimeUpdateKind.Returned,
                        MonsterEcsState.Snapshot(world, entity),
                        MovementEndField: 1));
                Publish(
                    events,
                    MonsterEcsState.RetireReturnedMonster(
                        world,
                        entity,
                        stepAt));
                break;
            }

            transform.X += movement.VelocityX;
            transform.Z += movement.VelocityZ;
            movement.RemainingMovementTicks--;
            movement.NextMovementStepAt +=
                MonsterEcsState.ElementalMovementInterval(in movement);
            positionsChanged = true;
        }

        return positionsChanged;
    }

    public static void AddReturnStart(
        EcsWorld world,
        EntityId entity,
        DateTimeOffset now,
        EcsEventBuffer events)
    {
        var update = MonsterEcsState.BeginReturnHome(
            world,
            entity,
            now);
        Publish(events, update);
        if (update.Kind == MonsterRuntimeUpdateKind.Returned)
        {
            Publish(
                events,
                MonsterEcsState.RetireReturnedMonster(
                    world,
                    entity,
                    now));
        }
    }

    private static void Publish(
        EcsEventBuffer events,
        MonsterRuntimeUpdate update) =>
        events.Publish(new MonsterEcsUpdateEvent(update));
}
