using System.Runtime.CompilerServices;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConditionalWeakTable<
        MonsterRuntimeUpdate,
        MonsterAttackEventIdentity> _monsterAttackEventIdentities =
        new();
    private long _nextMonsterAttackEventId;

    private async Task ProcessMonsterAttackEcsAsync(
        WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken)
    {
        if (attack.TargetCharacterId is not { } targetCharacterId)
        {
            return;
        }

        GameSessionContext? targetContext;
        var members = SnapshotMonsterAttackMembers(runtime);
        var statusContext = members.FirstOrDefault(context =>
            context.WorldReady &&
            context.CharacterId == targetCharacterId);
        var damageResolvedAt = DateTimeOffset.UtcNow;
        // Match the legacy lock order: runtime status first, then registry,
        // then character vitals inside the lifecycle-owned ECS adapter.
        var runtimeMitigation = default(RuntimeIncomingDamageMitigation);
        if (statusContext is not null &&
            !TryGetRuntimeIncomingDamageMitigation(
                statusContext.Session,
                damageResolvedAt,
                out runtimeMitigation))
        {
            throw new MonsterAttackTargetUnavailableException(
                targetCharacterId);
        }
        PlayerMonsterDamageEcsDecision decision = default;
        uint damage = 0;
        var monsterProfile = _gameplayCatalogs.MonsterCombatProfiles.Resolve(
            attack.Monster.Definition);
        var combatEventId = ResolveMonsterAttackEventId(attack);
        CombatResolution resolution = default;
        uint reboundDamage = 0;
        var replayRejected = false;
        var elementalAttempt = default(MonsterIncomingElementalAttempt);
        var elementalPostCommit =
            default(MonsterIncomingElementalPostCommit);
        var petHealingReceivedBasisPoints =
            ElementalBasisPointMath.Denominator;
        var deathInterruptionTask = Task.CompletedTask;
        lock (_gate)
        {
            targetContext = ResolveCurrentMonsterAttackTarget(
                runtime,
                members,
                targetCharacterId);
            if (targetContext is not null)
            {
                if (statusContext is null ||
                    !ReferenceEquals(
                        statusContext.Session,
                        targetContext.Session))
                {
                    runtimeMitigation = default;
                }

                lock (targetContext.Character.VitalsSync)
                {
                    var targetCombat =
                        MonsterIncomingCombatPolicy.ResolveTargetStats(
                            targetContext.Character,
                            runtimeMitigation);
                    var effectiveMonsterProfile =
                        AdjustPveMonsterAttackerProfile(
                            targetContext.Session,
                            attack.Monster,
                            damageResolvedAt,
                            monsterProfile);
                    resolution = MonsterIncomingCombatPolicy.ResolveAttack(
                            effectiveMonsterProfile,
                            targetContext.Character,
                            runtimeMitigation,
                            combatEventId);
                    var currentLifeRevision =
                        _playerLifeRevisions.GetOrAdd(
                            targetContext.Session,
                            0);
                    var canApplyElemental =
                        CanApplyEcsMonsterIncomingPreResolution(
                            targetContext,
                            attack,
                            combatEventId);
                    if (canApplyElemental &&
                        !TryClaimMonsterIncomingAttack(
                            targetContext,
                            attack.Monster,
                            combatEventId))
                    {
                        replayRejected = true;
                    }
                    else
                    {
                        if (canApplyElemental)
                        {
                            resolution =
                                AdjustMonsterIncomingElementalDamageLocked(
                                    targetContext,
                                    attack.Monster,
                                    combatEventId,
                                    damageResolvedAt,
                                    resolution,
                                    out elementalAttempt);
                            petHealingReceivedBasisPoints = checked((int)
                                Math.Clamp(
                                    AdjustMonsterIncomingElementalHealingLocked(
                                        targetContext,
                                        damageResolvedAt
                                            .ToUnixTimeMilliseconds(),
                                        ElementalBasisPointMath.Denominator),
                                    0,
                                    ElementalBasisPointMath.Denominator));
                        }

                        damage = resolution.Damage;
                        decision = ResolvePlayerVitalsDamageEcs(
                            targetContext.Session,
                            targetContext.Character,
                            targetContext.ObjectId,
                            new PlayerMonsterDamageEcsRequest(
                                combatEventId,
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
                                damage,
                                damageResolvedAt,
                                petHealingReceivedBasisPoints),
                            beforeLethalCommit: () =>
                            {
                                // The ECS decision is accepted and lethal,
                                // but vitals have not been committed yet.
                                deathInterruptionTask =
                                    RequestSkillCastInterruptionAsync(
                                        targetContext.Session,
                                        SkillCastInterruptionReason.Death,
                                        cancellationToken);
                            });
                        if (decision.Applied)
                        {
                            reboundDamage =
                                CombatSecondaryEffectPolicy.Resolve(
                                        decision.AppliedDamage,
                                        default,
                                        targetCombat)
                                    .ReboundDamage;
                        }

                        var acceptedMiss =
                            !decision.Applied &&
                            resolution.Outcome == CombatHitOutcome.Miss &&
                            decision.RejectionReason ==
                                MonsterPlayerDamageRejectionReason.ZeroDamage;
                        if (canApplyElemental &&
                            (decision.Applied || acceptedMiss))
                        {
                            elementalPostCommit =
                                CommitMonsterIncomingElementalLocked(
                                    targetContext,
                                    attack.Monster,
                                    elementalAttempt,
                                    decision.AppliedDamage);
                        }
                    }
                }
            }
        }

        if (targetContext is null)
        {
            ClearMonsterAttackAggro(
                runtime,
                targetCharacterId,
                DateTimeOffset.UtcNow);
            return;
        }

        if (replayRejected)
        {
            Console.WriteLine(
                $"[monster] ECS replay rejected monster={attack.Monster.ObjectId} target={targetContext.DisplayName} event={combatEventId}");
            return;
        }

        // The damage system records the event ID before rejecting zero. Treat
        // that one rejection as an accepted attack attempt so the miss is
        // published once without changing HP or the vitals revision.
        var missed =
            !decision.Applied &&
            resolution.Outcome == CombatHitOutcome.Miss &&
            decision.RejectionReason ==
                MonsterPlayerDamageRejectionReason.ZeroDamage;
        if (!decision.Applied && !missed)
        {
            if (decision.RejectionReason is not (
                    MonsterPlayerDamageRejectionReason
                        .DuplicateAttackEvent or
                    MonsterPlayerDamageRejectionReason
                        .StaleAttackEvent))
            {
                ClearMonsterAttackAggro(
                    runtime,
                    targetCharacterId,
                    DateTimeOffset.UtcNow);
            }

            Console.WriteLine(
                $"[monster] ECS attack rejected monster={attack.Monster.ObjectId} target={targetContext.DisplayName} event={decision.AttackEventId} reason={decision.RejectionReason} hp={decision.AfterHealth} vitals-revision={decision.AfterVitalsRevision} life-revision={decision.AfterLifeRevision}");
            return;
        }

        var reboundCommit = CommitMonsterRebound(
            targetContext,
            attack.Monster,
            combatEventId,
            decision.AppliedDamage,
            reboundDamage);
        var elementalReflection =
            CommitMonsterIncomingElementalReflection(
                targetContext,
                attack.Monster,
                elementalPostCommit.Reflection);
        var preparedReboundReward =
            await PrepareMonsterReboundRewardAsync(
                targetContext,
                reboundCommit);
        var preparedElementalRewards =
            await PreparePveElementalKillRewardsAsync(
                targetContext,
                elementalReflection);

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
            await TrySendWorldInstancePacketAsync(
                runtime,
                targetContext,
                PacketBuilder.SkillCastImpact(
                    monster.ObjectId,
                    LocalPlayerObjectId,
                    2000,
                    attack.TargetX,
                    attack.TargetZ),
                cancellationToken,
                "MonsterAttackImpactSelf");
            await TrySendWorldInstancePacketAsync(
                runtime,
                targetContext,
                PacketBuilder.PhysicalDamage(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    LocalPlayerObjectId,
                    resolution.CapturedDamageValue,
                    result: 0,
                    damageType: (byte)resolution.Outcome),
                cancellationToken,
                "MonsterAttackDamageSelf");
            if (decision.PetHealing is { } selfHealing)
            {
                await PublishPetHealingTalentAsync(
                    runtime,
                    targetContext,
                    target,
                    LocalPlayerObjectId,
                    selfHealing,
                    "Self",
                    cancellationToken);
            }
            if (elementalPostCommit.RecoveryApplied)
            {
                await TrySendWorldInstancePacketAsync(
                    runtime,
                    targetContext,
                    PacketBuilder.PlayerVitalsUpdate(
                        LocalPlayerObjectId,
                        elementalPostCommit.AfterHealth,
                        elementalPostCommit.AfterMana),
                    cancellationToken,
                    "MonsterElementalGuardRecoverySelf");
            }
            if (killed)
            {
                await TrySendWorldInstancePacketAsync(
                    runtime,
                    targetContext,
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

        foreach (var observer in SnapshotMonsterAttackMembers(runtime))
        {
            if (!observer.WorldReady ||
                ReferenceEquals(
                    observer.Session,
                    targetContext.Session) ||
                !runtime.Map.IsMonsterVisibleTo(
                    observer.Session,
                    monster.ObjectId))
            {
                continue;
            }

            try
            {
                await TrySendWorldInstancePacketAsync(
                    runtime,
                    observer,
                    PacketBuilder.SkillCastImpact(
                        monster.ObjectId,
                        worldTargetObjectId,
                        2000,
                        attack.TargetX,
                        attack.TargetZ),
                    cancellationToken,
                    "MonsterAttackImpactWorld");
                await TrySendWorldInstancePacketAsync(
                    runtime,
                    observer,
                    PacketBuilder.PhysicalDamage(
                        monster.ObjectId,
                        monster.X,
                        monster.Y,
                        monster.Z,
                        worldTargetObjectId,
                        resolution.CapturedDamageValue,
                        result: 0,
                        damageType: (byte)resolution.Outcome),
                    cancellationToken,
                    "MonsterAttackDamageWorld");
                if (decision.PetHealing is { } worldHealing)
                {
                    await PublishPetHealingTalentAsync(
                        runtime,
                        observer,
                        target,
                        worldTargetObjectId,
                        worldHealing,
                        "World",
                        cancellationToken);
                }
                if (elementalPostCommit.RecoveryApplied)
                {
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        observer,
                        PacketBuilder.PlayerVitalsUpdate(
                            worldTargetObjectId,
                            elementalPostCommit.AfterHealth,
                            elementalPostCommit.AfterMana),
                        cancellationToken,
                        "MonsterElementalGuardRecoveryWorld");
                }
                if (killed)
                {
                    await TrySendWorldInstancePacketAsync(
                        runtime,
                        observer,
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

        await PublishMonsterReboundAsync(
            runtime,
            targetContext,
            reboundCommit,
            preparedReboundReward,
            cancellationToken);
        await PublishPveElementalCommitAsync(
            targetContext.Session,
            elementalReflection,
            cancellationToken,
            capturedSource: targetContext,
            preparedRewards: preparedElementalRewards);

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

            ClearMonsterAttackAggro(
                runtime,
                targetCharacterId,
                DateTimeOffset.UtcNow);
        }

        try
        {
            await PersistRoutineVitalsAsync(
                targetContext,
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[monster] victim vitals persistence deferred character={targetContext.DisplayName}: {ex.Message}");
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
                checked((ulong)Interlocked.Increment(
                    ref _nextMonsterAttackEventId))))
            .Value;
    }

    private sealed class MonsterAttackTargetUnavailableException(
        int targetCharacterId) : Exception
    {
        public int TargetCharacterId { get; } = targetCharacterId;
    }

    private sealed record MonsterAttackEventIdentity(ulong Value);
}
