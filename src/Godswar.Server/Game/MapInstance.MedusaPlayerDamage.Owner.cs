using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        internal sealed class PlayerDamageClockReservation(
            MedusaRunRuntime.MonsterHitClockSnapshot run,
            MedusaEncounterMechanicsRuntime.MonsterHitTransactionSnapshot
                mechanics)
        {
            internal MedusaRunRuntime.MonsterHitClockSnapshot Run { get; } =
                run;

            internal MedusaEncounterMechanicsRuntime
                .MonsterHitTransactionSnapshot Mechanics { get; } =
                mechanics;

            internal bool Completed { get; set; }
        }

        public MedusaPlayerMonsterDamageOutcome PreviewPlayerDamage(
            int attackingCharacterId,
            PlayerOwnershipFence attackingOwnership,
            long attackingLifeRevision,
            long attackingWorldMembershipEpoch,
            uint objectId,
            uint spawnGeneration,
            DateTimeOffset committedAt,
            in CombatResolution source,
            out MedusaOwnedMonsterBinding binding,
            out CombatResolution resolution)
        {
            EnsureCoupledClocks(out _, out _);
            resolution = source;
            if (!_bindings.TryGetValue(
                    new(objectId, spawnGeneration),
                    out binding))
            {
                return _orderedBindings.Any(candidate =>
                    candidate.Identity.ObjectId == objectId)
                    ? MedusaPlayerMonsterDamageOutcome
                        .StaleMonsterGeneration
                    : MedusaPlayerMonsterDamageOutcome.UnknownMonster;
            }
            var defeat = _run.PreviewDefeatClaim(
                attackingCharacterId,
                objectId,
                spawnGeneration,
                committedAt);
            if (defeat is not (
                    MedusaDefeatClaimPreviewOutcome.Eligible or
                    MedusaDefeatClaimPreviewOutcome
                        .DeadlineBoundaryUnresolved or
                    MedusaDefeatClaimPreviewOutcome.TimedOut))
            {
                return OutcomeFor(defeat);
            }

            var outgoing = _mechanics.PreviewOutgoingDamage(
                attackingCharacterId,
                attackingOwnership,
                attackingLifeRevision,
                attackingWorldMembershipEpoch,
                committedAt,
                source);
            if (outgoing.Outcome !=
                MedusaOutgoingDamageOutcome.Resolved)
            {
                return outgoing.Outcome switch
                {
                    MedusaOutgoingDamageOutcome.CharacterNotAdmitted =>
                        MedusaPlayerMonsterDamageOutcome
                            .CharacterNotAdmitted,
                    MedusaOutgoingDamageOutcome.TimestampMovedBackward =>
                        MedusaPlayerMonsterDamageOutcome
                            .TimestampMovedBackward,
                    _ => MedusaPlayerMonsterDamageOutcome.InvalidResolution
                };
            }

            resolution = MedusaIslandCombatOverride
                .ApplyFinalIncomingDamage(
                    binding.Difficulty,
                    binding.Role,
                    outgoing.Resolution);
            if (_mechanics.HasDuePeriodicDamage(committedAt))
            {
                return MedusaPlayerMonsterDamageOutcome
                    .PeriodicDamageHandoffUnavailable;
            }
            if (defeat != MedusaDefeatClaimPreviewOutcome.Eligible)
            {
                return OutcomeFor(defeat);
            }
            EnsureCoupledClocks(out _, out _);
            return MedusaPlayerMonsterDamageOutcome.AppliedMedusa;
        }

        public MedusaOwnedDefeatPreview PreviewDefeat(
            int defeatedByCharacterId,
            uint objectId,
            uint spawnGeneration,
            DateTimeOffset occurredAt)
        {
            EnsureCoupledClocks(out _, out _);
            return new(
                _run.PreviewDefeatClaim(
                    defeatedByCharacterId,
                    objectId,
                    spawnGeneration,
                    occurredAt),
                _mechanics.PreviewRetireMonster(
                    objectId,
                    spawnGeneration,
                    occurredAt),
                _mechanics.HasDuePeriodicDamage(occurredAt));
        }

        public bool MatchesAttachment(
            MedusaMonsterAttachmentSnapshot attachment) =>
            attachment.WorldInstanceId == _run.WorldInstanceId &&
            attachment.Difficulty == _run.Difficulty &&
            attachment.ContentMapId == _run.ContentMapId &&
            attachment.StartedAt == _run.StartedAt;

        public PlayerDamageClockReservation PreparePlayerDamageClock(
            DateTimeOffset committedAt)
        {
            EnsureCoupledClocks(out _, out _);
            var run = _run.CaptureMonsterHitClockSnapshot();
            var mechanics =
                _mechanics.CaptureMonsterHitTransactionSnapshot();
            var reservation = new PlayerDamageClockReservation(
                run,
                mechanics);
            try
            {
                var runClock = _run.ObserveTime(committedAt);
                var mechanicsClock = _mechanics.ObserveTime(committedAt);
                if (runClock != MedusaRunClockOutcome.Active ||
                    mechanicsClock.Outcome !=
                        MedusaMechanicsClockOutcome.Advanced ||
                    mechanicsClock.PeriodicDamage is not null ||
                    !HasCoupledClockScalars())
                {
                    throw new InvalidOperationException(
                        "A preflighted player damage clock transaction " +
                        "changed outcome before HP.");
                }

                return reservation;
            }
            catch
            {
                _mechanics.RestoreMonsterHitTransactionSnapshot(mechanics);
                _run.RestoreMonsterHitClockSnapshot(run);
                reservation.Completed = true;
                throw;
            }
        }

        public static void CommitPlayerDamageClock(
            PlayerDamageClockReservation reservation) =>
            reservation.Completed = true;

        public void RollBackPlayerDamageClock(
            PlayerDamageClockReservation reservation)
        {
            if (reservation.Completed)
            {
                return;
            }

            _mechanics.RestoreMonsterHitTransactionSnapshot(
                reservation.Mechanics);
            _run.RestoreMonsterHitClockSnapshot(reservation.Run);
            reservation.Completed = true;
        }

        private static MedusaPlayerMonsterDamageOutcome OutcomeFor(
            MedusaDefeatClaimPreviewOutcome outcome) => outcome switch
            {
                MedusaDefeatClaimPreviewOutcome.CharacterNotAdmitted =>
                    MedusaPlayerMonsterDamageOutcome
                        .CharacterNotAdmitted,
                MedusaDefeatClaimPreviewOutcome.UnknownSpawn =>
                    MedusaPlayerMonsterDamageOutcome.UnknownMonster,
                MedusaDefeatClaimPreviewOutcome.StaleSpawnGeneration =>
                    MedusaPlayerMonsterDamageOutcome
                        .StaleMonsterGeneration,
                MedusaDefeatClaimPreviewOutcome.DuplicateDefeat =>
                    MedusaPlayerMonsterDamageOutcome.DuplicateDefeat,
                MedusaDefeatClaimPreviewOutcome.TimestampMovedBackward =>
                    MedusaPlayerMonsterDamageOutcome
                        .TimestampMovedBackward,
                MedusaDefeatClaimPreviewOutcome
                    .DeadlineBoundaryUnresolved =>
                    MedusaPlayerMonsterDamageOutcome
                        .DeadlineBoundaryUnresolved,
                MedusaDefeatClaimPreviewOutcome.TimedOut =>
                    MedusaPlayerMonsterDamageOutcome.TimedOut,
                MedusaDefeatClaimPreviewOutcome.RunNotActive =>
                    MedusaPlayerMonsterDamageOutcome.RunNotActive,
                _ => throw new InvalidOperationException(
                    $"Preview outcome {outcome} is not a rejection.")
            };
    }
}
