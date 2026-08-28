using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    private Action<string>?
        _protocolCheckBeforeMedusaNativePrefixPacket = null;
#endif

    private async Task PublishMonsterAttackNativeSequenceAsync(
        WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack,
        MonsterAttackEcsTransaction transaction,
        IReadOnlyList<MonsterAttackPublicationRecipient> recipients,
        GameSessionContext targetContext,
        CombatResolution resolution,
        MonsterIncomingElementalPostCommit elementalPostCommit,
        bool killed,
        MedusaBleedNativePrefixAdmission bleedNativePrefix,
        CancellationToken cancellationToken)
    {
        var decision = transaction.Decision;
        var monster = attack.Monster;
        var target = targetContext.Character;
        var worldTargetObjectId = targetContext.ObjectId;
        var impactSkillId = ResolveMedusaMonsterImpactSkillId(
            attack,
            transaction);
        var lethalVitalsRevision = killed
            ? decision.AfterVitalsRevision
            : (long?)null;
        var exactPrefixRequired =
            bleedNativePrefix.Required ||
            HasMedusaProjectionObligation(transaction) ||
            transaction.MedusaEffectInterruption is
            {
                Claimed: true,
                RequiresNotification: true
            };

        var publishSelfSuffix = bleedNativePrefix.Required
            ? bleedNativePrefix.SelfAdmitted
            : true;
        if (!bleedNativePrefix.Required)
        {
            try
            {
                InvokeProtocolCheckBeforeMedusaNativePrefixPacket(
                    "SelfImpact");
                var selfImpact = PacketBuilder.SkillCastImpact(
                    monster.ObjectId,
                    LocalPlayerObjectId,
                    impactSkillId,
                    attack.TargetX,
                    attack.TargetZ);
                var selfBasicImpact = PacketBuilder.SkillCastImpact(
                    monster.ObjectId,
                    LocalPlayerObjectId,
                    DefaultMonsterImpactSkillId,
                    attack.TargetX,
                    attack.TargetZ);

                InvokeProtocolCheckBeforeMedusaNativePrefixPacket(
                    "SelfDamage");
                var selfDamage = PacketBuilder.PhysicalDamage(
                    monster.ObjectId,
                    monster.X,
                    monster.Y,
                    monster.Z,
                    LocalPlayerObjectId,
                    resolution.CapturedDamageValue,
                    result: 0,
                    damageType: (byte)resolution.Outcome);
                ReadOnlyMemory<byte>[] selfPrefix =
                    [selfImpact, selfBasicImpact, selfDamage];
                var selfPrefixAdmitted =
                    TrySendMonsterAttackPacketBatchExactOutcome(
                        runtime,
                        targetContext,
                        decision.AfterLifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        selfPrefix,
                        cancellationToken,
                        "MonsterAttackPrefixSelf",
                        lethalVitalsRevision,
                        killed);
                if (exactPrefixRequired &&
                    !WasMonsterAttackBatchOwned(selfPrefixAdmitted))
                {
                    return;
                }
            }
            catch (Exception error)
            {
                publishSelfSuffix = false;
                if (exactPrefixRequired)
                {
                    FailClosedMonsterAttackPrefixTarget(
                        targetContext,
                        decision.AfterLifeRevision);
                    return;
                }
                if (error is IOException or ObjectDisposedException)
                {
                    Remove(targetContext.Session);
                }
                Console.WriteLine(
                    $"[monster] self publication deferred target={targetContext.DisplayName}: {error.Message}");
            }
        }

        if (publishSelfSuffix)
        {
            try
            {
                if (decision.PetHealing is { } selfHealing)
                {
                    await PublishPetHealingTalentAsync(
                        runtime,
                        targetContext,
                        target,
                        LocalPlayerObjectId,
                        selfHealing,
                        "Self",
                        cancellationToken,
                        decision.AfterLifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        lethalVitalsRevision,
                        killed);
                }
                if (elementalPostCommit.RecoveryApplied)
                {
                    await TrySendMonsterAttackPacketExactAsync(
                        runtime,
                        targetContext,
                        decision.AfterLifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        PacketBuilder.PlayerVitalsUpdate(
                            LocalPlayerObjectId,
                            elementalPostCommit.AfterHealth,
                            elementalPostCommit.AfterMana),
                        cancellationToken,
                        "MonsterElementalGuardRecoverySelf",
                        lethalVitalsRevision,
                        killed);
                }
                if (killed)
                {
                    await TrySendMonsterAttackPacketExactAsync(
                        runtime,
                        targetContext,
                        decision.AfterLifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        PacketBuilder.PlayerDeath(
                            LocalPlayerObjectId,
                            target.PositionX,
                            0f,
                            target.PositionZ,
                            target.CurrentMap),
                        cancellationToken,
                        "MonsterKillPlayerSelf",
                        lethalVitalsRevision,
                        requireTargetDead: true);
                }
            }
            catch (Exception error)
            {
                if (exactPrefixRequired)
                {
                    FailClosedMonsterAttackPrefixTarget(
                        targetContext,
                        decision.AfterLifeRevision);
                    if (!bleedNativePrefix.Required)
                    {
                        return;
                    }
                }
                else if (error is IOException or ObjectDisposedException)
                {
                    Remove(targetContext.Session);
                }
                Console.WriteLine(
                    $"[monster] self publication deferred target={targetContext.DisplayName}: {error.Message}");
            }
        }

        for (var recipientIndex = 0;
             recipientIndex < recipients.Count;
             recipientIndex++)
        {
            var captured = recipients[recipientIndex];
            var observer = RebaseMedusaPostCommitContext(
                transaction.MedusaOutcome,
                captured.Context,
                captured.LifeRevision)!;
            if (!observer.WorldReady ||
                ReferenceEquals(observer.Session, targetContext.Session) ||
                (bleedNativePrefix.Required
                    ? !bleedNativePrefix.IsObserverAdmitted(recipientIndex)
                    : !runtime.Map.IsMonsterVisibleTo(
                        observer.Session,
                        monster.ObjectId)))
            {
                continue;
            }

            try
            {
                if (!bleedNativePrefix.Required)
                {
                    InvokeProtocolCheckBeforeMedusaNativePrefixPacket(
                        "WorldImpact");
                    var worldImpact = PacketBuilder.SkillCastImpact(
                        monster.ObjectId,
                        worldTargetObjectId,
                        impactSkillId,
                        attack.TargetX,
                        attack.TargetZ);
                    var worldBasicImpact = PacketBuilder.SkillCastImpact(
                        monster.ObjectId,
                        worldTargetObjectId,
                        DefaultMonsterImpactSkillId,
                        attack.TargetX,
                        attack.TargetZ);

                    InvokeProtocolCheckBeforeMedusaNativePrefixPacket(
                        "WorldDamage");
                    var worldDamage = PacketBuilder.PhysicalDamage(
                        monster.ObjectId,
                        monster.X,
                        monster.Y,
                        monster.Z,
                        worldTargetObjectId,
                        resolution.CapturedDamageValue,
                        result: 0,
                        damageType: (byte)resolution.Outcome);
                    ReadOnlyMemory<byte>[] worldPrefix =
                        [worldImpact, worldBasicImpact, worldDamage];
                    var worldPrefixAdmitted =
                        TrySendMonsterAttackPacketBatchExactOutcome(
                            runtime,
                            observer,
                            captured.LifeRevision,
                            targetContext,
                            decision.AfterLifeRevision,
                            worldPrefix,
                            cancellationToken,
                            "MonsterAttackPrefixWorld",
                            lethalVitalsRevision,
                            killed);
                    if (exactPrefixRequired &&
                        !WasMonsterAttackBatchOwned(worldPrefixAdmitted))
                    {
                        continue;
                    }
                }

                if (decision.PetHealing is { } worldHealing)
                {
                    await PublishPetHealingTalentAsync(
                        runtime,
                        observer,
                        target,
                        worldTargetObjectId,
                        worldHealing,
                        "World",
                        cancellationToken,
                        captured.LifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        lethalVitalsRevision,
                        killed);
                }
                if (elementalPostCommit.RecoveryApplied)
                {
                    await TrySendMonsterAttackPacketExactAsync(
                        runtime,
                        observer,
                        captured.LifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        PacketBuilder.PlayerVitalsUpdate(
                            worldTargetObjectId,
                            elementalPostCommit.AfterHealth,
                            elementalPostCommit.AfterMana),
                        cancellationToken,
                        "MonsterElementalGuardRecoveryWorld",
                        lethalVitalsRevision,
                        killed);
                }
                if (killed)
                {
                    await TrySendMonsterAttackPacketExactAsync(
                        runtime,
                        observer,
                        captured.LifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        PacketBuilder.PlayerDeath(
                            worldTargetObjectId,
                            target.PositionX,
                            0f,
                            target.PositionZ,
                            target.CurrentMap),
                        cancellationToken,
                        "MonsterKillPlayerWorld",
                        lethalVitalsRevision,
                        requireTargetDead: true);
                }
            }
            catch (Exception error)
            {
                if (exactPrefixRequired)
                {
                    FailClosedMonsterAttackPrefixRecipient(
                        targetContext,
                        decision.AfterLifeRevision,
                        observer,
                        captured.LifeRevision);
                    continue;
                }
                if (error is IOException or ObjectDisposedException)
                {
                    Remove(observer.Session);
                }
                Console.WriteLine(
                    $"[monster] observer publication deferred observer={observer.DisplayName}: {error.Message}");
            }
        }
    }

    private void FailClosedMonsterAttackPrefixTarget(
        GameSessionContext target,
        long targetLifeRevision)
    {
        if (TryClaimExactMedusaMembershipDisconnect(
                target,
                targetLifeRevision,
                out var claimed))
        {
            CompleteClaimedExactStatusDisconnect(claimed);
        }
    }

    private void FailClosedMonsterAttackPrefixRecipient(
        GameSessionContext target,
        long targetLifeRevision,
        GameSessionContext recipient,
        long recipientLifeRevision)
    {
        if (TryClaimExactMedusaPublicationPairDisconnect(
                target,
                targetLifeRevision,
                recipient,
                recipientLifeRevision,
                out var claimed))
        {
            CompleteClaimedExactStatusDisconnect(claimed);
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckBeforeMedusaNativePrefixPacket(
        string stage)
    {
#if DEBUG
        _protocolCheckBeforeMedusaNativePrefixPacket?.Invoke(stage);
#endif
    }
}
