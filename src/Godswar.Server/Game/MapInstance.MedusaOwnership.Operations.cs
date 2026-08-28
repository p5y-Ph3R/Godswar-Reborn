using System.Collections.Immutable;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        private const long PureCompatibilityWorldMembershipEpoch = 1;

        public MedusaOwnedDefeatResult ClaimDefeat(
            int defeatedByCharacterId,
            uint objectId,
            uint spawnGeneration,
            DateTimeOffset occurredAt)
        {
            EnsureCoupledClocks(out _, out _);
            var authoritativeAt = occurredAt.ToUniversalTime();
            var claimPreview = _run.PreviewDefeatClaim(
                defeatedByCharacterId,
                objectId,
                spawnGeneration,
                authoritativeAt);
            var retirePreview = _mechanics.PreviewRetireMonster(
                objectId,
                spawnGeneration,
                authoritativeAt);
            if (claimPreview is not (
                    MedusaDefeatClaimPreviewOutcome.Eligible or
                    MedusaDefeatClaimPreviewOutcome
                        .DeadlineBoundaryUnresolved or
                    MedusaDefeatClaimPreviewOutcome.TimedOut))
            {
                var rejected = ClaimResultForPreview(claimPreview);
                return new(
                    GateForDefeatClaim(rejected.Outcome),
                    rejected,
                    SourceRetirement: null,
                    MechanicsClockResult: null);
            }
            if (retirePreview is not (
                    MedusaMechanicSourceRetireOutcome.Retired or
                    MedusaMechanicSourceRetireOutcome
                        .PeriodicDamageRequired or
                    MedusaMechanicSourceRetireOutcome
                        .DeadlineBoundaryUnresolved))
            {
                return new(
                    MedusaOwnedOperationGateOutcome.RunNotActive,
                    Claim: null,
                    new(retirePreview, PeriodicDamage: null),
                    MechanicsClockResult: null);
            }

            var periodic = _mechanics.ReservePeriodicDamage(
                authoritativeAt);
            if (periodic.Outcome ==
                    MedusaPeriodicDamageReserveOutcome.Reserved)
            {
                return new(
                    MedusaOwnedOperationGateOutcome
                        .PeriodicDamageRequired,
                    Claim: null,
                    SourceRetirement: null,
                    new(
                        MedusaMechanicsClockOutcome
                            .PeriodicDamageRequired,
                        periodic.Reservation))
                {
                    PeriodicDamage = periodic.Reservation
                };
            }

            if (claimPreview != MedusaDefeatClaimPreviewOutcome.Eligible)
            {
                var clock = ObserveTime(authoritativeAt);
                var expectedGate = claimPreview ==
                    MedusaDefeatClaimPreviewOutcome.TimedOut
                        ? MedusaOwnedOperationGateOutcome.TimedOut
                        : MedusaOwnedOperationGateOutcome
                            .DeadlineBoundaryUnresolved;
                var claim = clock.GateOutcome == expectedGate
                    ? ClaimResultForPreview(claimPreview)
                    : new MedusaDefeatClaimResult(
                        MedusaDefeatClaimOutcome.InvariantFault,
                        ScoreAwarded: 0,
                        TeamScore: _run.OwnerTeamScore);
                return new(
                    clock.GateOutcome == expectedGate
                        ? expectedGate
                        : MedusaOwnedOperationGateOutcome.InvariantFault,
                    claim,
                    SourceRetirement: null,
                    clock.MechanicsResult);
            }

            if (!TryPrepareDefeat(
                    defeatedByCharacterId,
                    objectId,
                    spawnGeneration,
                    authoritativeAt,
                    out var prepared,
                    out var rejection))
            {
                return rejection;
            }

            var clockReservation = PreparePlayerDamageClock(
                authoritativeAt);
            var completed = CompletePreparedDefeat(prepared);
            if (completed.GateOutcome ==
                MedusaOwnedOperationGateOutcome.InvariantFault)
            {
                RollBackPlayerDamageClock(clockReservation);
                return completed;
            }

            CommitPlayerDamageClock(clockReservation);
            return completed;
        }

        public MedusaOwnedClockResult ObserveTime(
            DateTimeOffset observedAt)
        {
            EnsureCoupledClocks(out var runBefore, out _);
            if (runBefore.State != MedusaRunState.Active)
            {
                return new(
                    GateForRunState(runBefore),
                    MedusaRunClockOutcome.RunNotActive,
                    MechanicsResult: null);
            }

            var authoritativeAt = observedAt.ToUniversalTime();
            if (authoritativeAt < runBefore.LastObservedAt)
            {
                return new(
                    MedusaOwnedOperationGateOutcome.TimestampMovedBackward,
                    MedusaRunClockOutcome.TimestampMovedBackward,
                    MechanicsResult: null);
            }
            var periodic = _mechanics.ReservePeriodicDamage(
                authoritativeAt);
            if (periodic.Outcome ==
                    MedusaPeriodicDamageReserveOutcome.Reserved)
            {
                return new(
                    MedusaOwnedOperationGateOutcome
                        .PeriodicDamageRequired,
                    RunOutcome: null,
                    new(
                        MedusaMechanicsClockOutcome
                            .PeriodicDamageRequired,
                        periodic.Reservation));
            }
            var runClock = _run.ObserveTime(authoritativeAt);
            var mechanicsClock = _mechanics.ObserveTime(authoritativeAt);
            var gate = MechanicsClockMatchesRunClock(
                    runClock,
                    mechanicsClock) &&
                HasCoupledClockScalars()
                    ? GateForRunClock(runClock)
                    : MedusaOwnedOperationGateOutcome.InvariantFault;
            return new(
                gate,
                runClock,
                mechanicsClock);
        }

        private static bool MechanicsClockMatchesRunClock(
            MedusaRunClockOutcome run,
            in MedusaMechanicsClockResult mechanics) => run switch
            {
                MedusaRunClockOutcome.Active or
                MedusaRunClockOutcome.TimedOut =>
                    mechanics.Outcome ==
                        MedusaMechanicsClockOutcome.Advanced &&
                    mechanics.PeriodicDamage is null,
                MedusaRunClockOutcome.DeadlineBoundaryUnresolved =>
                    mechanics.Outcome ==
                        MedusaMechanicsClockOutcome
                            .DeadlineBoundaryUnresolved &&
                    mechanics.PeriodicDamage is null,
                _ => false
            };

        public MedusaOwnedAbandonResult AbandonRun(
            int requestedByCharacterId,
            DateTimeOffset abandonedAt)
        {
            EnsureCoupledClocks(out _, out _);
            var authoritativeAt = abandonedAt.ToUniversalTime();
            var preview = _run.PreviewAbandonRun(
                requestedByCharacterId,
                authoritativeAt);
            if (preview is not (
                    MedusaRunAbandonOutcome.Exited or
                    MedusaRunAbandonOutcome
                        .DeadlineBoundaryUnresolved or
                    MedusaRunAbandonOutcome.TimedOut))
            {
                return new(
                    GateForRunAbandon(preview),
                    preview,
                    MechanicsClockResult: null);
            }
            var periodic = _mechanics.ReservePeriodicDamage(
                authoritativeAt);
            if (periodic.Outcome ==
                    MedusaPeriodicDamageReserveOutcome.Reserved)
            {
                return new(
                    MedusaOwnedOperationGateOutcome
                        .PeriodicDamageRequired,
                    RunOutcome: null,
                    new(
                        MedusaMechanicsClockOutcome
                            .PeriodicDamageRequired,
                        periodic.Reservation))
                {
                    PeriodicDamage = periodic.Reservation
                };
            }
            var runOutcome = _run.AbandonRun(
                requestedByCharacterId,
                authoritativeAt);
            var mechanicsClock = _mechanics.ObserveTime(authoritativeAt);
            var clocksMatch = runOutcome switch
            {
                MedusaRunAbandonOutcome.Exited or
                MedusaRunAbandonOutcome.TimedOut =>
                    mechanicsClock.Outcome ==
                        MedusaMechanicsClockOutcome.Advanced,
                MedusaRunAbandonOutcome.DeadlineBoundaryUnresolved =>
                    mechanicsClock.Outcome ==
                        MedusaMechanicsClockOutcome
                            .DeadlineBoundaryUnresolved,
                _ => false
            };
            if (!clocksMatch || !HasCoupledClockScalars())
            {
                return new(
                    MedusaOwnedOperationGateOutcome.InvariantFault,
                    RunOutcome: null,
                    mechanicsClock);
            }
            if (runOutcome is MedusaRunAbandonOutcome.Exited or
                MedusaRunAbandonOutcome.TimedOut)
            {
                _ = _mechanics.ClearAllEffectsAfterRunTerminal();
            }
            return new(
                GateForRunAbandon(runOutcome),
                runOutcome,
                mechanicsClock);
        }

        private MedusaDefeatClaimResult ClaimResultForPreview(
            MedusaDefeatClaimPreviewOutcome outcome) => new(
            outcome switch
            {
                MedusaDefeatClaimPreviewOutcome.DuplicateDefeat =>
                    MedusaDefeatClaimOutcome.DuplicateDefeat,
                MedusaDefeatClaimPreviewOutcome.UnknownSpawn =>
                    MedusaDefeatClaimOutcome.UnknownSpawn,
                MedusaDefeatClaimPreviewOutcome.StaleSpawnGeneration =>
                    MedusaDefeatClaimOutcome.StaleSpawnGeneration,
                MedusaDefeatClaimPreviewOutcome.CharacterNotAdmitted =>
                    MedusaDefeatClaimOutcome.CharacterNotAdmitted,
                MedusaDefeatClaimPreviewOutcome.TimestampMovedBackward =>
                    MedusaDefeatClaimOutcome.TimestampMovedBackward,
                MedusaDefeatClaimPreviewOutcome
                    .DeadlineBoundaryUnresolved =>
                    MedusaDefeatClaimOutcome
                        .DeadlineBoundaryUnresolved,
                MedusaDefeatClaimPreviewOutcome.TimedOut =>
                    MedusaDefeatClaimOutcome.TimedOut,
                MedusaDefeatClaimPreviewOutcome.RunNotActive =>
                    MedusaDefeatClaimOutcome.RunNotActive,
                _ => MedusaDefeatClaimOutcome.InvariantFault
            },
            ScoreAwarded: 0,
            TeamScore: _run.OwnerTeamScore);

        public MedusaOwnedOutgoingDamageResult PreviewOutgoingDamage(
            int attackingCharacterId,
            in CombatResolution source)
        {
            EnsureCoupledClocks(out var runBefore, out _);
            if (runBefore.State != MedusaRunState.Active ||
                runBefore.LastObservedAt >= runBefore.Deadline)
            {
                return new(
                    GateForRunState(runBefore),
                    MechanicsResult: null);
            }

            var result = _mechanics.PreviewOutgoingDamage(
                attackingCharacterId,
                attackingOwnership:
                    MedusaEncounterMechanicsRuntime
                        .CompatibilityOwnership,
                attackingLifeRevision: 0,
                attackingWorldMembershipEpoch:
                    PureCompatibilityWorldMembershipEpoch,
                runBefore.LastObservedAt,
                source);
            EnsureCoupledClocks(out _, out _);
            return new(
                MedusaOwnedOperationGateOutcome.Delegated,
                result);
        }

        private static MedusaOwnedOperationGateOutcome GateForRunClock(
            MedusaRunClockOutcome outcome) => outcome switch
            {
                MedusaRunClockOutcome.Active =>
                    MedusaOwnedOperationGateOutcome.Delegated,
                MedusaRunClockOutcome.DeadlineBoundaryUnresolved =>
                    MedusaOwnedOperationGateOutcome
                        .DeadlineBoundaryUnresolved,
                MedusaRunClockOutcome.TimedOut =>
                    MedusaOwnedOperationGateOutcome.TimedOut,
                MedusaRunClockOutcome.TimestampMovedBackward =>
                    MedusaOwnedOperationGateOutcome
                        .TimestampMovedBackward,
                MedusaRunClockOutcome.RunNotActive =>
                    MedusaOwnedOperationGateOutcome.RunNotActive,
                _ => throw new InvalidOperationException(
                    $"Unknown Medusa run-clock outcome {outcome}.")
            };

        private static MedusaOwnedOperationGateOutcome GateForDefeatClaim(
            MedusaDefeatClaimOutcome outcome) => outcome switch
            {
                MedusaDefeatClaimOutcome.Applied or
                MedusaDefeatClaimOutcome.Completed =>
                    MedusaOwnedOperationGateOutcome.Delegated,
                MedusaDefeatClaimOutcome.TimestampMovedBackward =>
                    MedusaOwnedOperationGateOutcome.TimestampMovedBackward,
                MedusaDefeatClaimOutcome.DeadlineBoundaryUnresolved =>
                    MedusaOwnedOperationGateOutcome
                        .DeadlineBoundaryUnresolved,
                MedusaDefeatClaimOutcome.TimedOut =>
                    MedusaOwnedOperationGateOutcome.TimedOut,
                MedusaDefeatClaimOutcome.InvariantFault =>
                    MedusaOwnedOperationGateOutcome.InvariantFault,
                _ => MedusaOwnedOperationGateOutcome.RunNotActive
            };

        private static MedusaOwnedOperationGateOutcome GateForRunAbandon(
            MedusaRunAbandonOutcome outcome) => outcome switch
            {
                MedusaRunAbandonOutcome.Exited =>
                    MedusaOwnedOperationGateOutcome.Delegated,
                MedusaRunAbandonOutcome.TimestampMovedBackward =>
                    MedusaOwnedOperationGateOutcome.TimestampMovedBackward,
                MedusaRunAbandonOutcome.DeadlineBoundaryUnresolved =>
                    MedusaOwnedOperationGateOutcome
                        .DeadlineBoundaryUnresolved,
                MedusaRunAbandonOutcome.TimedOut =>
                    MedusaOwnedOperationGateOutcome.TimedOut,
                _ => MedusaOwnedOperationGateOutcome.RunNotActive
            };

        private static MedusaOwnedOperationGateOutcome GateForRunState(
            MedusaRunSnapshot run) => run.State switch
            {
                MedusaRunState.TimedOut =>
                    MedusaOwnedOperationGateOutcome.TimedOut,
                MedusaRunState.Active
                    when run.LastObservedAt >= run.Deadline =>
                    MedusaOwnedOperationGateOutcome
                        .DeadlineBoundaryUnresolved,
                _ => MedusaOwnedOperationGateOutcome.RunNotActive
            };
    }

    internal bool TryObserveMedusaTime(
        DateTimeOffset observedAt,
        out MedusaOwnedClockResult result)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                result = owner.ObserveTime(observedAt);
                return true;
            }

            result = default;
            return false;
        }
    }

    internal bool TryAbandonMedusaRun(
        int requestedByCharacterId,
        DateTimeOffset abandonedAt,
        out MedusaOwnedAbandonResult result)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                result = owner.AbandonRun(
                    requestedByCharacterId,
                    abandonedAt);
                return true;
            }

            result = default;
            return false;
        }
    }

    internal bool TryPreviewMedusaOutgoingDamage(
        int attackingCharacterId,
        in CombatResolution source,
        out MedusaOwnedOutgoingDamageResult result)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                result = owner.PreviewOutgoingDamage(
                    attackingCharacterId,
                    source);
                return true;
            }

            result = default;
            return false;
        }
    }

    private bool TryCompleteMedusaPeriodicDamageForProtocolCheck(
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
            reservation,
        bool terminal,
        out MedusaPeriodicDamageDispositionOutcome outcome)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is { } owner)
            {
                outcome = owner.CompletePeriodicDamageForProtocolCheck(
                    reservation,
                    terminal);
                return true;
            }

            outcome = MedusaPeriodicDamageDispositionOutcome
                .ForeignReservation;
            return false;
        }
    }

}
