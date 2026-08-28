using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed class MedusaPreparedMonsterPlayerEffect
    {
        public MedusaPreparedMonsterPlayerEffect(
            MedusaMonsterPlayerHitCaptureOutcome outcome,
            MedusaEncounterEffectKind? effectKind,
            MedusaEncounterMechanicsRuntime.MonsterHitReservation?
                reservation,
            MedusaRunRuntime.MonsterHitClockSnapshot? runSnapshot,
            MedusaEncounterMechanicsRuntime
                .MonsterHitTransactionSnapshot? mechanicsSnapshot)
        {
            Outcome = outcome;
            EffectKind = effectKind;
            Reservation = reservation;
            RunSnapshot = runSnapshot;
            MechanicsSnapshot = mechanicsSnapshot;
        }

        public MedusaMonsterPlayerHitCaptureOutcome Outcome { get; }

        public MedusaEncounterEffectKind? EffectKind { get; }

        public MedusaEncounterMechanicsRuntime.MonsterHitReservation?
            Reservation { get; }

        public MedusaRunRuntime.MonsterHitClockSnapshot? RunSnapshot
            { get; }

        public MedusaEncounterMechanicsRuntime.MonsterHitTransactionSnapshot?
            MechanicsSnapshot { get; }

        public bool Completed { get; set; }
    }

    private bool FinalizeCommittedMonsterPlayerDeath(
        MedusaInstanceOwnerBoundAggregate owner,
        in MedusaMonsterPlayerSourceAuthority source,
        in MedusaMonsterPlayerTargetAuthority target)
    {
        try
        {
            var cleared = owner.ClearMonsterPlayerEffectsForLife(
                target.CharacterId,
                target.Ownership,
                target.LifeRevision,
                target.WorldMembershipEpoch,
                source.CommittedAt);
            _monsterRuntime!.ClearAggroForCharacterStateOnly(
                target.CharacterId,
                source.CommittedAt);
            return cleared.Outcome ==
                MedusaPeriodicDamageReserveOutcome.NoneDue;
        }
        catch
        {
            return false;
        }
    }
}
