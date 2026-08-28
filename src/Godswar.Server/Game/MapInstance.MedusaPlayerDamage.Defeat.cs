using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        internal sealed class PreparedPlayerDefeat(
            MedusaRunRuntime.PreparedDefeatClaim run,
            MedusaEncounterMechanicsRuntime.PreparedSourceRetirement
                mechanics)
        {
            internal MedusaRunRuntime.PreparedDefeatClaim Run { get; } = run;

            internal MedusaEncounterMechanicsRuntime
                .PreparedSourceRetirement Mechanics { get; } = mechanics;

            internal bool Completed { get; set; }
        }

        private static readonly MedusaOwnedDefeatResult
            PreparedDefeatInvariantFault = new(
                MedusaOwnedOperationGateOutcome.InvariantFault,
                new(
                    MedusaDefeatClaimOutcome.InvariantFault,
                    ScoreAwarded: 0,
                    TeamScore: 0),
                SourceRetirement: null,
                MechanicsClockResult: null);

        public bool TryPrepareDefeat(
            int defeatedByCharacterId,
            uint objectId,
            uint spawnGeneration,
            DateTimeOffset occurredAt,
            out PreparedPlayerDefeat? prepared,
            out MedusaOwnedDefeatResult rejection)
        {
            if (!_run.TryPrepareDefeatClaim(
                    defeatedByCharacterId,
                    objectId,
                    spawnGeneration,
                    occurredAt,
                    out var run,
                    out var runRejection))
            {
                prepared = null;
                rejection = new(
                    GateForDefeatClaim(runRejection.Outcome),
                    runRejection,
                    SourceRetirement: null,
                    MechanicsClockResult: null);
                return false;
            }
            if (!_mechanics.TryPrepareSourceRetirement(
                    objectId,
                    spawnGeneration,
                    occurredAt,
                    out var mechanics,
                    out var mechanicsRejection))
            {
                prepared = null;
                rejection = new(
                    MedusaOwnedOperationGateOutcome.RunNotActive,
                    Claim: null,
                    mechanicsRejection,
                    MechanicsClockResult: null);
                return false;
            }

            rejection = default;
            prepared = new(run!, mechanics!);
            return true;
        }

        public MedusaOwnedDefeatResult CompletePreparedDefeat(
            PreparedPlayerDefeat? prepared)
        {
            if (prepared is null ||
                prepared.Completed ||
                !HasCoupledClockScalars() ||
                !_run.CanCompletePreparedDefeat(prepared.Run) ||
                !_mechanics.CanCompletePreparedSourceRetirement(
                    prepared.Mechanics,
                    prepared.Run.CompletesRun))
            {
                if (prepared is not null)
                {
                    prepared.Completed = true;
                }
                return PreparedDefeatInvariantFault;
            }

            prepared.Completed = true;
            var claim = _run.CompletePreparedDefeat(prepared.Run);
            var retirement = _mechanics
                .CompletePreparedSourceRetirement(
                    prepared.Mechanics,
                    prepared.Run.CompletesRun);
            return new(
                MedusaOwnedOperationGateOutcome.Delegated,
                claim,
                retirement,
                MechanicsClockResult: null);
        }

        public static MedusaOwnedDefeatResult DefeatInvariantFault() =>
            PreparedDefeatInvariantFault;

#if DEBUG
        public static void InvalidatePreparedDefeatForProtocolCheck(
            PreparedPlayerDefeat prepared,
            int fault)
        {
            if (fault == 1)
            {
                MedusaRunRuntime.InvalidatePreparedDefeatForProtocolCheck(
                    prepared.Run);
            }
            else if (fault == 2)
            {
                MedusaEncounterMechanicsRuntime
                    .InvalidatePreparedRetirementForProtocolCheck(
                        prepared.Mechanics);
            }
            else if (fault == 3)
            {
                MedusaEncounterMechanicsRuntime
                    .InvalidatePreparedTerminalClearForProtocolCheck(
                        prepared.Mechanics);
            }
        }
#endif
    }
}
