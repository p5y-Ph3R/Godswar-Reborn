using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task ProcessMonsterAttackEcsAsync(
        WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack,
        CancellationToken cancellationToken,
        DateTimeOffset? capturedWorldTime = null)
    {
        if (attack.TargetCharacterId is not { } targetCharacterId)
        {
            return;
        }

        var members = SnapshotMonsterAttackMembers(runtime);
        var publicationRecipients =
            CaptureMonsterAttackPublicationRecipients(runtime, members);
        var statusContext = members.FirstOrDefault(context =>
            context.WorldReady &&
            context.CharacterId == targetCharacterId);
        var damageResolvedAt =
            capturedWorldTime?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
#if DEBUG
        damageResolvedAt = ResolveMonsterAttackTimeForProtocolCheck(
            damageResolvedAt);
#endif
        var runtimeMitigation = default(RuntimeIncomingDamageMitigation);
        if (statusContext is not null &&
            !TryPreviewRuntimeIncomingDamageMitigation(
                statusContext.Session,
                damageResolvedAt,
                out runtimeMitigation))
        {
            throw new MonsterAttackTargetUnavailableException(
                targetCharacterId);
        }

        var monsterProfile = _gameplayCatalogs.MonsterCombatProfiles.Resolve(
            attack.Monster.Definition);
        var combatEventId = ResolveMonsterAttackEventId(attack);
        var transaction = ResolveMonsterAttackEcsTransaction(
            runtime,
            attack,
            members,
            statusContext,
            targetCharacterId,
            damageResolvedAt,
            runtimeMitigation,
            monsterProfile,
            combatEventId,
            cancellationToken);
        if (transaction.MedusaOwnerInvariantFault)
        {
            transaction.TerminalClear?
                .FailClosedPreparedMembersNonThrowing();
        }
        else if (transaction.TimedOutMedusaInstance.IsValid)
        {
            if (transaction.TerminalClear is { } terminalClear &&
                terminalClear.InstanceId ==
                    transaction.TimedOutMedusaInstance)
            {
                terminalClear.ScheduleNonThrowing(damageResolvedAt);
            }
            else
            {
                transaction.TerminalClear?
                    .FailClosedPreparedMembersNonThrowing();
            }
        }
        var medusaStatusCompleted = false;
        try
        {
        InvokeProtocolCheckAfterMedusaTransaction();
        var decision = transaction.Decision;
        var targetContext = RebaseMedusaPostCommitContext(
            transaction.MedusaOutcome,
            transaction.TargetContext,
            decision.AfterLifeRevision);
        var resolution = transaction.Resolution;
        var damage = transaction.Damage;
        var reboundDamage = transaction.ReboundDamage;
        var elementalPostCommit = transaction.ElementalPostCommit;
        var deathInterruptionTask = transaction.DeathInterruptionTask;
        var rideStatusRemoved = transaction.RideStatusRemoved;
        if (transaction.ElementalPostCommitError is { } postCommitError)
        {
            Console.WriteLine(
                $"[monster] elemental postcommit deferred target={targetContext?.DisplayName ?? targetCharacterId.ToString()}: {postCommitError.Message}");
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

        if (transaction.ReplayRejected)
        {
            Console.WriteLine(
                $"[monster] ECS replay rejected monster={attack.Monster.ObjectId} target={targetContext.DisplayName} event={combatEventId}");
            return;
        }
        if (transaction.AuthorityRejected)
        {
            Console.WriteLine(
                $"[medusa] ECS attack authority rejected monster={attack.Monster.ObjectId} target={targetContext.DisplayName} event={combatEventId} outcome={transaction.MedusaOutcome}");
            return;
        }

        var acceptedZeroResolution =
            !decision.Applied &&
            resolution.Damage == 0 &&
            decision.RejectionReason ==
                MonsterPlayerDamageRejectionReason.ZeroDamage;
        var acceptedInvariantFault =
            transaction.MedusaOutcome ==
                MedusaMonsterPlayerHitCommitOutcome
                    .AppliedWithoutEffectInvariantFault;
        if (!decision.Applied &&
            !acceptedZeroResolution &&
            !acceptedInvariantFault)
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

        var killed = decision.Killed;
        var bleedNativePrefix = AdmitCommittedBleedNativePrefix(
            runtime,
            attack,
            transaction,
            publicationRecipients,
            targetContext,
            resolution);
        var reboundCommit = default(PveMonsterReboundCommit);
        var elementalReflection = PveElementalCommitResult.Empty;
        try
        {
            reboundCommit = CommitMonsterRebound(
                runtime,
                targetContext,
                attack.Monster,
                combatEventId,
                decision.AppliedDamage,
                reboundDamage);
            elementalReflection =
                CommitMonsterIncomingElementalReflection(
                    runtime,
                    targetContext,
                    reboundCommit.DamageResult?.Monster ??
                        attack.Monster,
                    elementalPostCommit.Reflection);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[monster] secondary damage deferred target={targetContext.DisplayName}: {ex.Message}");
        }

        try
        {
            InvokeProtocolCheckBeforeMedusaBleedVitalsPersistence(
                bleedNativePrefix.Required);
            var persistence = PersistRoutineVitalsAsync(
                targetContext,
                CancellationToken.None);
            if (HasMedusaProjectionObligation(transaction) &&
                !persistence.IsCompletedSuccessfully)
            {
                ObserveDeferredMedusaVitalsPersistence(
                    targetContext,
                    persistence);
            }
            else
            {
                await persistence;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[monster] victim vitals persistence deferred character={targetContext.DisplayName}: {ex.Message}");
        }

        if (killed)
        {
            try
            {
                // Completion-owned effects linearize before death
                // publication. Every later lethal send revalidates the
                // committed HP/vitals epoch, so an intervening same-life
                // recovery suppresses stale impact/damage/death packets.
                await deathInterruptionTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[monster] death interruption notification deferred target={targetContext.DisplayName}: {ex.Message}");
            }
        }

        if (rideStatusRemoved)
        {
            try
            {
                await PublishRuntimeStatusRemovalAsync(
                    targetContext.Session,
                    DateTimeOffset.UtcNow,
                    "mount-death",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[mount] Ride removal publication deferred character={targetContext.DisplayName}: {ex.Message}");
            }
        }

        var publicationCancellation = killed ||
            transaction.MedusaOutcome is not null
                ? CancellationToken.None
                : cancellationToken;
        await PublishMonsterAttackNativeSequenceAsync(
            runtime,
            attack,
            transaction,
            publicationRecipients,
            targetContext,
            resolution,
            elementalPostCommit,
            killed,
            bleedNativePrefix,
            publicationCancellation);

        // The opaque control claim already owns the exact pending cast
        // generation. Preserve client order: committed impact/damage first,
        // complete status replacement second, interruption notice last.
        // Every fallible publication above is caught, so this completion
        // point cannot be bypassed for an accepted nonlethal Medusa effect.
        await CompleteMedusaStatusPublicationAsync(
            targetContext,
            transaction,
            damageResolvedAt);
        medusaStatusCompleted = true;

        PreparedPveMonsterKillReward? preparedReboundReward = null;
        IReadOnlyList<PreparedPveMonsterKillReward>
            preparedElementalRewards = [];
        try
        {
            preparedReboundReward =
                await PrepareMonsterReboundRewardAsync(
                    targetContext,
                    reboundCommit);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[monster] rebound reward preparation deferred target={targetContext.DisplayName}: {ex.Message}");
        }
        try
        {
            preparedElementalRewards =
                await PreparePveElementalKillRewardsAsync(
                    targetContext,
                    elementalReflection);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[monster] elemental reward preparation deferred target={targetContext.DisplayName}: {ex.Message}");
        }

        try
        {
            await PublishMonsterReboundAsync(
                runtime,
                targetContext,
                reboundCommit,
                preparedReboundReward,
                publicationCancellation);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[monster] rebound publication deferred target={targetContext.DisplayName}: {ex.Message}");
        }
        try
        {
            await PublishPveElementalCommitAsync(
                targetContext.Session,
                elementalReflection,
                publicationCancellation,
                capturedSource: targetContext,
                preparedRewards: preparedElementalRewards);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[monster] elemental publication deferred target={targetContext.DisplayName}: {ex.Message}");
        }

        var impactSkillId = ResolveMedusaMonsterImpactSkillId(
            attack,
            transaction);
        Console.WriteLine(
            $"[monster] attack monster={attack.Monster.ObjectId} " +
            $"tier={attack.Monster.Definition.Tier} " +
            $"target={targetContext.DisplayName} " +
            $"impact-skill={impactSkillId} damage={damage} " +
            $"hp={targetContext.Character.CurrentHp}/" +
            $"{targetContext.Character.MaxHp} killed={killed}");
        }
        finally
        {
            if (!medusaStatusCompleted)
            {
                await CompleteAbandonedMedusaStatusPublicationAsync(
                    transaction);
            }
        }
    }

}
