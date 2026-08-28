using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private Task PublishMedusaPeriodicDamageAsync(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        in MedusaPeriodicDamageLedgerSnapshot snapshot,
        in MedusaPeriodicDamageHpCommitEvidence hpCommit)
    {
        if (!TryGetPeriodicSelf(
                _medusaPeriodicDamageLedger,
                handle,
                snapshot.RecipientCount,
                out var self))
        {
            SettleRemainingPeriodicRecipientsWithoutAdmission(handle);
            return Task.CompletedTask;
        }

        int currentMana;
        lock (self.Context.Character.VitalsSync)
        {
            currentMana = self.Context.Character.CurrentMp;
        }
        var lethal = hpCommit.AfterHealth == 0;
        var targetLifeRevision = hpCommit.AfterLifeRevision;
        while (_medusaPeriodicDamageLedger.TryGetNextRecipientSettlement(
                   handle,
                   out _,
                   out var recipient,
                   out var settlement))
        {
            try
            {
                var targetObjectId = recipient.Variant ==
                    MedusaPeriodicDamageRecipientVariant.Self
                        ? LocalPlayerObjectId
                        : snapshot.Target.Authority.ObjectId;
                IReadOnlyList<ReadOnlyMemory<byte>> packets = lethal
                    ?
                    [
                        PacketBuilder.PlayerVitalsUpdate(
                            targetObjectId,
                            hpCommit.AfterHealth,
                            currentMana),
                        PacketBuilder.PlayerDeath(
                            targetObjectId,
                            self.Context.Character.PositionX,
                            0f,
                            self.Context.Character.PositionZ,
                            self.Context.Character.CurrentMap)
                    ]
                    :
                    [
                        PacketBuilder.PlayerVitalsUpdate(
                            targetObjectId,
                            hpCommit.AfterHealth,
                            currentMana)
                    ];
                SettleMedusaPeriodicRecipientExact(
                    runtime,
                    recipient,
                    recipient.Variant ==
                        MedusaPeriodicDamageRecipientVariant.Self
                            ? targetLifeRevision
                            : recipient.LifeRevision,
                    self.Context,
                    targetLifeRevision,
                    hpCommit.AfterVitalsRevision,
                    lethal,
                    packets,
                    settlement);
            }
            catch
            {
                lock (_gate)
                {
                    settlement.MarkSettled(admissionOwned: false);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void SettleMedusaPeriodicRecipientExact(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageRecipientIdentity recipientIdentity,
        long expectedRecipientLifeRevision,
        GameSessionContext targetIdentity,
        long expectedTargetLifeRevision,
        long expectedTargetVitalsRevision,
        bool requireTargetDead,
        IReadOnlyList<ReadOnlyMemory<byte>> packets,
        MedusaPeriodicDamageRecipientSettlementObserver settlement)
    {
        Task completion = Task.CompletedTask;
        ClientSession? claimedDisconnect = null;
        var owned = false;
        lock (_gate)
        {
            if (!TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    recipientIdentity.Context,
                    expectedRecipientLifeRevision,
                    out var recipient) ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    targetIdentity,
                    expectedTargetLifeRevision,
                    out var target) ||
                !MatchesMonsterAttackTargetVitalsFence(
                    target,
                    expectedTargetVitalsRevision,
                    requireTargetDead))
            {
                settlement.MarkSettled(admissionOwned: false);
                return;
            }

            try
            {
                var outcome = recipient.Session.TryAdmitExactBatchOutcome(
                    packets,
                    out completion);
                owned = outcome is
                    ExactEgressAdmissionOutcome.Admitted or
                    ExactEgressAdmissionOutcome.AdmittedTerminal;
                settlement.MarkSettled(owned);
                if (outcome != ExactEgressAdmissionOutcome.Admitted &&
                    recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
            }
            catch
            {
                settlement.MarkSettled(admissionOwned: false);
                if (recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
            }
        }

        if (claimedDisconnect is not null)
        {
            CompleteClaimedExactStatusDisconnect(claimedDisconnect);
        }
        if (owned)
        {
            ObserveExactAdmissionCompletion(
                recipientIdentity.Session,
                completion,
                "MedusaPeriodicVitals");
        }
    }

    private void SettleRemainingPeriodicRecipientsWithoutAdmission(
        MedusaPeriodicDamageLedgerHandle handle)
    {
        while (_medusaPeriodicDamageLedger.TryGetNextRecipientSettlement(
                   handle,
                   out _,
                   out _,
                   out var settlement))
        {
            lock (_gate)
            {
                settlement.MarkSettled(admissionOwned: false);
            }
        }
    }
}
