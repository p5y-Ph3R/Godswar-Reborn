using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private MonsterAttackEcsTransaction
        ResolveMonsterAttackEcsTransaction(
            WorldInstanceRuntime runtime,
            MonsterRuntimeUpdate attack,
            IReadOnlyList<GameSessionContext> members,
            GameSessionContext? statusContext,
            int targetCharacterId,
            DateTimeOffset damageResolvedAt,
            RuntimeIncomingDamageMitigation runtimeMitigation,
            MonsterCombatProfile monsterProfile,
            ulong combatEventId,
            CancellationToken cancellationToken)
    {
        GameSessionContext? targetContext;
        PlayerMonsterDamageEcsDecision decision = default;
        CombatResolution resolution = default;
        uint damage = 0;
        uint reboundDamage = 0;
        var replayRejected = false;
        var authorityRejected = false;
        var elementalAttempt = default(MonsterIncomingElementalAttempt);
        var elementalPostCommit =
            default(MonsterIncomingElementalPostCommit);
        var petHealingReceivedBasisPoints =
            ElementalBasisPointMath.Denominator;
        var deathInterruptionTask = Task.CompletedTask;
        var targetCombat = default(CombatTargetStats);
        var canApplyElemental = false;
        var damageRequest = default(PlayerMonsterDamageEcsRequest);
        var medusaCapture = default(MedusaMonsterPlayerHitCapture);
        MedusaMonsterPlayerHitCommitOutcome? medusaOutcome = null;
        MedusaMechanicHitResult? medusaMechanicsResult = null;
        RegistryMedusaCapturedEffectInterruption?
            medusaEffectInterruption = null;
        var elementalClaimKey = default(MonsterIncomingAttackCommitKey);
        ElementalIncomingMutationReservation? elementalReservation = null;
        var elementalClaimCompleted = false;
        var attackIrreversiblyCommitted = false;
        var transactionCompleted = false;
        var rideStatusRemoved = false;
        Exception? elementalPostCommitError = null;
        PlayerRecoveryDeadline? recoveryDeadline = null;
        MedusaRunTerminalClearWorkItem? terminalClear = null;
        WorldInstanceId timedOutMedusaInstance = default;
        var medusaOwnerInvariantFault = false;
        var nextRecoveryAt =
            damageResolvedAt + PlayerRecoveryInterval;
        var retainedStatusState =
            TryRetainMonsterAttackStatusState(statusContext);

        try
        {
            lock (_gate)
            {
            var authority = CaptureMonsterAttackEcsAuthorityLocked(
                runtime,
                attack,
                members,
                statusContext,
                targetCharacterId,
                damageResolvedAt,
                runtimeMitigation,
                monsterProfile,
                combatEventId);
            targetContext = authority.TargetContext;
            runtimeMitigation = authority.RuntimeMitigation;
            recoveryDeadline = authority.RecoveryDeadline;
            medusaCapture = authority.MedusaCapture;
            terminalClear = authority.TerminalClear;
            if (medusaCapture.Outcome ==
                MedusaMonsterPlayerHitCaptureOutcome.TimedOut)
            {
                timedOutMedusaInstance = runtime.InstanceId;
            }
            medusaOwnerInvariantFault = medusaCapture.Outcome ==
                MedusaMonsterPlayerHitCaptureOutcome.OwnerClockInvariantFault;
            if (targetContext is null)
            {
                return Result();
            }
            if (authority.AuthorityRejected)
            {
                authorityRejected = true;
                return Result();
            }

            var targetAuthority = authority.TargetAuthority;

            lock (targetContext.Character.VitalsSync)
            {
                targetCombat =
                    MonsterIncomingCombatPolicy.ResolveTargetStats(
                        targetContext.Character,
                        runtimeMitigation);
                var effectiveMonsterProfile =
                    AdjustPveMonsterAttackerProfile(
                        targetContext.Session,
                        attack.Monster,
                        damageResolvedAt,
                        medusaCapture.MonsterProfile);
                resolution = MonsterIncomingCombatPolicy.ResolveAttack(
                    effectiveMonsterProfile,
                    targetContext.Character,
                    runtimeMitigation,
                    combatEventId);
                var lastCommittedAttackEventId =
                    GetPlayerVitalsDamageEcsDiagnostics(
                        targetContext.Session)?.LastAttackEventId ?? 0;
                if (medusaCapture.IsCaptured &&
                    combatEventId <= lastCommittedAttackEventId)
                {
                    // The owner replay ledger is bounded. The ECS ledger is
                    // the durable process-local fallback and must be checked
                    // before reserving mechanics time, so an old event cannot
                    // expire or refresh an effect before ECS rejects it.
                    replayRejected = true;
                }

                canApplyElemental = !replayRejected &&
                    CanApplyEcsMonsterIncomingPreResolution(
                        targetContext,
                        attack,
                        combatEventId);
                if (!replayRejected &&
                    canApplyElemental &&
                    !TryClaimMonsterIncomingAttack(
                        targetContext,
                        attack.Monster,
                        combatEventId,
                        out elementalClaimKey))
                {
                    replayRejected = true;
                }
                else if (!replayRejected)
                {
                    if (canApplyElemental)
                    {
                        try
                        {
                            resolution = medusaCapture.IsCaptured
                                ? ReserveMonsterIncomingElementalDamageLocked(
                                    targetContext,
                                    attack.Monster,
                                    combatEventId,
                                    damageResolvedAt,
                                    resolution,
                                    out elementalAttempt,
                                    out elementalReservation)
                                : AdjustMonsterIncomingElementalDamageLocked(
                                    targetContext,
                                    attack.Monster,
                                    combatEventId,
                                    damageResolvedAt,
                                    resolution,
                                    out elementalAttempt);
                        }
                        catch
                        {
                            elementalReservation?.RollBack();
                            ReleaseMonsterIncomingAttack(
                                elementalClaimKey);
                            throw;
                        }
                        petHealingReceivedBasisPoints = checked((int)
                            Math.Clamp(
                                medusaCapture.IsCaptured
                                    ? PreviewMonsterIncomingElementalHealingLocked(
                                        targetContext,
                                        damageResolvedAt
                                            .ToUnixTimeMilliseconds(),
                                        ElementalBasisPointMath.Denominator)
                                    : AdjustMonsterIncomingElementalHealingLocked(
                                    targetContext,
                                    damageResolvedAt
                                        .ToUnixTimeMilliseconds(),
                                    ElementalBasisPointMath.Denominator),
                                0,
                                ElementalBasisPointMath.Denominator));
                    }

                    damage = resolution.Damage;
                    damageRequest = new PlayerMonsterDamageEcsRequest(
                        combatEventId,
                        attack.Monster.ObjectId,
                        attack.Monster.SpawnGeneration,
                        targetCharacterId,
                        targetAuthority.ObjectId,
                        targetAuthority.LifeRevision,
                        targetAuthority.VitalsRevision,
                        damage,
                        damageResolvedAt,
                        petHealingReceivedBasisPoints);
                    if (!medusaCapture.IsCaptured)
                    {
                        var capturedDeathInterruption =
                            CaptureDeathInterruption(
                                targetContext.Session,
                                cancellationToken);
                        decision = ResolvePlayerVitalsDamageEcs(
                            targetContext.Session,
                            targetContext.Character,
                            targetContext.ObjectId,
                            damageRequest,
                            beforeLethalCommit: () =>
                            {
                                deathInterruptionTask =
                                    capturedDeathInterruption();
                            });
                    }
                }
            }

            if (medusaCapture.IsCaptured && !replayRejected)
            {
                MedusaMonsterPlayerHitCommit medusaCommit;
                try
                {
                    var captured = CommitCapturedMedusaMonsterPlayerHit(
                        runtime,
                        targetContext,
                        medusaCapture,
                        damageRequest,
                        cancellationToken);
                    medusaCommit = captured.Commit;
                    deathInterruptionTask = captured.DeathInterruptionTask;
                    medusaEffectInterruption =
                        captured.EffectInterruption;
                }
                catch
                {
                    elementalReservation?.RollBack();
                    ReleaseMonsterIncomingAttack(elementalClaimKey);
                    throw;
                }
                medusaOutcome = medusaCommit.Outcome;
                medusaMechanicsResult = medusaCommit.MechanicsResult;
                decision = medusaCommit.VitalsDecision;
                if (medusaCommit.Outcome is
                    MedusaMonsterPlayerHitCommitOutcome.ReplayRejected or
                    MedusaMonsterPlayerHitCommitOutcome
                        .ReplayIdentityConflict)
                {
                    replayRejected = true;
                }
                else if (medusaCommit.Outcome is
                    MedusaMonsterPlayerHitCommitOutcome.AuthorityRejected or
                    MedusaMonsterPlayerHitCommitOutcome
                        .PeriodicDamageHandoffUnavailable)
                {
                    authorityRejected = true;
                }

            }

            var acceptedZeroResolution =
                !decision.Applied &&
                resolution.Damage == 0 &&
                decision.RejectionReason ==
                    MonsterPlayerDamageRejectionReason.ZeroDamage;
            var acceptedInvariantFault =
                medusaOutcome ==
                    MedusaMonsterPlayerHitCommitOutcome
                        .AppliedWithoutEffectInvariantFault;
            attackIrreversiblyCommitted =
                !replayRejected &&
                !authorityRejected &&
                (decision.Applied ||
                 acceptedZeroResolution ||
                 acceptedInvariantFault);
            if (attackIrreversiblyCommitted)
            {
                // The elemental reservation still owns its state Gate here.
                // Finish all direct-hit postcommit state while that lane is
                // retained, so movement/status work cannot interleave between
                // pre-resolution and reflection/recovery finalization.
                try
                {
                    lock (targetContext.Character.VitalsSync)
                    {
                        if (decision.Applied)
                        {
                            reboundDamage =
                                CombatSecondaryEffectPolicy.Resolve(
                                        decision.AppliedDamage,
                                        default,
                                        targetCombat)
                                    .ReboundDamage;
                        }

                        if (canApplyElemental)
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
                catch (Exception ex)
                {
                    // Player HP and the owner replay are already durable.
                    // Retain that final attack identity and continue through
                    // mandatory death cleanup; postcommit effects are
                    // best-effort after the primary transaction.
                    elementalPostCommitError = ex;
                }
                finally
                {
                    elementalReservation?.Commit();
                    CompleteMonsterIncomingAttack(elementalClaimKey);
                    elementalClaimCompleted = elementalClaimKey.IsValid;
                }
            }
            else
            {
                elementalReservation?.RollBack();
                ReleaseMonsterIncomingAttack(elementalClaimKey);
            }

            if (attackIrreversiblyCommitted && decision.Killed)
            {
                // Reflection is captured from the committed old-life
                // elemental state above.  Then clear every registry-owned
                // life subsystem before returning to any fallible I/O.  The
                // bound Map commit already cleared exact Map aggro.
                ApplyPlayerLifeAdvanceSideEffectsLocked(
                    targetContext.Session,
                    nextRecoveryAt,
                    recoveryDeadline!,
                    damageResolvedAt,
                    resetIncomingDamage: true);
                if (retainedStatusState is not null &&
                    statusContext is not null &&
                    ReferenceEquals(
                        statusContext.Session,
                        targetContext.Session))
                {
                    rideStatusRemoved =
                        RemovePersistentRuntimeStatusForLifeRevisionLocked(
                            targetContext.Session,
                            retainedStatusState,
                            decision.AfterLifeRevision,
                            MountCatalog.RuntimeStatusKind);
                }
            }

                // Idempotent defensive finalization: no invariant check may
                // escape after player HP has become irreversible.
                elementalReservation?.Commit();

                transactionCompleted = true;
                return Result();
            }
        }
        finally
        {
            if (!transactionCompleted &&
                !elementalClaimCompleted &&
                !attackIrreversiblyCommitted)
            {
                elementalReservation?.RollBack();
                ReleaseMonsterIncomingAttack(elementalClaimKey);
            }
            retainedStatusState?.Gate.Release();
        }

        MonsterAttackEcsTransaction Result() => new(
            targetContext,
            decision,
            resolution,
            damage,
            reboundDamage,
            replayRejected,
            authorityRejected,
            elementalPostCommit,
            deathInterruptionTask,
            medusaOutcome,
            medusaCapture.SourceAuthority,
            medusaMechanicsResult,
            medusaEffectInterruption,
            rideStatusRemoved,
            elementalPostCommitError,
            terminalClear,
            timedOutMedusaInstance,
            medusaOwnerInvariantFault);
    }

}
