using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal Task ProcessMonsterAttackForSessionAsync(
        ClientSession session,
        MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryGetValue(
                session,
                out var context) ||
            !TryGetWorldInstance(context, out var runtime))
        {
            throw new MonsterAttackTargetUnavailableException(
                attack.TargetCharacterId ?? 0);
        }

        return ProcessMonsterAttackAsync(
            runtime,
            attack,
            cancellationToken);
    }

    private async Task ProcessMonsterAttackAsync(
        WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken,
        DateTimeOffset? capturedWorldTime = null)
    {
        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await ProcessMonsterAttackEcsAsync(
                runtime,
                attack,
                cancellationToken,
                capturedWorldTime);
            return;
        }

        // A bound Medusa run owns ECS-only source, life, and effect
        // authority. Falling through to legacy HP mutation would discard
        // those fences. Ordinary unbound legacy maps retain their existing
        // behavior.
        if (IsLegacyMedusaMonsterAttackUnsupported(runtime))
        {
            Console.WriteLine(
                $"[medusa] rejected legacy monster attack " +
                $"instance={runtime.InstanceId} " +
                $"monster={attack.Monster.ObjectId}");
            return;
        }

        await ProcessMonsterAttackLegacyAsync(
            runtime,
            attack,
            cancellationToken);
    }

    private async Task ProcessMonsterAttackLegacyAsync(
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
        // Runtime statuses have their own gate. Snapshot the mitigation before
        // taking the registry gate so status publication and a monster attack
        // cannot acquire those locks in opposite order.
        var runtimeMitigation = default(RuntimeIncomingDamageMitigation);
        if (statusContext is not null)
        {
            TryGetRuntimeIncomingDamageMitigation(
                statusContext.Session,
                damageResolvedAt,
                out runtimeMitigation);
        }
        var monsterProfile = _gameplayCatalogs.MonsterCombatProfiles.Resolve(
            attack.Monster.Definition);
        var combatEventId = ResolveMonsterAttackEventId(attack);
        CombatResolution resolution = default;
        uint damage;
        uint appliedPlayerDamage = 0;
        uint reboundDamage = 0;
        var replayRejected = false;
        var elementalAttempt = default(MonsterIncomingElementalAttempt);
        var elementalPostCommit =
            default(MonsterIncomingElementalPostCommit);
        var killed = false;
        long? deathLifeRevision = null;
        var deathInterruptionTask = Task.CompletedTask;
        lock (_gate)
        {
            targetContext = ResolveCurrentMonsterAttackTarget(
                runtime,
                members,
                targetCharacterId,
                attack);
            if (targetContext is null)
            {
                damage = 0;
            }
            else if (!_playerLifeRevisions.TryGetValue(
                         targetContext.Session,
                         out var targetLifeRevision))
            {
                targetContext = null;
                damage = 0;
            }
            else
            {
                if (statusContext is null ||
                    !ReferenceEquals(statusContext.Session, targetContext.Session))
                {
                    runtimeMitigation = default;
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
                        if (!TryClaimMonsterIncomingAttack(
                                targetContext,
                                attack.Monster,
                                combatEventId))
                        {
                            replayRejected = true;
                            damage = 0;
                        }
                        else
                        {
                            resolution =
                                AdjustMonsterIncomingElementalDamageLocked(
                                    targetContext,
                                    attack.Monster,
                                    combatEventId,
                                    damageResolvedAt,
                                    resolution,
                                    out elementalAttempt);
                            damage = resolution.Damage;
                            var beforeHealth =
                                targetContext.Character.CurrentHp;
                            killed = resolution.Hit &&
                                damage >= (uint)beforeHealth;
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

                            if (resolution.Hit)
                            {
                                targetContext.Character.CurrentHp =
                                    damage >= (uint)beforeHealth
                                        ? 0
                                        : beforeHealth - (int)damage;
                                targetContext.Character.MarkVitalsChanged();
                                appliedPlayerDamage = checked((uint)(
                                    beforeHealth -
                                    targetContext.Character.CurrentHp));
                                reboundDamage =
                                    CombatSecondaryEffectPolicy.Resolve(
                                            appliedPlayerDamage,
                                            default,
                                            targetCombat)
                                        .ReboundDamage;
                            }

                            elementalPostCommit =
                                CommitMonsterIncomingElementalLocked(
                                    targetContext,
                                    attack.Monster,
                                    elementalAttempt,
                                    appliedPlayerDamage);
                            if (killed)
                            {
                                var advancedLifeRevision = checked(
                                    targetLifeRevision + 1);
                                if (!_playerLifeRevisions.TryUpdate(
                                        targetContext.Session,
                                        advancedLifeRevision,
                                        targetLifeRevision))
                                {
                                    throw new InvalidOperationException(
                                        "Established player life authority " +
                                        "changed while the registry gate " +
                                        "was held.");
                                }
                                deathLifeRevision = advancedLifeRevision;
                            }
                        }
                    }
                }
            }
        }

        if (targetContext is null)
        {
            if (!HasExactEmittedMonsterTarget(attack))
            {
                ClearMonsterAttackAggro(
                    runtime,
                    targetCharacterId,
                    DateTimeOffset.UtcNow);
            }
            return;
        }

        if (replayRejected)
        {
            Console.WriteLine(
                $"[monster] replay rejected monster={attack.Monster.ObjectId} target={targetContext.DisplayName} event={combatEventId}");
            return;
        }

        var reboundCommit = CommitMonsterRebound(
            runtime,
            targetContext,
            attack.Monster,
            combatEventId,
            appliedPlayerDamage,
            reboundDamage);
        var elementalReflection =
            CommitMonsterIncomingElementalReflection(
                runtime,
                targetContext,
                reboundCommit.DamageResult?.Monster ?? attack.Monster,
                elementalPostCommit.Reflection);
        var preparedReboundReward =
            await PrepareMonsterReboundRewardAsync(
                targetContext,
                reboundCommit);
        var preparedElementalRewards =
            await PreparePveElementalKillRewardsAsync(
                targetContext,
                elementalReflection);

        if (killed)
        {
            await deathInterruptionTask;
        }

        var monster = attack.Monster;
        var target = targetContext.Character;
        var worldTargetObjectId = targetContext.ObjectId;
        try
        {
            await TrySendWorldInstancePacketAsync(
                runtime,
                targetContext,
                PacketBuilder.SkillCastImpact(
                    monster.ObjectId,
                    LocalPlayerObjectId,
                    2000,
                    monster.X,
                    monster.Z),
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Remove(targetContext.Session);
        }

        foreach (var observer in SnapshotMonsterAttackMembers(runtime))
        {
            if (!observer.WorldReady ||
                ReferenceEquals(observer.Session, targetContext.Session) ||
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
                        monster.X,
                        monster.Z),
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
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[monster] victim vitals persistence deferred character={targetContext.DisplayName}: {ex.Message}");
        }

        Console.WriteLine(
            $"[monster] attack monster={monster.ObjectId} tier={monster.Definition.Tier} target={targetContext.DisplayName} damage={damage} hp={target.CurrentHp}/{target.MaxHp} killed={killed}");
    }

}
