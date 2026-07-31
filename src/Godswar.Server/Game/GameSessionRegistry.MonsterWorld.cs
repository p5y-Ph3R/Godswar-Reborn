using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal async Task AdvanceMonsterWorldOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ticks = new List<WorldInstanceMonsterTick>();
        foreach (var runtime in WorldInstances.Snapshot())
        {
            if (runtime.Descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Closed)
            {
                continue;
            }

            var tick = InvokeWorldOwner(
                runtime,
                map => _playerRuntimeMode ==
                    PlayerRuntimeMode.Ecs
                    ? map.AdvanceMonsters(
                        now,
                        GetPlayerLifeRevision)
                    : map.AdvanceMonsters(now));
            if (!tick.PositionsChanged && tick.Updates.Count == 0)
            {
                continue;
            }

            var sessions = InvokeWorldOwner(
                runtime,
                static map => map.Snapshot());
            ticks.Add(
                new WorldInstanceMonsterTick(
                    runtime,
                    tick,
                    sessions));
        }

        await Parallel.ForEachAsync(
            ticks,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism =
                    _worldInstanceOptions
                        .MaximumFanoutConcurrency
            },
            async (worldTick, token) =>
            {
                foreach (var attack in
                         worldTick.Tick.Updates.Where(
                             update => update.Kind ==
                                 MonsterRuntimeUpdateKind
                                     .Attacked))
                {
                    try
                    {
                        await ProcessMonsterAttackAsync(
                            worldTick.Runtime,
                            attack,
                            token);
                    }
                    catch (
                        MonsterAttackTargetUnavailableException
                            ex)
                    {
                        InvokeWorldOwner(
                            worldTick.Runtime,
                            map =>
                                map.ClearMonsterAggroForCharacter(
                                    ex.TargetCharacterId,
                                    now));
                        Console.WriteLine(
                            $"[monster] skipped stale target " +
                            $"character={ex.TargetCharacterId} " +
                            $"instance=" +
                            $"{worldTick.Runtime.InstanceId} " +
                            $"map={worldTick.Runtime.MapId}");
                    }
                }
            });

        var deliveries = RoundRobinMonsterDeliveries(ticks);
        await Parallel.ForEachAsync(
            deliveries,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism =
                    _worldInstanceOptions
                        .MaximumFanoutConcurrency
            },
            async (delivery, token) =>
            {
                try
                {
                    await SendMonsterRuntimeTickAsync(
                        delivery.Runtime,
                        delivery.Context,
                        delivery.Tick,
                        token);
                }
                catch (Exception ex)
                    when (ex is IOException or
                        ObjectDisposedException)
                {
                    Remove(delivery.Context.Session);
                }
            });
    }

    private static IReadOnlyList<MonsterTickDelivery>
        RoundRobinMonsterDeliveries(
            IReadOnlyList<WorldInstanceMonsterTick> ticks)
    {
        var queues = ticks
            .Select(tick => new Queue<GameSessionContext>(
                tick.Sessions.Where(
                    static context =>
                        context.WorldReady)))
            .ToArray();
        var result = new List<MonsterTickDelivery>(
            queues.Sum(static queue => queue.Count));
        while (queues.Any(static queue => queue.Count > 0))
        {
            for (var index = 0;
                 index < queues.Length;
                 index++)
            {
                if (queues[index].TryDequeue(
                        out var context))
                {
                    result.Add(new MonsterTickDelivery(
                        ticks[index].Runtime,
                        context,
                        ticks[index].Tick));
                }
            }
        }

        return result;
    }

    private async Task SendMonsterRuntimeTickAsync(
        WorldInstanceRuntime runtime,
        GameSessionContext context,
        MonsterRuntimeTick tick,
        CancellationToken cancellationToken)
    {
        var map = runtime.Map;
        await using var transition = await map.BeginMonsterVisibilityTransitionAsync(
            context.Session,
            context.Character.PositionX,
            context.Character.PositionZ,
            cancellationToken);
        if (transition is null)
        {
            return;
        }

        var delta = transition.Delta;
        var despawnedObjectIds = tick.Updates
            .Where(update => update.Kind == MonsterRuntimeUpdateKind.Despawned)
            .Select(update => update.Monster.ObjectId)
            .ToHashSet();
        var returnedByObjectId = tick.Updates
            .Where(update => update.Kind == MonsterRuntimeUpdateKind.Returned)
            .GroupBy(update => update.Monster.ObjectId)
            .ToDictionary(group => group.Key, group => group.Last());
        var returnedInsideViewerAoi = new HashSet<uint>();
        var returnedOutsideViewerAoi = new HashSet<uint>();
        foreach (var objectId in delta.Leaving.Where(despawnedObjectIds.Contains))
        {
            if (!returnedByObjectId.TryGetValue(objectId, out var returned))
            {
                continue;
            }

            if (!WorldSectorVisibilityTracker<CapturedMonsterSpawn>.TryGetCell(
                    returned.Monster.X,
                    returned.Monster.Z,
                    out var returnedCell) ||
                !WorldSectorVisibilityTracker<CapturedMonsterSpawn>.IsNeighbor(
                    delta.PlayerCell,
                    returnedCell))
            {
                returnedOutsideViewerAoi.Add(objectId);
                continue;
            }

            // The runtime has already retired this object, so the final-state
            // visibility delta alone would send its marker first and suppress
            // movement-end. Serialize the immutable home-arrival snapshot
            // before removing the old client entity.
            await context.Session.SendAsync(
                PacketBuilder.MonsterMovementEnd(
                    objectId,
                    returned.MovementEndField ?? returned.Monster.MovementTicks,
                    returned.Monster.X,
                    returned.Monster.Y,
                    returned.Monster.Z,
                    returned.Monster.Facing),
                cancellationToken,
                "MonsterLeashReturnEnd");
            await context.Session.SendAsync(
                PacketBuilder.MonsterLifecycleMarker(objectId),
                cancellationToken,
                "MonsterLeashRetire");
            returnedInsideViewerAoi.Add(objectId);
        }

        var ordinaryLeaving = delta.Leaving
            .Where(objectId =>
                !despawnedObjectIds.Contains(objectId) ||
                returnedOutsideViewerAoi.Contains(objectId))
            .ToArray();
        if (ordinaryLeaving.Length > 0)
        {
            await context.Session.SendAsync(
                PacketBuilder.RemoveWorldObjects(ordinaryLeaving),
                cancellationToken,
                "RoamingMonsterAoiRemovals");
        }

        foreach (var objectId in delta.Leaving.Where(objectId =>
                     despawnedObjectIds.Contains(objectId) &&
                     !returnedInsideViewerAoi.Contains(objectId) &&
                     !returnedOutsideViewerAoi.Contains(objectId)))
        {
            await context.Session.SendAsync(
                PacketBuilder.MonsterLifecycleMarker(objectId),
                cancellationToken,
                "MonsterCorpseDespawn");
        }

        var enteringObjectIds = delta.Entering
            .Select(monster => monster.ObjectId)
            .ToHashSet();
        var respawnedObjectIds = tick.Updates
            .Where(update => update.Kind == MonsterRuntimeUpdateKind.Respawned)
            .Select(update => update.Monster.ObjectId)
            .ToHashSet();
        foreach (var objectId in enteringObjectIds.Where(respawnedObjectIds.Contains))
        {
            await context.Session.SendAsync(
                PacketBuilder.MonsterLifecycleMarker(objectId),
                cancellationToken,
                "MonsterRespawnMarker");
        }

        if (delta.Entering.Count > 0)
        {
            await context.Session.SendAsync(
                PacketBuilder.CapturedMonsterSpawns(
                    delta.Entering.Select(monster => monster.Appearance).ToArray()),
                cancellationToken,
                "RoamingMonsterAoiSpawns",
                framed: false);
        }

        // A monster can cross into a stationary viewer's AOI midway through a
        // leg. Start a continuation after its appearance so the new viewer does
        // not see a frozen monster followed by an arrival snap.
        foreach (var monster in delta.Entering.Where(monster => monster.IsMoving))
        {
            await context.Session.SendAsync(
                PacketBuilder.MonsterMovementStart(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    monster.VelocityX,
                    monster.VelocityY,
                    monster.VelocityZ),
                cancellationToken,
                "RoamingMonsterContinuation");
        }

        foreach (var update in tick.Updates)
        {
            var monster = update.Monster;
            if (enteringObjectIds.Contains(monster.ObjectId) ||
                !transition.IsDesiredVisible(monster.ObjectId))
            {
                continue;
            }

            var currentMonster = update.Kind is (
                    MonsterRuntimeUpdateKind.Started or
                    MonsterRuntimeUpdateKind.Arrived or
                    MonsterRuntimeUpdateKind.Returned)
                ? InvokeWorldOwner(
                    runtime,
                    ownedMap =>
                        ownedMap.TryGetMonsterSnapshot(
                            monster.ObjectId,
                            out var snapshot)
                            ? snapshot
                            : null)
                : null;
            if (update.Kind is (MonsterRuntimeUpdateKind.Started or
                    MonsterRuntimeUpdateKind.Arrived or
                    MonsterRuntimeUpdateKind.Returned) &&
                (currentMonster is null ||
                 !currentMonster.IsAlive ||
                 !currentMonster.IsSpawned ||
                 currentMonster.SpawnGeneration != monster.SpawnGeneration ||
                 (update.Kind == MonsterRuntimeUpdateKind.Started && !currentMonster.IsMoving) ||
                 (update.Kind == MonsterRuntimeUpdateKind.Returned &&
                  (currentMonster.IsMoving ||
                   currentMonster.CombatPhase != MonsterCombatPhase.AwaitingRetirement))))
            {
                // Combat can atomically kill a monster after this world tick was
                // calculated but before a slower viewer send. Never resurrect a
                // cancelled leg with a stale movement packet.
                continue;
            }

            var packet = update.Kind switch
            {
                MonsterRuntimeUpdateKind.Started => PacketBuilder.MonsterMovementStart(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    monster.VelocityX,
                    monster.VelocityY,
                    monster.VelocityZ,
                    update.MovementMode),
                MonsterRuntimeUpdateKind.Arrived or MonsterRuntimeUpdateKind.Returned =>
                    PacketBuilder.MonsterMovementEnd(
                        monster.ObjectId,
                        update.MovementEndField ?? monster.MovementTicks,
                        monster.X,
                        monster.Y,
                        monster.Z,
                        monster.Facing),
                _ => []
            };
            if (packet.Length > 0)
            {
                await context.Session.SendAsync(
                    packet,
                    cancellationToken,
                    $"RoamingMonster{update.Kind}");
            }
        }

        // Commit only after the complete remove/spawn/movement handoff succeeds.
        transition.Commit();
    }

    private sealed record WorldInstanceMonsterTick(
        WorldInstanceRuntime Runtime,
        MonsterRuntimeTick Tick,
        IReadOnlyList<GameSessionContext> Sessions);

    private sealed record MonsterTickDelivery(
        WorldInstanceRuntime Runtime,
        GameSessionContext Context,
        MonsterRuntimeTick Tick);
}
