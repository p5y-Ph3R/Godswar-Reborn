using System.Runtime.CompilerServices;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConditionalWeakTable<
        MonsterRuntimeUpdate,
        MonsterAttackEventIdentity> _monsterAttackEventIdentities =
        new();
    private long _nextMonsterAttackEventId;

    private async Task ProcessMonsterAttackEcsAsync(
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
            context.WorldReady &&
            context.CharacterId == targetCharacterId);
        var damageResolvedAt = DateTimeOffset.UtcNow;
        // Match the legacy lock order: runtime status first, then registry,
        // then character vitals inside the lifecycle-owned ECS adapter.
        var physicalDamageReduction = 0m;
        if (statusContext is not null &&
            !TryGetRuntimePhysicalDamageReduction(
                statusContext.Session,
                damageResolvedAt,
                out physicalDamageReduction))
        {
            throw new MonsterAttackTargetUnavailableException(
                targetCharacterId);
        }
        PlayerMonsterDamageEcsDecision decision = default;
        uint damage = 0;
        var deathInterruptionTask = Task.CompletedTask;
        lock (_gate)
        {
            targetContext = map.Snapshot().FirstOrDefault(context =>
                context.WorldReady &&
                context.CharacterId == targetCharacterId);
            if (targetContext is not null)
            {
                if (statusContext is null ||
                    !ReferenceEquals(
                        statusContext.Session,
                        targetContext.Session))
                {
                    physicalDamageReduction = 0m;
                }

                lock (targetContext.Character.VitalsSync)
                {
                    damage =
                        MonsterCombatResolver
                            .CalculateMonsterPhysicalAttack(
                                attack.Monster.Definition.Tier,
                                targetContext.Character,
                                physicalDamageReduction);
                    var currentLifeRevision =
                        _playerLifeRevisions.GetOrAdd(
                            targetContext.Session,
                            0);
                    decision = ResolvePlayerVitalsDamageEcs(
                        targetContext.Session,
                        targetContext.Character,
                        targetContext.ObjectId,
                        new PlayerMonsterDamageEcsRequest(
                            ResolveMonsterAttackEventId(attack),
                            attack.Monster.ObjectId,
                            attack.Monster.SpawnGeneration,
                            targetCharacterId,
                            attack.TargetObjectId ??
                                targetContext.ObjectId,
                            attack.TargetLifeRevision ??
                                currentLifeRevision,
                             attack.TargetVitalsRevision ??
                                 targetContext.Character
                                     .VitalsRevision,
                             damage),
                        beforeLethalCommit: () =>
                        {
                            // The ECS decision is accepted and lethal, but
                            // vitals have not been committed yet. Claim the
                            // cast interruption synchronously at that
                            // authoritative boundary.
                            deathInterruptionTask =
                                RequestSkillCastInterruptionAsync(
                                    targetContext.Session,
                                    SkillCastInterruptionReason.Death,
                                    cancellationToken);
                        });
                }
            }
        }

        if (targetContext is null)
        {
            map.ClearMonsterAggroForCharacter(
                targetCharacterId,
                DateTimeOffset.UtcNow);
            return;
        }

        if (!decision.Applied)
        {
            if (decision.RejectionReason is not (
                    MonsterPlayerDamageRejectionReason
                        .DuplicateAttackEvent or
                    MonsterPlayerDamageRejectionReason
                        .StaleAttackEvent))
            {
                map.ClearMonsterAggroForCharacter(
                    targetCharacterId,
                    DateTimeOffset.UtcNow);
            }

            Console.WriteLine(
                $"[monster] ECS attack rejected monster={attack.Monster.ObjectId} target={targetContext.DisplayName} event={decision.AttackEventId} reason={decision.RejectionReason} hp={decision.AfterHealth} vitals-revision={decision.AfterVitalsRevision} life-revision={decision.AfterLifeRevision}");
            return;
        }

        var killed = decision.Killed;
        if (killed)
        {
            await deathInterruptionTask;
        }

        var monster = attack.Monster;
        var target = targetContext.Character;
        var worldTargetObjectId =
            WorldObjectIds.ForPlayer(target.Id);
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
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            Remove(targetContext.Session);
        }

        foreach (var observer in map.Snapshot())
        {
            if (!observer.WorldReady ||
                ReferenceEquals(
                    observer.Session,
                    targetContext.Session) ||
                !map.IsMonsterVisibleTo(
                    observer.Session,
                    monster.ObjectId))
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
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(observer.Session);
            }
        }

        if (killed)
        {
            try
            {
                await RemovePersistentRuntimeStatusForLifeRevisionAndPublishAsync(
                    targetContext.Session,
                    decision.AfterLifeRevision,
                    MountCatalog.RuntimeStatusKind,
                    DateTimeOffset.UtcNow,
                    "mount-death",
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[mount] failed clearing Ride after death character={targetContext.DisplayName}: {ex.Message}");
            }

            map.ClearMonsterAggroForCharacter(
                targetCharacterId,
                DateTimeOffset.UtcNow);
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
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[monster] victim vitals persistence deferred character={targetContext.DisplayName}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[monster] attack monster={monster.ObjectId} tier={monster.Definition.Tier} target={targetContext.DisplayName} damage={damage} hp={target.CurrentHp}/{target.MaxHp} killed={killed}");
    }

    private ulong ResolveMonsterAttackEventId(
        MonsterRuntimeUpdate attack)
    {
        if (attack.AttackEventId != 0)
        {
            return attack.AttackEventId;
        }

        return _monsterAttackEventIdentities.GetValue(
            attack,
            _ => new MonsterAttackEventIdentity(
                checked((ulong)++_nextMonsterAttackEventId)))
            .Value;
    }

    private sealed class MonsterAttackTargetUnavailableException(
        int targetCharacterId) : Exception
    {
        public int TargetCharacterId { get; } = targetCharacterId;
    }

    private sealed record MonsterAttackEventIdentity(ulong Value);
}
