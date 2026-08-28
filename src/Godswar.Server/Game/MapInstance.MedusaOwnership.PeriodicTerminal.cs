using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        public MedusaPeriodicDamageOwnerReconcileResult
            CompletePeriodicDamageTerminalWithoutHp(
                MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
                MedusaPeriodicDamageTerminalWithoutHpAuthority? authority,
                Action? beforeOwnerConsume)
        {
            if (receipt is not PreparedPeriodicDamageOwnerReceipt exact ||
                !ReferenceEquals(exact.Owner, this))
            {
                return RejectedPeriodicOwnerReconcile(
                    MedusaPeriodicDamageDispositionOutcome.ForeignReservation,
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
                !exact.MatchesReservation(exact.Reservation) ||
                authority is null ||
                !authority.TryClaim(exact.Reservation, exact))
            {
                return RejectedPeriodicOwnerReconcile(
                    MedusaPeriodicDamageDispositionOutcome.ForeignReservation,
                    exact);
            }

            var invariantConsumeStarted = false;
            var terminalConsumeStarted = false;
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
                        beforeOwnerConsume?.Invoke();
                        terminalConsumeStarted = true;
                        outcome = _mechanics.CompletePeriodicDamageTerminal(
                            exact.Reservation);
                    }
                }

                return RecordPeriodicOwnerDisposition(
                    exact,
                    outcome,
                    invariantConsumeStarted,
                    terminalConsumeStarted);
            }
            catch
            {
                return RecoverPeriodicOwnerDispositionNonThrowing(
                    exact,
                    invariantConsumeStarted,
                    terminalConsumeStarted);
            }
        }
    }

    internal bool TryCompleteMedusaPeriodicDamageTerminalWithoutHp(
        MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
        MedusaPeriodicDamageTerminalWithoutHpAuthority? authority,
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
                result = owner.CompletePeriodicDamageTerminalWithoutHp(
                    receipt,
                    authority,
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
}
