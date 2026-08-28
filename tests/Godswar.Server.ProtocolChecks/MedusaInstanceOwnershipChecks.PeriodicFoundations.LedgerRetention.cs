using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckPeriodicLedgerCapacityAndRetention()
    {
        var first = PrepareSimplePeriodicFoundation(attackEventId: 1_301);
        var second = PrepareSimplePeriodicFoundation(attackEventId: 1_302);
        var ledger = new MedusaPeriodicDamageLedger(1);
        var firstHandle = PrepareLedgerEntry(ledger, first);
        Check.True(
            ledger.TryPrepare(
                second.Reservation,
                second.Target,
                second.AttackEventId,
                second.Receipt,
                second.Recipients,
                out var overflow) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .CapacityExhausted &&
            overflow is null,
            "the bounded ledger admits at most its world-runtime capacity");

        CompletePeriodicFoundationOwnerAck(ledger, first, firstHandle);
        Check.True(
            ledger.MarkPublished(firstHandle) ==
                MedusaPeriodicDamageLedgerMutationOutcome.Published &&
            ledger.RemovePublished(
                firstHandle,
                settlementAuthority: null) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase &&
            ledger.TryGetSnapshot(
                first.Reservation.Identity.WorldInstanceId,
                out var retainedPublished) &&
            retainedPublished.Phase ==
                MedusaPeriodicDamageLedgerPhase.Published &&
            ledger.TryPrepare(
                second.Reservation,
                second.Target,
                second.AttackEventId,
                second.Receipt,
                second.Recipients,
                out overflow) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .CapacityExhausted,
            "Published work remains capacity-owning until opaque persistence settlement is supplied");

        var firstRetirement = CreatePeriodicFoundationRetirePermit(
            first.Reservation.Identity.WorldInstanceId);
        Check.True(
            ledger.RetireWorld(
                firstRetirement,
                first.Receipt,
                out var publishedAbort) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .ReconciliationRequired &&
            publishedAbort is null &&
            ledger.TryGetSnapshot(
                first.Reservation.Identity.WorldInstanceId,
                out retainedPublished) &&
            retainedPublished.Phase ==
                MedusaPeriodicDamageLedgerPhase.Published,
            "typed runtime retirement cannot evict published persistence-pending work");

        Check.True(
            ledger.TryClaimPersistenceAttempt(firstHandle) &&
            !ledger.TryGetPersistenceSettlementAuthority(
                firstHandle,
                out _) &&
            ledger.ReleasePersistenceAttempt(firstHandle) ==
                MedusaPeriodicDamageLedgerMutationOutcome.Published &&
            ledger.TryClaimPersistenceAttempt(firstHandle) &&
            ledger.MarkPersistenceAttemptSettled(firstHandle) ==
                MedusaPeriodicDamageLedgerMutationOutcome.Published &&
            ledger.TryGetPersistenceSettlementAuthority(
                firstHandle,
                out var persistenceAuthority) &&
            ledger.RemovePublished(
                firstHandle,
                persistenceAuthority) ==
                MedusaPeriodicDamageLedgerMutationOutcome.Removed,
            "failed persistence remains retryable and only a successful attempt permits removal");

        var retiring = PrepareSimplePeriodicFoundation(attackEventId: 1_401);
        var retirementLedger = new MedusaPeriodicDamageLedger(1);
        var retiringHandle = PrepareLedgerEntry(retirementLedger, retiring);
        var retirement = CreatePeriodicFoundationRetirePermit(
            retiring.Reservation.Identity.WorldInstanceId);
        Check.True(
            retirementLedger.RetireWorld(
                retirement,
                retiring.Receipt,
                out var retirementAbort) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.Prepared &&
            retirementAbort?.Reason ==
                MedusaPeriodicDamagePreparedAbortReason.RuntimeRetirement &&
            !retirementLedger.TryGetPreparedAttempt(
                retiringHandle,
                out _,
                out _,
                out _) &&
            retiring.Map.TryAbortPreparedMedusaPeriodicDamageOwnerReceipt(
                retiring.Receipt,
                retirementAbort,
                out var retired) &&
            retired.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.InvariantFault &&
            retirementLedger.MarkPreparedOwnerAborted(
                retiringHandle,
                retirementAbort,
                retired) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .OwnerInvariantFault,
            "a typed durable runtime-retirement permit alone mints pre-HP abort authority and fences HP reacquisition");
        Check.True(
            retirementLedger.RetireWorld(
                retirement,
                retiring.Receipt,
                out var settledAbort) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .InvariantSettlementRequired &&
            settledAbort is null,
            "runtime retirement retains owner-invariant work until its separate settlement proof arrives");
    }

    private static MedusaRuntimeRetirePermit
        CreatePeriodicFoundationRetirePermit(
            Godswar.Server.Domain.World.Instances.WorldInstanceId
                worldInstanceId)
    {
        var reservedAt = MedusaDurableAdmissionFoundationChecks.Utc(0);
        var party = MedusaDurableAdmissionFoundationChecks.Lease(
            [
                MedusaDurableAdmissionFoundationChecks.Member(
                    accountId: 10,
                    characterId: 100,
                    generation: 1)
            ],
            reservedAt);
        var request = MedusaDurableAdmissionFoundationChecks.Request(
            party,
            reservedAt,
            MedusaEncounterDifficulty.Enhanced,
            worldInstanceId: worldInstanceId);
        var terminal = new MedusaAdmissionSnapshot(
            request.AdmissionId,
            request.WorldInstanceId,
            request.RealmDay,
            request.Difficulty,
            request.ContentMapId,
            request.Source,
            request.Party,
            request.EncounterContentFingerprint,
            request.RosterHash,
            request.RequestHash,
            MedusaAdmissionState.Completed,
            revision: 5,
            new MedusaRosterTransferBarrierEvidence(
                Guid.Parse("90000000-0000-0000-0000-000000000001"),
                new string(
                    'B',
                    MedusaDurableAdmissionPolicy.Sha256HexLength)),
            reservedAt,
            reservedAt.AddMinutes(1),
            reservedAt.AddMinutes(2),
            reservedAt.AddMinutes(3),
            reservedAt.AddMinutes(4),
            releasedAtUtc: null);
        Check.True(
            MedusaRuntimeRetirePermit.TryCreate(
                terminal,
                out var retirement),
            "terminal durable admission mints typed runtime-retirement proof");
        return retirement;
    }
}
