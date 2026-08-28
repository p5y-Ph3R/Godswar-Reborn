using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        private MedusaPeriodicDamageOwnerReconcileResult
            ConsumePreparedPeriodicDamageOwnerReceipt(
                PreparedPeriodicDamageOwnerReceipt exact,
                Action? beforeOwnerConsume)
        {
            var invariantConsumeStarted = false;
            try
            {
                var dueAt = exact.Identity.DueAt;
                MedusaPeriodicDamageDispositionOutcome outcome;
                if (!HasCoupledClockScalars() ||
                    _run.OwnerState != MedusaRunState.Active ||
                    _run.OwnerLastObservedAt > dueAt ||
                    dueAt >= _run.Deadline)
                {
                    invariantConsumeStarted = true;
                    outcome = _mechanics
                        .CompletePeriodicDamageInvariantFault(
                            exact.Reservation);
                }
                else
                {
                    var runClock = _run.ObserveTime(dueAt);
                    if (runClock != MedusaRunClockOutcome.Active)
                    {
                        invariantConsumeStarted = true;
                        outcome = _mechanics
                            .CompletePeriodicDamageInvariantFault(
                                exact.Reservation);
                    }
                    else
                    {
                        // DEBUG may throw after the run cursor moved but
                        // before mechanics consumes the reservation.
                        beforeOwnerConsume?.Invoke();
                        outcome = exact.RequestedIntent ==
                            MedusaPeriodicDamageOwnerIntent.Terminal
                            ? _mechanics
                                .CompletePeriodicDamageTerminal(
                                    exact.Reservation)
                            : _mechanics
                                .CompletePeriodicDamageApplied(
                                    exact.Reservation);
                    }
                }

                return RecordPeriodicOwnerDisposition(
                    exact,
                    outcome,
                    invariantConsumeStarted);
            }
            catch
            {
                return RecoverPeriodicOwnerDispositionNonThrowing(
                    exact,
                    invariantConsumeStarted);
            }
        }

        private MedusaPeriodicDamageOwnerReconcileResult
            RecoverPeriodicOwnerDispositionNonThrowing(
                PreparedPeriodicDamageOwnerReceipt exact,
                bool invariantConsumeStarted,
                bool terminalWithoutHpStarted = false)
        {
            MedusaPeriodicDamageDispositionOutcome? actual =
                DispositionFromReservationState(
                    exact,
                    invariantConsumeStarted,
                    terminalWithoutHpStarted);
            if (actual is null)
            {
                try
                {
                    var recovered = _mechanics
                        .CompletePeriodicDamageInvariantFault(
                            exact.Reservation);
                    if (recovered ==
                        MedusaPeriodicDamageDispositionOutcome.InvariantFault)
                    {
                        actual = recovered;
                    }
                }
                catch
                {
                    actual = DispositionFromReservationState(
                        exact,
                        invariantConsume: true,
                        terminalWithoutHp: terminalWithoutHpStarted);
                }
            }

            if (!TryRecouplePeriodicOwnerClocksNonThrowing())
            {
                actual = null;
            }
            return actual is { } consumed
                ? RecordConsumedPeriodicOwnerDisposition(exact, consumed)
                : RecordUnresolvedPeriodicOwnerDisposition(exact);
        }

        private MedusaPeriodicDamageOwnerReconcileResult
            RecordPeriodicOwnerDisposition(
                PreparedPeriodicDamageOwnerReceipt exact,
                MedusaPeriodicDamageDispositionOutcome outcome,
                bool invariantConsumeStarted = false,
                bool terminalWithoutHpStarted = false)
        {
            if (!TryRecouplePeriodicOwnerClocksNonThrowing())
            {
                return RecordUnresolvedPeriodicOwnerDisposition(exact);
            }
            if (outcome is MedusaPeriodicDamageDispositionOutcome.Applied or
                MedusaPeriodicDamageDispositionOutcome.Terminal or
                MedusaPeriodicDamageDispositionOutcome.InvariantFault)
            {
                return RecordConsumedPeriodicOwnerDisposition(exact, outcome);
            }
            if (outcome ==
                MedusaPeriodicDamageDispositionOutcome.AlreadyCompleted)
            {
                // The legacy mechanics endpoint intentionally collapses exact
                // replay to AlreadyCompleted. Recover the actual consumed
                // disposition from the same opaque reservation before
                // deciding that ownership is ambiguous.
                var actual = DispositionFromReservationState(
                    exact,
                    invariantConsumeStarted,
                    terminalWithoutHpStarted);
                return actual is
                    MedusaPeriodicDamageDispositionOutcome.Applied or
                    MedusaPeriodicDamageDispositionOutcome.Terminal or
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault
                    ? RecordConsumedPeriodicOwnerDisposition(exact, actual.Value)
                    : RecordUnresolvedPeriodicOwnerDisposition(exact, outcome);
            }

            return RejectedPeriodicOwnerReconcile(outcome, exact);
        }

        private bool TryRecouplePeriodicOwnerClocksNonThrowing()
        {
            try
            {
                if (_run.OwnerLastObservedAt !=
                    _mechanics.OwnerLastObservedAt)
                {
                    _run.RestoreMonsterHitClockSnapshot(
                        new(_mechanics.OwnerLastObservedAt));
                }
                return _run.OwnerLastObservedAt ==
                    _mechanics.OwnerLastObservedAt;
            }
            catch
            {
                return false;
            }
        }

        private static MedusaPeriodicDamageDispositionOutcome?
            DispositionFromReservationState(
                PreparedPeriodicDamageOwnerReceipt exact,
                bool invariantConsume = false,
                bool terminalWithoutHp = false) =>
            exact.Reservation.State switch
            {
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Applied =>
                    MedusaPeriodicDamageDispositionOutcome.Applied,
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Terminal when terminalWithoutHp =>
                    MedusaPeriodicDamageDispositionOutcome.Terminal,
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Terminal when invariantConsume ||
                        exact.RequestedIntent ==
                            MedusaPeriodicDamageOwnerIntent.Applied =>
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault,
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Terminal =>
                    MedusaPeriodicDamageDispositionOutcome.Terminal,
                MedusaEncounterMechanicsRuntime.PeriodicReservationState
                    .Canceled =>
                    MedusaPeriodicDamageDispositionOutcome.Canceled,
                _ => null
            };

        private static MedusaPeriodicDamageOwnerReconcileResult
            RecordConsumedPeriodicOwnerDisposition(
                PreparedPeriodicDamageOwnerReceipt exact,
                MedusaPeriodicDamageDispositionOutcome outcome)
        {
            exact.RecordActualDisposition(outcome);
            exact.State = PeriodicOwnerReceiptState.Consumed;
            return new(outcome, exact, outcome);
        }

        private static MedusaPeriodicDamageOwnerReconcileResult
            RecordUnresolvedPeriodicOwnerDisposition(
                PreparedPeriodicDamageOwnerReceipt exact,
                MedusaPeriodicDamageDispositionOutcome outcome =
                    MedusaPeriodicDamageDispositionOutcome.InvariantFault)
        {
            exact.State = PeriodicOwnerReceiptState.Unresolved;
            return new(outcome, exact, ActualDisposition: null);
        }
    }
}
