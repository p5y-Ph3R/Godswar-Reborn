using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

internal static class MonsterEcsPatrolSimulation
{
    public static bool Advance(
        EcsWorld world,
        EntityId entity,
        DateTimeOffset now,
        EcsEventBuffer events)
    {
        ref var transform = ref world.Get<MonsterTransformComponent>(entity);
        ref var movement = ref world.Get<MonsterMovementComponent>(entity);
        ref var random = ref world.Get<MonsterRandomComponent>(entity);
        var positionsChanged = false;

        if (movement.IsMoving)
        {
            while (movement.IsMoving &&
                   now >= movement.NextMovementStepAt)
            {
                var stepAt = movement.NextMovementStepAt;
                transform.X += movement.VelocityX;
                transform.Z += movement.VelocityZ;
                movement.RemainingMovementTicks--;
                movement.NextMovementStepAt +=
                    MonsterEcsRules.TickInterval;
                positionsChanged = true;

                if (movement.RemainingMovementTicks != 0)
                {
                    continue;
                }

                transform.X = movement.TargetX;
                transform.Z = movement.TargetZ;
                movement.IsMoving = false;
                movement.NextMovementAt =
                    stepAt + MonsterEcsRandom.NextIdleDelay(ref random);
                events.Publish(
                    new MonsterEcsUpdateEvent(
                        new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Arrived,
                            MonsterEcsState.Snapshot(world, entity))));
            }

            return positionsChanged;
        }

        if (now < movement.NextMovementAt)
        {
            return false;
        }

        StartMovement(
            ref transform,
            ref movement,
            ref random,
            now);
        events.Publish(
            new MonsterEcsUpdateEvent(
                new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Started,
                    MonsterEcsState.Snapshot(world, entity))));
        return false;
    }

    private static void StartMovement(
        ref MonsterTransformComponent transform,
        ref MonsterMovementComponent movement,
        ref MonsterRandomComponent random,
        DateTimeOffset now)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var ticks = MonsterEcsRules.MinimumMovementTicks +
                (int)(MonsterEcsRandom.Next(ref random) %
                    (MonsterEcsRules.MaximumMovementTicks -
                     MonsterEcsRules.MinimumMovementTicks + 1));
            var angle = MonsterEcsRandom.NextUnit(ref random) * Math.Tau;
            var velocityX = (float)(
                Math.Sin(angle) * MonsterEcsRules.MovementStep);
            var velocityZ = (float)(
                Math.Cos(angle) * MonsterEcsRules.MovementStep);
            var targetX = transform.X + (velocityX * ticks);
            var targetZ = transform.Z + (velocityZ * ticks);
            if (MonsterEcsState.DistanceSquared(
                    transform.HomeX,
                    transform.HomeZ,
                    targetX,
                    targetZ) >
                MonsterEcsRules.MaximumRoamRadius *
                MonsterEcsRules.MaximumRoamRadius)
            {
                continue;
            }

            MonsterEcsState.SetMovement(
                ref transform,
                ref movement,
                now,
                ticks,
                velocityX,
                velocityZ,
                targetX,
                targetZ);
            return;
        }

        var towardHomeX = transform.HomeX - transform.X;
        var towardHomeZ = transform.HomeZ - transform.Z;
        var distance = Math.Sqrt(
            (towardHomeX * towardHomeX) +
            (towardHomeZ * towardHomeZ));
        var velocityXFallback = distance > double.Epsilon
            ? (float)(
                (towardHomeX / distance) *
                MonsterEcsRules.MovementStep)
            : MonsterEcsRules.MovementStep;
        var velocityZFallback = distance > double.Epsilon
            ? (float)(
                (towardHomeZ / distance) *
                MonsterEcsRules.MovementStep)
            : 0f;
        MonsterEcsState.SetMovement(
            ref transform,
            ref movement,
            now,
            MonsterEcsRules.MinimumMovementTicks,
            velocityXFallback,
            velocityZFallback,
            transform.X + velocityXFallback,
            transform.Z + velocityZFallback);
    }
}
