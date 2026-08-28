using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
#if DEBUG
    private Action? _protocolCheckBeforeMedusaPeriodicOwnerConsume = null;
    private Action? _protocolCheckAfterMedusaPeriodicOwnerPrepare = null;
#endif

    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        private enum PeriodicOwnerReceiptState : byte
        {
            Prepared = 1,
            Superseded = 2,
            Consumed = 3,
            Unresolved = 4
        }

        private sealed class PreparedPeriodicDamageOwnerReceipt
            : MedusaPreparedPeriodicDamageOwnerReceipt
        {
            internal PreparedPeriodicDamageOwnerReceipt(
                MedusaInstanceOwnerBoundAggregate owner,
                MedusaEncounterMechanicsRuntime
                    .PeriodicDamageReservation reservation,
                ulong attackEventId,
                MedusaPeriodicDamageOwnerIntent requestedIntent)
            {
                Owner = owner;
                Reservation = reservation;
                Identity = reservation.Identity;
                AttackEventId = attackEventId;
                RequestedIntent = requestedIntent;
            }

            internal MedusaInstanceOwnerBoundAggregate Owner { get; }

            internal MedusaEncounterMechanicsRuntime
                .PeriodicDamageReservation Reservation { get; }

            internal PeriodicOwnerReceiptState State { get; set; } =
                PeriodicOwnerReceiptState.Prepared;

            internal override MedusaPeriodicDamageIdentity Identity
                { get; }

            internal override ulong AttackEventId { get; }

            internal override MedusaPeriodicDamageOwnerIntent
                RequestedIntent { get; }

            private MedusaPeriodicDamageDispositionOutcome?
                _actualDisposition;

            internal override MedusaPeriodicDamageDispositionOutcome?
                ActualDisposition => _actualDisposition;

            internal void RecordActualDisposition(
                MedusaPeriodicDamageDispositionOutcome outcome) =>
                _actualDisposition = outcome;

            internal bool LethalCleanupCompleted { get; set; }

            internal override bool MatchesReservation(
                MedusaEncounterMechanicsRuntime
                    .PeriodicDamageReservation reservation) =>
                ReferenceEquals(Reservation, reservation) &&
                Identity == reservation.Identity;
        }

        private PreparedPeriodicDamageOwnerReceipt?
            _preparedPeriodicDamageOwnerReceipt;

        public MedusaPeriodicDamageOwnerPrepareResult
            PreparePeriodicDamageOwnerReceipt(
                MedusaEncounterMechanicsRuntime
                    .PeriodicDamageReservation? reservation,
                ulong attackEventId,
                MedusaPeriodicDamageOwnerIntent requestedIntent,
                MedusaPeriodicDamageReceiptRefreshAuthority?
                    refreshAuthority)
        {
            if (reservation is null ||
                !_mechanics.IsPendingPeriodicDamage(reservation))
            {
                return RejectedPeriodicOwnerPrepare(
                    MedusaPeriodicDamageOwnerPrepareOutcome
                        .ForeignReservation);
            }
            if (attackEventId == 0 ||
                !reservation.Identity.IsValid ||
                requestedIntent is not (
                    MedusaPeriodicDamageOwnerIntent.Applied or
                    MedusaPeriodicDamageOwnerIntent.Terminal))
            {
                return RejectedPeriodicOwnerPrepare(
                    MedusaPeriodicDamageOwnerPrepareOutcome
                        .InvalidIdentity);
            }

            var existing = _preparedPeriodicDamageOwnerReceipt;
            if (existing is not null &&
                existing.State != PeriodicOwnerReceiptState.Superseded &&
                existing.MatchesReservation(reservation) &&
                existing.AttackEventId == attackEventId)
            {
                if (existing.RequestedIntent != requestedIntent)
                {
                    return RejectedPeriodicOwnerPrepare(
                        MedusaPeriodicDamageOwnerPrepareOutcome
                        .ConflictingPreparation);
                }
                refreshAuthority?.MarkInstalled(existing);
                return new(
                    MedusaPeriodicDamageOwnerPrepareOutcome
                        .AlreadyPrepared,
                    existing);
            }
            if (existing is not null &&
                attackEventId <= existing.AttackEventId)
            {
                return RejectedPeriodicOwnerPrepare(
                    MedusaPeriodicDamageOwnerPrepareOutcome
                        .NonMonotonicEvent);
            }

            var previousToSupersede = existing is
                    { State: PeriodicOwnerReceiptState.Prepared } &&
                existing.MatchesReservation(reservation)
                ? existing
                : null;
            if (previousToSupersede is not null)
            {
                if (attackEventId <= previousToSupersede.AttackEventId)
                {
                    return RejectedPeriodicOwnerPrepare(
                        MedusaPeriodicDamageOwnerPrepareOutcome
                            .NonMonotonicEvent);
                }
                if (refreshAuthority is null)
                {
                    return RejectedPeriodicOwnerPrepare(
                        MedusaPeriodicDamageOwnerPrepareOutcome
                            .RefreshAuthorityRequired);
                }
            }

            var dueAt = reservation.Identity.DueAt;
            if (!HasCoupledClockScalars() ||
                _run.OwnerState != MedusaRunState.Active ||
                _run.OwnerLastObservedAt > dueAt ||
                dueAt >= _run.Deadline)
            {
                return RejectedPeriodicOwnerPrepare(
                    MedusaPeriodicDamageOwnerPrepareOutcome
                        .InvariantFault);
            }

            // Allocate first. A pre-HP allocation failure must leave the
            // prior receipt authoritative and retryable.
            var receipt = new PreparedPeriodicDamageOwnerReceipt(
                this,
                reservation,
                attackEventId,
                requestedIntent);
            if (previousToSupersede is not null &&
                !refreshAuthority!.TryClaim(
                    reservation,
                    previousToSupersede,
                    attackEventId))
            {
                return RejectedPeriodicOwnerPrepare(
                    MedusaPeriodicDamageOwnerPrepareOutcome
                        .RefreshAuthorityRequired);
            }
            if (previousToSupersede is not null)
            {
                // The opaque ledger authority re-proved that its HP observer
                // has no post-HP marker. Only that proof permits replacing a
                // receipt after an ECS pre-HP rejection consumed the event.
                previousToSupersede.State =
                    PeriodicOwnerReceiptState.Superseded;
            }
            _preparedPeriodicDamageOwnerReceipt = receipt;
            refreshAuthority?.MarkInstalled(receipt);
            return new(
                MedusaPeriodicDamageOwnerPrepareOutcome.Prepared,
                receipt);
        }

        public MedusaPeriodicDamageOwnerReconcileResult
            ReconcilePeriodicDamageOwnerReceipt(
                MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
                MedusaPeriodicDamageOwnerAcknowledgementAuthority?
                    acknowledgementAuthority,
                Action? beforeOwnerConsume)
        {
            if (receipt is not PreparedPeriodicDamageOwnerReceipt exact ||
                !ReferenceEquals(exact.Owner, this) ||
                !TryClaimOwnerAcknowledgementNonThrowing(
                    acknowledgementAuthority,
                    exact))
            {
                return RejectedPeriodicOwnerReconcile(
                    MedusaPeriodicDamageDispositionOutcome
                        .ForeignReservation,
                    receipt);
            }

            if (exact.State == PeriodicOwnerReceiptState.Consumed &&
                exact.ActualDisposition is { } actual)
            {
                return new(
                    MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted,
                    exact,
                    actual);
            }
            if (exact.State == PeriodicOwnerReceiptState.Unresolved)
            {
                return new(
                    MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted,
                    exact,
                    ActualDisposition: null);
            }
            if (exact.State != PeriodicOwnerReceiptState.Prepared ||
                !ReferenceEquals(
                    _preparedPeriodicDamageOwnerReceipt,
                    exact) ||
                !exact.MatchesReservation(exact.Reservation))
            {
                return RejectedPeriodicOwnerReconcile(
                    MedusaPeriodicDamageDispositionOutcome
                        .ForeignReservation,
                    exact);
            }

            return ConsumePreparedPeriodicDamageOwnerReceipt(
                exact,
                beforeOwnerConsume);
        }

        public bool TryGetPreparedPeriodicDamageOwnerReceipt(
            MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
                reservation,
            out MedusaPreparedPeriodicDamageOwnerReceipt receipt)
        {
            if (reservation is not null &&
                _mechanics.IsPendingPeriodicDamage(reservation) &&
                _preparedPeriodicDamageOwnerReceipt is
                    { State: PeriodicOwnerReceiptState.Prepared } current &&
                current.MatchesReservation(reservation))
            {
                receipt = current;
                return true;
            }

            receipt = null!;
            return false;
        }

        public MedusaPeriodicDamageOwnerReconcileResult
            AbortPreparedPeriodicDamageOwnerReceipt(
                MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
                MedusaPeriodicDamagePreparedAbortAuthority? abortAuthority)
        {
            if (receipt is not PreparedPeriodicDamageOwnerReceipt exact ||
                !ReferenceEquals(exact.Owner, this) ||
                !TryClaimPreparedAbortNonThrowing(
                    abortAuthority,
                    exact))
            {
                return RejectedPeriodicOwnerReconcile(
                    MedusaPeriodicDamageDispositionOutcome
                        .ForeignReservation,
                    receipt);
            }
            if (exact.State == PeriodicOwnerReceiptState.Consumed &&
                exact.ActualDisposition is { } actual)
            {
                return new(
                    MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted,
                    exact,
                    actual);
            }
            if (exact.State == PeriodicOwnerReceiptState.Unresolved)
            {
                return new(
                    MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted,
                    exact,
                    ActualDisposition: null);
            }
            if (exact.State != PeriodicOwnerReceiptState.Prepared ||
                !ReferenceEquals(
                    _preparedPeriodicDamageOwnerReceipt,
                    exact) ||
                !_mechanics.IsPendingPeriodicDamage(exact.Reservation))
            {
                return RejectedPeriodicOwnerReconcile(
                    MedusaPeriodicDamageDispositionOutcome
                        .ForeignReservation,
                    receipt);
            }

            try
            {
                var outcome = _mechanics
                    .CompletePeriodicDamageInvariantFault(exact.Reservation);
                return RecordPeriodicOwnerDisposition(
                    exact,
                    outcome,
                    invariantConsumeStarted: true);
            }
            catch
            {
                return RecoverPeriodicOwnerDispositionNonThrowing(
                    exact,
                    invariantConsumeStarted: true);
            }
        }

        private static bool TryClaimOwnerAcknowledgementNonThrowing(
            MedusaPeriodicDamageOwnerAcknowledgementAuthority? authority,
            PreparedPeriodicDamageOwnerReceipt receipt)
        {
            try
            {
                return authority?.TryClaim(
                    receipt.Reservation,
                    receipt) == true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryClaimPreparedAbortNonThrowing(
            MedusaPeriodicDamagePreparedAbortAuthority? authority,
            PreparedPeriodicDamageOwnerReceipt receipt)
        {
            try
            {
                return authority?.TryClaim(
                    receipt.Reservation,
                    receipt) == true;
            }
            catch
            {
                return false;
            }
        }

        private static MedusaPeriodicDamageOwnerPrepareResult
            RejectedPeriodicOwnerPrepare(
                MedusaPeriodicDamageOwnerPrepareOutcome outcome) =>
            new(outcome, Receipt: null);

        private static MedusaPeriodicDamageOwnerReconcileResult
            RejectedPeriodicOwnerReconcile(
                MedusaPeriodicDamageDispositionOutcome outcome,
                MedusaPreparedPeriodicDamageOwnerReceipt? receipt) =>
            new(outcome, receipt, ActualDisposition: null);
    }

    internal bool TryPrepareMedusaPeriodicDamageOwnerReceipt(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        ulong attackEventId,
        MedusaPeriodicDamageOwnerIntent requestedIntent,
        out MedusaPeriodicDamageOwnerPrepareResult result) =>
        TryPrepareMedusaPeriodicDamageOwnerReceipt(
            reservation,
            attackEventId,
            requestedIntent,
            refreshAuthority: null,
            out result);

    internal bool TryPrepareMedusaPeriodicDamageOwnerReceipt(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        ulong attackEventId,
        MedusaPeriodicDamageOwnerIntent requestedIntent,
        MedusaPeriodicDamageReceiptRefreshAuthority? refreshAuthority,
        out MedusaPeriodicDamageOwnerPrepareResult result)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                result = owner.PreparePeriodicDamageOwnerReceipt(
                    reservation,
                    attackEventId,
                    requestedIntent,
                    refreshAuthority);
#if DEBUG
                if (result.IsPrepared)
                {
                    _protocolCheckAfterMedusaPeriodicOwnerPrepare?.Invoke();
                }
#endif
                return true;
            }

            result = new(
                MedusaPeriodicDamageOwnerPrepareOutcome
                    .ForeignReservation,
                Receipt: null);
            return false;
        }
    }

    internal bool TryGetPreparedMedusaPeriodicDamageOwnerReceipt(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        out MedusaPreparedPeriodicDamageOwnerReceipt receipt)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                return owner.TryGetPreparedPeriodicDamageOwnerReceipt(
                    reservation,
                    out receipt);
            }

            receipt = null!;
            return false;
        }
    }

    internal bool TryReconcileMedusaPeriodicDamageOwnerReceipt(
        MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
        MedusaPeriodicDamageOwnerAcknowledgementAuthority?
            acknowledgementAuthority,
        out MedusaPeriodicDamageOwnerReconcileResult result)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                Action? beforeOwnerConsume = null;
#if DEBUG
                beforeOwnerConsume =
                    _protocolCheckBeforeMedusaPeriodicOwnerConsume;
#endif
                result = owner.ReconcilePeriodicDamageOwnerReceipt(
                    receipt,
                    acknowledgementAuthority,
                    beforeOwnerConsume);
                return true;
            }

            result = new(
                MedusaPeriodicDamageDispositionOutcome.ForeignReservation,
                receipt,
                ActualDisposition: null);
            return false;
        }
    }

    internal bool TryAbortPreparedMedusaPeriodicDamageOwnerReceipt(
        MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
        MedusaPeriodicDamagePreparedAbortAuthority? abortAuthority,
        out MedusaPeriodicDamageOwnerReconcileResult result)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                result = owner.AbortPreparedPeriodicDamageOwnerReceipt(
                    receipt,
                    abortAuthority);
                return true;
            }

            result = new(
                MedusaPeriodicDamageDispositionOutcome.ForeignReservation,
                receipt,
                ActualDisposition: null);
            return false;
        }
    }
}
