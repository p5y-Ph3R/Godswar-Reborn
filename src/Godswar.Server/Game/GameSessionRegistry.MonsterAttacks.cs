using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task ProcessMonsterAttackAsync(
        MapInstance map,
        MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken)
    {
        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await ProcessMonsterAttackEcsAsync(
                map,
                attack,
                cancellationToken);
            return;
        }

        await ProcessMonsterAttackLegacyAsync(
            map,
            attack,
            cancellationToken);
    }

    private async Task ProcessMonsterAttackLegacyAsync(
        MapInstance map,
        MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken)
    {
        if (attack.TargetCharacterId is not { } targetCharacterId)
        {
            return;
        }

        GameSessionContext? targetContext;
        var statusContext = map.Snapshot().FirstOrDefault(context =>
            context.WorldReady && context.CharacterId == targetCharacterId);
        var damageResolvedAt = DateTimeOffset.UtcNow;
        // Runtime statuses have their own gate. Snapshot the mitigation before
        // taking the registry gate so status publication and a monster attack
        // cannot acquire those locks in opposite order.
        var physicalDamageReduction = statusContext is null
            ? 0m
            : GetRuntimePhysicalDamageReduction(statusContext.Session, damageResolvedAt);
        uint damage;
        var killed = false;
        long? deathLifeRevision = null;
        var deathInterruptionTask = Task.CompletedTask;
        lock (_gate)
        {
            targetContext = map.Snapshot().FirstOrDefault(context =>
                context.WorldReady && context.CharacterId == targetCharacterId);
            if (targetContext is null)
            {
                damage = 0;
            }
            else
            {
                if (statusContext is null ||
                    !ReferenceEquals(statusContext.Session, targetContext.Session))
                {
                    physicalDamageReduction = 0m;
                }

                lock (targetContext.Character.VitalsSync)
                {
                    if (targetContext.Character.CurrentHp <= 0)
                    {
                        targetContext = null;
                        damage = 0;
                    }
                    else
                    {
                        damage = MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                            attack.Monster.Definition.Tier,
                            targetContext.Character,
                            physicalDamageReduction);
                        var beforeHealth = targetContext.Character.CurrentHp;
                        killed = damage >= (uint)beforeHealth;
                        if (killed)
                        {
                            // Claim before the lethal vitals commit. The
                            // handler does no asynchronous work before this
                            // claim, so a deadline completion and death have
                            // one authoritative order.
                            deathInterruptionTask =
                                RequestSkillCastInterruptionAsync(
                                    targetContext.Session,
                                    SkillCastInterruptionReason.Death,
                                    cancellationToken);
                        }

                        targetContext.Character.CurrentHp = damage >= (uint)beforeHealth
                            ? 0
                            : beforeHealth - (int)damage;
                        targetContext.Character.MarkVitalsChanged();
                        if (killed)
                        {
                            deathLifeRevision = _playerLifeRevisions.AddOrUpdate(
                                targetContext.Session,
                                1,
                                static (_, revision) => revision + 1);
                        }
                    }
                }
            }
        }

        if (targetContext is null || damage == 0)
        {
            map.ClearMonsterAggroForCharacter(targetCharacterId, DateTimeOffset.UtcNow);
            return;
        }

        if (killed)
        {
            await deathInterruptionTask;
        }

        var monster = attack.Monster;
        var target = targetContext.Character;
        var worldTargetObjectId = WorldObjectIds.ForPlayer(target.Id);
        try
        {
            await targetContext.Session.SendAsync(
                PacketBuilder.SkillCastImpact(
                    monster.ObjectId,
                    LocalPlayerObjectId,
                    2000,
                    attack.TargetX,
                    attack.TargetZ),
                cancellationToken,
                "MonsterAttackImpactSelf");
            await targetContext.Session.SendAsync(
                PacketBuilder.PhysicalDamage(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    LocalPlayerObjectId,
                    damage,
                    result: 0),
                cancellationToken,
                "MonsterAttackDamageSelf");
            if (killed)
            {
                await targetContext.Session.SendAsync(
                    PacketBuilder.PlayerDeath(
                        LocalPlayerObjectId,
                        target.PositionX,
                        0f,
                        target.PositionZ,
                        target.CurrentMap),
                    cancellationToken,
                    "MonsterKillPlayerSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Remove(targetContext.Session);
        }

        foreach (var observer in map.Snapshot())
        {
            if (!observer.WorldReady ||
                ReferenceEquals(observer.Session, targetContext.Session) ||
                !map.IsMonsterVisibleTo(observer.Session, monster.ObjectId))
            {
                continue;
            }

            try
            {
                await observer.Session.SendAsync(
                    PacketBuilder.SkillCastImpact(
                        monster.ObjectId,
                        worldTargetObjectId,
                        2000,
                        attack.TargetX,
                        attack.TargetZ),
                    cancellationToken,
                    "MonsterAttackImpactWorld");
                await observer.Session.SendAsync(
                    PacketBuilder.PhysicalDamage(
                        monster.ObjectId,
                        monster.X,
                        monster.Y,
                        monster.Z,
                        worldTargetObjectId,
                        damage,
                        result: 0),
                    cancellationToken,
                    "MonsterAttackDamageWorld");
                if (killed)
                {
                    await observer.Session.SendAsync(
                        PacketBuilder.PlayerDeath(
                            worldTargetObjectId,
                            target.PositionX,
                            0f,
                            target.PositionZ,
                            target.CurrentMap),
                        cancellationToken,
                        "MonsterKillPlayerWorld");
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(observer.Session);
            }
        }

        if (killed && deathLifeRevision is { } expectedLifeRevision)
        {
            try
            {
                await RemovePersistentRuntimeStatusForLifeRevisionAndPublishAsync(
                    targetContext.Session,
                    expectedLifeRevision,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow,
                    "mount-death",
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[mount] failed clearing Ride after death character={targetContext.DisplayName}: {ex.Message}");
            }
        }

        if (killed)
        {
            map.ClearMonsterAggroForCharacter(targetCharacterId, DateTimeOffset.UtcNow);
        }

        if (_store is not null)
        {
            try
            {
                int currentHp;
                int currentMp;
                long vitalsRevision;
                lock (target.VitalsSync)
                {
                    currentHp = target.CurrentHp;
                    currentMp = target.CurrentMp;
                    vitalsRevision = target.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    targetContext.AccountId,
                    targetContext.CharacterId,
                    currentHp,
                    currentMp,
                    vitalsRevision,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[monster] victim vitals persistence deferred character={targetContext.DisplayName}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[monster] attack monster={monster.ObjectId} tier={monster.Definition.Tier} target={targetContext.DisplayName} damage={damage} hp={target.CurrentHp}/{target.MaxHp} killed={killed}");
    }

}
