using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<int> BroadcastToMonsterViewersAsync(
        byte mapId,
        uint monsterId,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string? label = null,
        bool framed = true,
        MonsterHealthMutation? healthMutation = null,
        uint? expectedSpawnGeneration = null)
    {
        if (!TryResolveWorldInstance(
                mapId,
                excludeSession,
                out var runtime))
        {
            return 0;
        }

        if (healthMutation is { } mutation && mutation.ObjectId != monsterId)
        {
            throw new ArgumentException(
                $"Health mutation object {mutation.ObjectId} does not match broadcast monster {monsterId}.",
                nameof(healthMutation));
        }

        if (healthMutation is { } versionedHealthMutation &&
            expectedSpawnGeneration is { } expectedGeneration &&
            versionedHealthMutation.SpawnGeneration != expectedGeneration)
        {
            throw new ArgumentException(
                "Health mutation and ordinary delivery generation do not match.",
                nameof(expectedSpawnGeneration));
        }

        var sent = 0;
        var recipients = InvokeWorldOwner(
            runtime,
            static map => map.Snapshot(),
            cancellationToken);
        foreach (var context in recipients)
        {
            if (!context.WorldReady ||
                excludeSession is not null && ReferenceEquals(context.Session, excludeSession))
            {
                continue;
            }

            try
            {
                await using var deliveryLease =
                    healthMutation is { } versionedMutation
                        ? await runtime.Map
                            .AcquireMonsterViewerHealthDeliveryLeaseAsync(
                            context.Session,
                            [versionedMutation],
                            cancellationToken)
                        : expectedSpawnGeneration is { } versionedGeneration
                            ? await runtime.Map
                                .AcquireMonsterViewerDeliveryLeaseAsync(
                                context.Session,
                                monsterId,
                                versionedGeneration,
                                cancellationToken)
                            : await runtime.Map
                                .AcquireMonsterViewerDeliveryLeaseAsync(
                                context.Session,
                                monsterId,
                                cancellationToken);
                if (deliveryLease is null)
                {
                    continue;
                }

                if (deliveryLease.ReconciliationObjectIds.Count > 0)
                {
                    await SendMonsterHealthReconciliationAsync(
                        context.Session,
                        deliveryLease,
                        cancellationToken,
                        label);
                }
                else
                {
                    await context.Session.SendAsync(packet, cancellationToken, label, framed);
                }

                deliveryLease.Commit();
                sent++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }
        }

        return sent;
    }

    private static async Task SendMonsterHealthReconciliationAsync(
        ClientSession session,
        MonsterViewerDeliveryLease deliveryLease,
        CancellationToken cancellationToken,
        string? label)
    {
        await session.SendAsync(
            PacketBuilder.RemoveWorldObjects(
                deliveryLease.ReconciliationObjectIds.ToArray()),
            cancellationToken,
            $"{label ?? "MonsterHealth"}ReconcileRemove");
        if (deliveryLease.ReconciliationMonsters.Count == 0)
        {
            return;
        }

        await session.SendAsync(
            PacketBuilder.CapturedMonsterSpawns(
                deliveryLease.ReconciliationMonsters
                    .Select(monster => monster.Appearance)
                    .ToArray()),
            cancellationToken,
            $"{label ?? "MonsterHealth"}ReconcileSpawn",
            framed: false);
        foreach (var monster in deliveryLease.ReconciliationMonsters.Where(
                     monster => monster.IsMoving))
        {
            await session.SendAsync(
                PacketBuilder.MonsterMovementStart(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    monster.VelocityX,
                    monster.VelocityY,
                    monster.VelocityZ),
                cancellationToken,
                $"{label ?? "MonsterHealth"}ReconcileMovement");
        }
    }

    public async Task<bool> DeliverMonsterPacketToViewerAsync(
        ClientSession session,
        byte mapId,
        uint monsterId,
        ReadOnlyMemory<byte> packet,
        uint expectedSpawnGeneration,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        if (!_sessions.TryGetValue(session, out var context) ||
            context.MapId != mapId ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        await using var deliveryLease =
            await runtime.Map.AcquireMonsterViewerDeliveryLeaseAsync(
                session,
                monsterId,
                expectedSpawnGeneration,
                cancellationToken);
        if (deliveryLease is null)
        {
            return false;
        }

        await session.SendAsync(packet, cancellationToken, label, framed);
        deliveryLease.Commit();
        return true;
    }

    public async Task<bool> DeliverMonsterHealthPacketToViewerAsync(
        ClientSession session,
        byte mapId,
        uint monsterId,
        ReadOnlyMemory<byte> packet,
        MonsterHealthMutation healthMutation,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        if (healthMutation.ObjectId != monsterId)
        {
            throw new ArgumentException(
                $"Health mutation object {healthMutation.ObjectId} does not match delivery monster {monsterId}.",
                nameof(healthMutation));
        }

        if (!_sessions.TryGetValue(session, out var context) ||
            context.MapId != mapId ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        await using var deliveryLease =
            await runtime.Map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
                session,
                [healthMutation],
                cancellationToken);
        if (deliveryLease is null)
        {
            return false;
        }

        if (deliveryLease.ReconciliationObjectIds.Count > 0)
        {
            await SendMonsterHealthReconciliationAsync(
                session,
                deliveryLease,
                cancellationToken,
                label);
        }
        else
        {
            await session.SendAsync(packet, cancellationToken, label, framed);
        }

        deliveryLease.Commit();
        return true;
    }

    public async Task<bool> DeliverMonsterAreaDamageToViewerAsync(
        ClientSession session,
        byte mapId,
        uint attackerObjectId,
        uint skillId,
        IReadOnlyList<MonsterAreaDamageBroadcastHit> hits,
        CancellationToken cancellationToken,
        string labelPrefix = "AreaSkillSelf")
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0 ||
            !_sessions.TryGetValue(session, out var context) ||
            context.MapId != mapId ||
            !context.WorldReady ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return false;
        }

        var mutations = hits.Select(hit => hit.HealthMutation).ToArray();
        var hitsByObjectId = hits.ToDictionary(hit => hit.HealthMutation.ObjectId);
        await using var deliveryLease =
            await runtime.Map.AcquireMonsterViewerHealthDeliveryLeaseAsync(
                session,
                mutations,
                cancellationToken);
        if (deliveryLease is null)
        {
            return false;
        }

        if (deliveryLease.ReconciliationObjectIds.Count > 0)
        {
            await SendMonsterHealthReconciliationAsync(
                session,
                deliveryLease,
                cancellationToken,
                labelPrefix);
        }

        if (deliveryLease.DirectHealthMutations.Count > 0)
        {
            var directHits = deliveryLease.DirectHealthMutations
                .Select(mutation => hitsByObjectId[mutation.ObjectId])
                .Select(hit => new SkillClusterDamageEntry(
                    hit.HealthMutation.ObjectId,
                    hit.ReportedDamage))
                .ToArray();
            await session.SendAsync(
                PacketBuilder.SkillClusterDamage(
                    attackerObjectId,
                    skillId,
                    directHits),
                cancellationToken,
                $"{labelPrefix}Damage");
        }

        deliveryLease.Commit();
        return true;
    }

    public async Task<int> BroadcastMonsterAreaDamageToViewersAsync(
        byte mapId,
        ReadOnlyMemory<byte> visualPacket,
        ReadOnlyMemory<byte> impactPacket,
        uint attackerObjectId,
        uint skillId,
        IReadOnlyList<MonsterAreaDamageBroadcastHit> hits,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string labelPrefix = "AreaSkill",
        bool publishCastVisual = true)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            var visualRecipients = publishCastVisual
                ? await BroadcastToMapAsync(
                    mapId,
                    visualPacket,
                    cancellationToken,
                    excludeSession,
                    $"{labelPrefix}CastWorld")
                : 0;
            var impactRecipients = await BroadcastToMapAsync(
                mapId,
                impactPacket,
                cancellationToken,
                excludeSession,
                $"{labelPrefix}ImpactWorld");
            return Math.Max(visualRecipients, impactRecipients);
        }

        if (!TryResolveWorldInstance(
                mapId,
                excludeSession,
                out var runtime))
        {
            return 0;
        }

        var mutations = hits.Select(hit => hit.HealthMutation).ToArray();
        var hitsByObjectId = hits.ToDictionary(
            hit => hit.HealthMutation.ObjectId);
        var sent = 0;
        var recipients = InvokeWorldOwner(
            runtime,
            static map => map.Snapshot(),
            cancellationToken);
        foreach (var context in recipients)
        {
            if (!context.WorldReady ||
                excludeSession is not null && ReferenceEquals(context.Session, excludeSession))
            {
                continue;
            }

            try
            {
                await using var deliveryLease =
                    await runtime.Map
                        .AcquireMonsterViewerHealthDeliveryLeaseAsync(
                        context.Session,
                        mutations,
                        cancellationToken);
                if (deliveryLease is null)
                {
                    if (!publishCastVisual)
                    {
                        await context.Session.SendAsync(
                            impactPacket,
                            cancellationToken,
                            $"{labelPrefix}ImpactWorld");
                        sent++;
                    }
                    continue;
                }

                if (deliveryLease.ReconciliationObjectIds.Count > 0)
                {
                    await SendMonsterHealthReconciliationAsync(
                        context.Session,
                        deliveryLease,
                        cancellationToken,
                        labelPrefix);
                }

                var impactPublished = false;
                if (deliveryLease.DirectHealthMutations.Count > 0)
                {
                    var directHits = deliveryLease.DirectHealthMutations
                        .Select(mutation => hitsByObjectId[mutation.ObjectId])
                        .Select(hit => new SkillClusterDamageEntry(
                            hit.HealthMutation.ObjectId,
                            hit.ReportedDamage))
                        .ToArray();
                    if (publishCastVisual)
                    {
                        await context.Session.SendAsync(
                            visualPacket,
                            cancellationToken,
                            $"{labelPrefix}CastWorld");
                    }
                    await context.Session.SendAsync(
                        impactPacket,
                        cancellationToken,
                        $"{labelPrefix}ImpactWorld");
                    impactPublished = true;
                    await context.Session.SendAsync(
                        PacketBuilder.SkillClusterDamage(
                            attackerObjectId,
                            skillId,
                            directHits),
                        cancellationToken,
                        $"{labelPrefix}DamageWorld");
                }

                if (!publishCastVisual && !impactPublished)
                {
                    await context.Session.SendAsync(
                        impactPacket,
                        cancellationToken,
                        $"{labelPrefix}ImpactWorld");
                }

                deliveryLease.Commit();
                sent++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }
        }

        return sent;
    }

}
