using Godswar.Server.Ecs;
using Godswar.Server.Game;
using Godswar.Server.World.Components.Monsters;

namespace Godswar.Server.World.Systems.Monsters;

/// <summary>
/// Executes one deterministic monster frame over typed component pools. The
/// ordering intentionally matches the legacy runtime during shadow rollout.
/// </summary>
internal sealed class MonsterEcsSimulationSystem(
    MonsterEcsSimulationFrame frame) : IEcsSystem
{
    public int Order => 100;

    public void Update(EcsSystemContext context)
    {
        var deathsAnnounced = new HashSet<uint>();
        var returnStartsAnnounced = new HashSet<uint>();
        PublishPending(
            context.Events,
            deathsAnnounced,
            returnStartsAnnounced);

        foreach (var entity in context.World.Query<MonsterIdentityComponent>())
        {
            ref var identity =
                ref context.World.Get<MonsterIdentityComponent>(entity);
            ref var vitals =
                ref context.World.Get<MonsterVitalsComponent>(entity);
            ref var lifecycle =
                ref context.World.Get<MonsterLifecycleComponent>(entity);

            if (!vitals.IsAlive)
            {
                AdvanceLifecycle(
                    context,
                    entity,
                    identity.Definition.ObjectId,
                    ref vitals,
                    ref lifecycle,
                    deathsAnnounced);
                continue;
            }

            ref var movement =
                ref context.World.Get<MonsterMovementComponent>(entity);
            ref var combat =
                ref context.World.Get<MonsterCombatComponent>(entity);
            ref var transform =
                ref context.World.Get<MonsterTransformComponent>(entity);
            if (combat.StunnedUntil is { } stunnedUntil)
            {
                if (frame.Now < stunnedUntil)
                {
                    continue;
                }

                combat.StunnedUntil = null;
                combat.NextAttackAt =
                    frame.Now + MonsterEcsRules.TickInterval;
                movement.NextMovementStepAt =
                    frame.Now + MonsterEcsState.ElementalMovementInterval(
                        in movement);
            }

            if (combat.AggroCharacterId is { } aggroCharacterId)
            {
                if (frame.Targets.TryGetValue(
                        aggroCharacterId,
                        out var target) &&
                    target.IsAlive &&
                    IsTargetInsideCombatBoundary(
                        context.World,
                        entity,
                        target))
                {
                    frame.PositionsChanged |=
                        MonsterEcsCombatSimulation.Advance(
                            context.World,
                            entity,
                            target,
                            frame.Now,
                            context.Events);
                    continue;
                }

                MonsterEcsCombatSimulation.AddReturnStart(
                    context.World,
                    entity,
                    frame.Now,
                    context.Events);
                continue;
            }

            if (combat.Phase == MonsterCombatPhase.Returning)
            {
                if (!returnStartsAnnounced.Contains(
                        identity.Definition.ObjectId))
                {
                    frame.PositionsChanged |=
                        MonsterEcsCombatSimulation.AdvanceReturnHome(
                            context.World,
                            entity,
                            frame.Now,
                            context.Events);
                }

                continue;
            }

            if (combat.Phase ==
                MonsterCombatPhase.AwaitingRetirement)
            {
                if (MonsterEcsState.ShouldRetireReturnedMonster(
                        context.World,
                        entity))
                {
                    Publish(
                        context.Events,
                        MonsterEcsState.RetireReturnedMonster(
                            context.World,
                            entity,
                            frame.Now));
                }
                else
                {
                    MonsterEcsState.SettleReturnedMonster(
                        context.World,
                        entity);
                }

                continue;
            }

            if (MonsterAggroPolicy.IsAggressive(
                    identity.Definition.Tier) &&
                MonsterAggroPolicy.TrySelectNearestAggressiveTarget(
                    frame.Targets,
                    transform.X,
                    transform.Z,
                    out var nearbyTarget))
            {
                var stoppedPatrol = MonsterEcsState.SetAggroTarget(
                    ref movement,
                    ref combat,
                    nearbyTarget.CharacterId,
                    frame.Now);
                if (stoppedPatrol)
                {
                    Publish(
                        context.Events,
                        new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Arrived,
                            MonsterEcsState.Snapshot(
                                context.World,
                                entity),
                            MovementEndField: 1));
                }
                frame.PositionsChanged |=
                    MonsterEcsCombatSimulation.Advance(
                        context.World,
                        entity,
                        nearbyTarget,
                        frame.Now,
                        context.Events);
                continue;
            }

            frame.PositionsChanged |= MonsterEcsPatrolSimulation.Advance(
                context.World,
                entity,
                frame.Now,
                context.Events);
        }
    }

    private void PublishPending(
        EcsEventBuffer events,
        HashSet<uint> deathsAnnounced,
        HashSet<uint> returnStartsAnnounced)
    {
        foreach (var update in frame.PendingUpdates)
        {
            Publish(events, update);
            if (update.Kind == MonsterRuntimeUpdateKind.Died)
            {
                deathsAnnounced.Add(update.Monster.ObjectId);
            }
            else if (update.Kind == MonsterRuntimeUpdateKind.Started &&
                     update.Monster.CombatPhase ==
                     MonsterCombatPhase.Returning)
            {
                returnStartsAnnounced.Add(update.Monster.ObjectId);
            }
        }
    }

    private void AdvanceLifecycle(
        EcsSystemContext context,
        EntityId entity,
        uint objectId,
        ref MonsterVitalsComponent vitals,
        ref MonsterLifecycleComponent lifecycle,
        HashSet<uint> deathsAnnounced)
    {
        if (lifecycle.RespawnPolicy == MonsterRespawnPolicy.Never &&
            lifecycle.RespawnAt is not null)
        {
            throw new InvalidOperationException(
                "Never-respawn monster contains a scheduled respawn.");
        }

        if (lifecycle.RespawnPolicy is not (
                MonsterRespawnPolicy.Timed or
                MonsterRespawnPolicy.Never))
        {
            throw new InvalidOperationException(
                "Monster lifecycle contains an unsupported respawn policy.");
        }

        if (deathsAnnounced.Contains(objectId))
        {
            return;
        }

        if (vitals.IsSpawned &&
            lifecycle.DespawnAt is { } despawnAt &&
            frame.Now >= despawnAt)
        {
            vitals.IsSpawned = false;
            Publish(
                context.Events,
                new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Despawned,
                    MonsterEcsState.Snapshot(context.World, entity)));
            return;
        }

        if (!vitals.IsSpawned &&
            lifecycle.RespawnAt is { } respawnAt &&
            frame.Now >= respawnAt)
        {
            Respawn(context.World, entity);
            frame.PositionsChanged = true;
            Publish(
                context.Events,
                new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Respawned,
                    MonsterEcsState.Snapshot(context.World, entity)));
        }
    }

    private void Respawn(EcsWorld world, EntityId entity)
    {
        ref var transform =
            ref world.Get<MonsterTransformComponent>(entity);
        ref var vitals =
            ref world.Get<MonsterVitalsComponent>(entity);
        ref var movement =
            ref world.Get<MonsterMovementComponent>(entity);
        ref var combat =
            ref world.Get<MonsterCombatComponent>(entity);
        ref var lifecycle =
            ref world.Get<MonsterLifecycleComponent>(entity);
        ref var random =
            ref world.Get<MonsterRandomComponent>(entity);

        if (lifecycle.RespawnPolicy != MonsterRespawnPolicy.Timed)
        {
            throw new InvalidOperationException(
                "Only timed monster lifecycles can respawn.");
        }

        transform.X = transform.HomeX;
        transform.Z = transform.HomeZ;
        transform.Facing = transform.HomeFacing;
        vitals.CurrentHealth = vitals.MaximumHealth;
        vitals.IsAlive = true;
        vitals.IsSpawned = true;
        vitals.SpawnGeneration =
            checked(vitals.SpawnGeneration + 1);
        vitals.HealthRevision = 0;
        movement = new MonsterMovementComponent
        {
            NextMovementAt = frame.Now +
                MonsterEcsRandom.NextIdleDelay(ref random),
            MovementSpeedBasisPoints = 10_000
        };
        combat = new MonsterCombatComponent();
        lifecycle.DespawnAt = null;
        lifecycle.RespawnAt = null;
    }

    private static bool IsTargetInsideCombatBoundary(
        EcsWorld world,
        EntityId entity,
        MonsterCombatTarget target)
    {
        ref var transform =
            ref world.Get<MonsterTransformComponent>(entity);
        ref var identity =
            ref world.Get<MonsterIdentityComponent>(entity);
        var radius =
            MonsterEcsRules.CombatLeashRadius +
            identity.AttackRange;
        return MonsterEcsState.DistanceSquared(
            transform.HomeX,
            transform.HomeZ,
            target.X,
            target.Z) <= radius * radius;
    }

    private static void Publish(
        EcsEventBuffer events,
        MonsterRuntimeUpdate update) =>
        events.Publish(new MonsterEcsUpdateEvent(update));
}
