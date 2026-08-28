using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckPeriodicLedgerRefreshAndAbort()
    {
        var initial = PrepareSimplePeriodicFoundation(attackEventId: 801);
        var ledger = new MedusaPeriodicDamageLedger(1);
        var handle = PrepareLedgerEntry(ledger, initial);
        var target = initial.Target;
        var eventId = initial.AttackEventId;
        var receipt = initial.Receipt;

        MedusaPeriodicDamageOwnerReconcileResult aborted = default;
        MedusaPeriodicDamageOwnerReconcileResult abortReplay = default;
        Check.True(
            ledger.TryCreatePreparedAbortAuthority(
                handle,
                receipt,
                out var prematureAbort) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.WrongPhase &&
            prematureAbort is null &&
            ledger.TryCreateReceiptRefreshAuthority(
                handle,
                target,
                eventId,
                initial.Recipients,
                out var nonmonotonic) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.Invalid &&
            nonmonotonic is null,
            "prepared work cannot mint abort authority and refresh requires a strictly higher event");

        target = RefreshedPeriodicTarget(target);
        eventId++;
        Check.True(
            ledger.TryCreateReceiptRefreshAuthority(
                handle,
                target,
                eventId,
                initial.Recipients,
                out var refresh) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.Prepared &&
            refresh is not null &&
            ledger.TryCreateReceiptRefreshAuthority(
                handle,
                target,
                eventId,
                initial.Recipients,
                out var refreshReplay) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .AlreadyPresent &&
            ReferenceEquals(refresh, refreshReplay) &&
            ledger.TryCreateReceiptRefreshAuthority(
                handle,
                target,
                eventId + 1,
                initial.Recipients,
                out var conflictingRefresh) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.Invalid &&
            conflictingRefresh is null,
            "one exact pending refresh authority replays while a conflicting event is rejected");
#if DEBUG
        ledger.ProtocolCheckBeforeRefreshInstallTransition = () =>
            throw new InvalidOperationException(
                "simulated lost refresh-install result");
#endif
        receipt = PreparePeriodicFoundationOwnerReceipt(
            initial.Map,
            initial.Reservation,
            eventId,
            MedusaPeriodicDamageOwnerIntent.Applied,
            refresh);
#if DEBUG
        ledger.ProtocolCheckBeforeRefreshInstallTransition = null;
#endif
        Check.True(
            ledger.TryGetRetained(
                initial.Reservation.Identity.WorldInstanceId,
                out var retained,
                out var recovered) &&
            ReferenceEquals(retained, handle) &&
            recovered.Phase == MedusaPeriodicDamageLedgerPhase.Prepared &&
            recovered.PreparationAttempt == 2 &&
            recovered.AttackEventId == eventId &&
            recovered.Target == target &&
            ledger.TryGetPreparedAttempt(
                handle,
                out var recoveredTarget,
                out var recoveredEvent,
                out _) &&
            recoveredTarget == target &&
            recoveredEvent == eventId,
            "a sticky refresh-install marker recovers the higher event and exact target after its callback result is lost");

        while (eventId < 808)
        {
            target = RefreshedPeriodicTarget(target);
            eventId++;
            Check.True(
                ledger.TryCreateReceiptRefreshAuthority(
                    handle,
                    target,
                    eventId,
                    initial.Recipients,
                    out refresh) ==
                        MedusaPeriodicDamageLedgerMutationOutcome.Prepared &&
                refresh is not null,
                "each bounded refresh attempt receives exact ledger authority");
            receipt = PreparePeriodicFoundationOwnerReceipt(
                initial.Map,
                initial.Reservation,
                eventId,
                MedusaPeriodicDamageOwnerIntent.Applied,
                refresh);
            Check.True(
                ledger.TryGetSnapshot(
                    initial.Reservation.Identity.WorldInstanceId,
                    out var refreshed) &&
                refreshed.Phase ==
                    MedusaPeriodicDamageLedgerPhase.Prepared &&
                refreshed.AttackEventId == eventId,
                "each installed refresh is monotonically retained");
        }

        var exhaustedTarget = RefreshedPeriodicTarget(target);
        Check.True(
            ledger.TryCreateReceiptRefreshAuthority(
                handle,
                exhaustedTarget,
                eventId + 1,
                initial.Recipients,
                out var exhaustedRefresh) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .AttemptsExhausted &&
            exhaustedRefresh is null &&
            ledger.TryGetSnapshot(
                initial.Reservation.Identity.WorldInstanceId,
                out var exhausted) &&
            exhausted.Phase ==
                MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault &&
            exhausted.PreparationAttempt ==
                MedusaPeriodicDamageLedger.MaximumPreparationAttempts &&
            !ledger.TryGetPreparedAttempt(
                handle,
                out _,
                out _,
                out _),
            "refresh exhaustion fences HP capability in a remediable pre-HP invariant state");

        var abortCreation = ledger.TryCreatePreparedAbortAuthority(
            handle,
            receipt,
            out var abortAuthority);
        Check.True(
            abortCreation ==
                MedusaPeriodicDamageLedgerMutationOutcome.Prepared &&
            abortAuthority?.Reason ==
                MedusaPeriodicDamagePreparedAbortReason.AttemptsExhausted,
            "only the exhausted pre-HP state mints abort authority");
        var routedAbort =
            initial.Map.TryAbortPreparedMedusaPeriodicDamageOwnerReceipt(
                receipt,
                abortAuthority,
                out aborted);
        Check.True(
            routedAbort &&
            aborted.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.InvariantFault &&
            initial.Reservation.State ==
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Terminal,
            "the claimed abort authority consumes the owner reservation");
        var routedReplay =
            initial.Map.TryAbortPreparedMedusaPeriodicDamageOwnerReceipt(
                receipt,
                abortAuthority,
                out abortReplay);
        Check.True(
            routedReplay &&
            abortReplay.Outcome ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted &&
            abortReplay.ActualDisposition ==
                MedusaPeriodicDamageDispositionOutcome.InvariantFault,
            "a lost abort result replays the exact owner-written disposition");
        Check.True(
            ledger.MarkPreparedOwnerAborted(
                handle,
                abortAuthority,
                aborted) ==
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .OwnerInvariantFault &&
            ledger.MarkPreparedOwnerAborted(
                handle,
                abortAuthority,
                abortReplay) ==
                    MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent,
            "the exact claimed abort authority records one owner invariant disposition");
    }

    private static MedusaPeriodicDamageTargetCapture RefreshedPeriodicTarget(
        in MedusaPeriodicDamageTargetCapture target) =>
        new(
            target.Authority with
            {
                VitalsRevision = target.Authority.VitalsRevision + 1
            },
            target.CurrentHealth - 1);
}
