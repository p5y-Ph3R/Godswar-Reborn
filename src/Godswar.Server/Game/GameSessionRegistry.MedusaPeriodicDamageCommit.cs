using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private bool TryApplyMedusaPeriodicDamage(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        int recipientCount,
        in MedusaPeriodicDamageTargetCapture target,
        ulong eventId,
        MedusaPeriodicDamageHpCommitObserver hpObserver,
        out PlayerMonsterDamageEcsDecision decision)
    {
        decision = default;
        if (!TryGetPeriodicSelf(
                _medusaPeriodicDamageLedger,
                handle,
                recipientCount,
                out var self))
        {
            return false;
        }

        lock (_gate)
        {
            if (!TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    self.Context,
                    target.Authority.LifeRevision,
                    out var current) ||
                !MatchesPeriodicTargetCapture(current, target))
            {
                return false;
            }
            lock (current.Character.VitalsSync)
            {
                if (current.Character.CurrentHp != target.CurrentHealth ||
                    current.Character.VitalsRevision !=
                        target.Authority.VitalsRevision)
                {
                    return false;
                }
            }

            var identity = handle.Identity;
            var request = new PlayerMonsterDamageEcsRequest(
                eventId,
                identity.SourceObjectId,
                identity.SourceSpawnGeneration,
                identity.TargetCharacterId,
                target.Authority.ObjectId,
                target.Authority.LifeRevision,
                target.Authority.VitalsRevision,
                identity.Damage,
                identity.DueAt,
                HealingReceivedBasisPoints: 0);
            decision = GetPlayerRuntimeEcs(current.Session)
                .IncomingDamage.Apply(
                    current.Character,
                    current.ObjectId,
                    target.Authority.LifeRevision,
                    request,
                    beforeLethalCommit: null,
                    periodicHpCommitObserver: hpObserver);
            return true;
        }
    }

    private static bool MatchesPeriodicTargetCapture(
        GameSessionContext current,
        in MedusaPeriodicDamageTargetCapture target) =>
        current.WorldInstanceId == target.Authority.WorldInstanceId &&
        current.WorldRevision >= target.Authority.WorldRevision &&
        current.WorldMembershipEpoch ==
            target.Authority.WorldMembershipEpoch &&
        current.Ownership == target.Authority.Ownership &&
        current.CharacterId == target.Authority.CharacterId &&
        current.ObjectId == target.Authority.ObjectId;

    private async Task<bool> CompleteAndPublishMedusaPeriodicDamageAsync(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        MedusaPeriodicDamageLedgerSnapshot snapshot,
        DateTimeOffset now)
    {
        if (snapshot.HpCommit is null)
        {
            if (snapshot.ActualOwnerDisposition !=
                MedusaPeriodicDamageDispositionOutcome.Terminal)
            {
                return false;
            }
            SettleRemainingPeriodicRecipientsWithoutAdmission(handle);
            return _medusaPeriodicDamageLedger.MarkPublished(handle) is
                MedusaPeriodicDamageLedgerMutationOutcome.Published or
                MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
        }

        var hpCommit = snapshot.HpCommit.Value;
        if (hpCommit.AfterHealth == 0)
        {
            if (!TryAdvanceMedusaPeriodicLethalLife(
                    runtime,
                    handle,
                    snapshot,
                    hpCommit) ||
                !TryFinalizeMedusaPeriodicLethalOwnerCleanup(
                    runtime,
                    handle) ||
                !TryApplyMedusaPeriodicLethalRegistrySideEffects(
                    runtime,
                    handle,
                    snapshot,
                    hpCommit,
                    now))
            {
                return false;
            }
        }

        await PublishMedusaPeriodicDamageAsync(
            runtime,
            handle,
            snapshot,
            hpCommit);
        return _medusaPeriodicDamageLedger.MarkPublished(handle) is
            MedusaPeriodicDamageLedgerMutationOutcome.Published or
            MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
    }

    private bool TryAdvanceMedusaPeriodicLethalLife(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        in MedusaPeriodicDamageLedgerSnapshot snapshot,
        in MedusaPeriodicDamageHpCommitEvidence hpCommit)
    {
        if (snapshot.LethalLifeAdvanced)
        {
            return true;
        }
        if (!TryGetPeriodicSelf(
                _medusaPeriodicDamageLedger,
                handle,
                snapshot.RecipientCount,
                out var self))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_playerLifeRevisions.TryGetValue(
                    self.Session,
                    out var observed) ||
                observed != hpCommit.BeforeLifeRevision &&
                observed != hpCommit.AfterLifeRevision ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    self.Context,
                    observed,
                    out var current) ||
                !MatchesPeriodicTargetCapture(current, snapshot.Target))
            {
                return false;
            }
            lock (current.Character.VitalsSync)
            {
                if (current.Character.CurrentHp != 0 ||
                    current.Character.VitalsRevision !=
                        hpCommit.AfterVitalsRevision)
                {
                    return false;
                }
            }
            if (observed == hpCommit.BeforeLifeRevision &&
                !_playerLifeRevisions.TryUpdate(
                    self.Session,
                    hpCommit.AfterLifeRevision,
                    hpCommit.BeforeLifeRevision))
            {
                return false;
            }

            return _medusaPeriodicDamageLedger.MarkLethalLifeAdvanced(
                    handle) is
                MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked or
                MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
        }
    }

    private bool TryFinalizeMedusaPeriodicLethalOwnerCleanup(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle)
    {
        if (!_medusaPeriodicDamageLedger.TryGetSnapshot(
                runtime.InstanceId,
                out var snapshot))
        {
            return false;
        }
        if (snapshot.LethalOwnerCleanupSettled)
        {
            return true;
        }
        if (!_medusaPeriodicDamageLedger.TryGetCurrentOwnerReceipt(
                handle,
                out var receipt) ||
            !InvokeWorldOwnerAuthoritativeMutation(
                runtime,
                map => map
                    .TryFinalizeMedusaPeriodicDamageLethalCleanup(receipt)))
        {
            return false;
        }

        return _medusaPeriodicDamageLedger
                .MarkLethalOwnerCleanupSettled(handle) is
            MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked or
            MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
    }

    private bool TryApplyMedusaPeriodicLethalRegistrySideEffects(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        in MedusaPeriodicDamageLedgerSnapshot original,
        in MedusaPeriodicDamageHpCommitEvidence hpCommit,
        DateTimeOffset now)
    {
        if (!_medusaPeriodicDamageLedger.TryGetSnapshot(
                runtime.InstanceId,
                out var snapshot))
        {
            return false;
        }
        if (snapshot.LethalRegistrySideEffectsSettled)
        {
            return true;
        }
        if (!TryGetPeriodicSelf(
                _medusaPeriodicDamageLedger,
                handle,
                original.RecipientCount,
                out var self))
        {
            return false;
        }

        lock (_gate)
        {
            if (!TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    self.Context,
                    hpCommit.AfterLifeRevision,
                    out var current) ||
                !MatchesPeriodicTargetCapture(current, original.Target) ||
                !_nextPlayerRecoveryAt.TryGetValue(
                    current.CharacterId,
                    out var recoveryDeadline))
            {
                return false;
            }
            lock (current.Character.VitalsSync)
            {
                if (current.Character.CurrentHp != 0 ||
                    current.Character.VitalsRevision !=
                        hpCommit.AfterVitalsRevision)
                {
                    return false;
                }
            }

            ApplyPeriodicLifeAdvanceSideEffectsWithoutOwnerLocked(
                current.Session,
                now + PlayerRecoveryInterval,
                recoveryDeadline);
            return _medusaPeriodicDamageLedger
                    .MarkLethalRegistrySideEffectsSettled(handle) is
                MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked or
                MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
        }
    }

    private void ApplyPeriodicLifeAdvanceSideEffectsWithoutOwnerLocked(
        ClientSession session,
        DateTimeOffset nextRecoveryAt,
        PlayerRecoveryDeadline recoveryDeadline)
    {
        ClearElementalCombatLifeState(session);
        ClearTrainingDummyHostileStatusesLocked(session);
        recoveryDeadline.Write(nextRecoveryAt);
        ResetPlayerRecoveryEcs(session);
        ResetPlayerVitalsDamageEcs(session);
    }
}
