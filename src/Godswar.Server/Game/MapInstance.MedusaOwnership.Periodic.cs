using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        internal MedusaPeriodicDamageDispositionOutcome
            CompletePeriodicDamageForProtocolCheck(
                MedusaEncounterMechanicsRuntime
                    .PeriodicDamageReservation? reservation,
                bool terminal)
        {
            if (!_mechanics.IsPendingPeriodicDamage(reservation))
            {
                return terminal
                    ? _mechanics.CompletePeriodicDamageTerminal(reservation)
                    : _mechanics.CompletePeriodicDamageApplied(reservation);
            }

            var dueAt = reservation!.Identity.DueAt;
            if (!HasCoupledClockScalars() ||
                _run.OwnerState != MedusaRunState.Active ||
                _run.OwnerLastObservedAt > dueAt ||
                dueAt >= _run.Deadline)
            {
                return _mechanics
                    .CompletePeriodicDamageInvariantFault(reservation);
            }

            var runClock = _run.ObserveTime(dueAt);
            if (runClock != MedusaRunClockOutcome.Active)
            {
                return _mechanics
                    .CompletePeriodicDamageInvariantFault(reservation);
            }

            var outcome = terminal
                ? _mechanics.CompletePeriodicDamageTerminal(reservation)
                : _mechanics.CompletePeriodicDamageApplied(reservation);
            return outcome;
        }

        private bool HasCoupledClockScalars() =>
            _run.WorldInstanceId == _mechanics.WorldInstanceId &&
            _run.Difficulty == _mechanics.Difficulty &&
            _run.ContentMapId == _mechanics.ContentMapId &&
            _run.StartedAt == _mechanics.StartedAt &&
            _run.OwnerLastObservedAt ==
                _mechanics.OwnerLastObservedAt;

        private MedusaMechanicsClockResult AdvanceMechanics(
            DateTimeOffset authoritativeAt)
        {
            var result = _mechanics.ObserveTime(authoritativeAt);
            if (result.Outcome is
                MedusaMechanicsClockOutcome.PeriodicDamageRequired or
                MedusaMechanicsClockOutcome.TimestampMovedBackward)
            {
                throw new InvalidOperationException(
                    "Coupled Medusa mechanics time rejected an accepted " +
                    "run-clock observation.");
            }

            return result;
        }

        private void EnsureCoupledClocks(
            out MedusaRunSnapshot run,
            out MedusaEncounterMechanicsSnapshot mechanics)
        {
            run = _run.Snapshot();
            mechanics = _mechanics.Snapshot();
            if (run.WorldInstanceId != mechanics.WorldInstanceId ||
                run.Difficulty != mechanics.Difficulty ||
                run.ContentMapId != mechanics.ContentMapId ||
                run.StartedAt != mechanics.StartedAt ||
                run.LastObservedAt != mechanics.LastObservedAt)
            {
                throw new InvalidOperationException(
                    "The owner-bound Medusa run and mechanics clocks " +
                    "must never diverge.");
            }
        }
    }
}
