using Godswar.Server.Application.Characters;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    /// <summary>
    /// Opaque, single-use publication capability. Allocation, clock advance,
    /// sequence reservation, and dictionary capacity are completed before a
    /// player-vitals mutation. Finalization is then one owner-only replacement
    /// with no validation or allocation on the normal path.
    /// </summary>
    internal sealed class MonsterHitReservation
    {
        private readonly ActiveEffectState _active;
        private readonly CharacterState _character;

        private bool _completed;

        internal MonsterHitReservation(
            MedusaEncounterMechanicsRuntime owner,
            CharacterState character,
            ActiveEffectState active,
            MedusaMechanicHitResult preparedResult)
        {
            Owner = owner;
            _character = character;
            _active = active;
            PreparedResult = preparedResult;
        }

        internal MedusaEncounterMechanicsRuntime Owner { get; }

        private MedusaMechanicHitResult PreparedResult { get; }

        internal void Cancel()
        {
            if (!_completed)
            {
                _completed = true;
            }
        }

        internal MedusaMechanicHitResult FinalizeEffect()
        {
            // This is deliberately the sole post-vitals publication point.
            // The owner creates the capability and invokes it synchronously
            // without releasing its mailbox lane, so these invariants require
            // no fallible runtime revalidation here.
            foreach (var kind in EffectKinds)
            {
                if (_character.Effects.TryGetValue(kind, out var effect) &&
                    (effect.TargetOwnership != _active.TargetOwnership ||
                     effect.TargetLifeRevision !=
                         _active.TargetLifeRevision ||
                     effect.TargetWorldMembershipEpoch !=
                         _active.TargetWorldMembershipEpoch))
                {
                    _character.Effects.Remove(kind);
                }
            }
            _character.Effects[_active.Definition.Kind] = _active;
            _completed = true;
            return PreparedResult;
        }
    }

    internal readonly record struct MonsterHitReservationResult(
        MedusaMechanicHitOutcome Outcome,
        MonsterHitReservation? Reservation,
        PeriodicDamageReservation? PeriodicDamage);

    internal MonsterHitReservationResult ReserveMonsterHit(
        int targetCharacterId,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt) => ReserveMonsterHit(
        targetCharacterId,
        targetOwnership: CompatibilityOwnershipFor(sourceObjectId),
        targetLifeRevision: 0,
        sourceObjectId,
        sourceSpawnGeneration,
        committedAt);

    internal MonsterHitReservationResult ReserveMonsterHit(
        int targetCharacterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt) => ReserveMonsterHit(
        targetCharacterId,
        targetOwnership,
        targetLifeRevision,
        targetWorldMembershipEpoch:
            PureCompatibilityWorldMembershipEpoch,
        sourceObjectId,
        sourceSpawnGeneration,
        committedAt);

    internal MonsterHitReservationResult ReserveMonsterHit(
        int targetCharacterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt)
    {
        var preview = PreviewMonsterHit(
            targetCharacterId,
            targetOwnership,
            targetLifeRevision,
            targetWorldMembershipEpoch,
            sourceObjectId,
            sourceSpawnGeneration,
            committedAt);
        if (preview is not MedusaMechanicHitOutcome.Applied and
            not MedusaMechanicHitOutcome.Refreshed)
        {
            var periodic = preview ==
                    MedusaMechanicHitOutcome.PeriodicDamageRequired
                ? ReservePeriodicDamage(committedAt).Reservation
                : null;
            return new(
                preview,
                Reservation: null,
                periodic);
        }

        var character = _charactersById[targetCharacterId];
        var monster = _monstersByObjectId[sourceObjectId];
        var definition = monster.Effect!.Value;
        var authoritativeAt = committedAt.ToUniversalTime();
        _ = TryAdd(
            authoritativeAt,
            definition.Duration,
            out var expiresAt);
        AdvanceWithoutPeriodicDamage(authoritativeAt);
        var sequence = ++_nextApplicationSequence;
        DateTimeOffset? firstTickAt = definition.Bleed is { } bleed
            ? authoritativeAt.Add(bleed.TickInterval)
            : null;
        var active = new ActiveEffectState(
            definition,
            targetOwnership,
            targetLifeRevision,
            targetWorldMembershipEpoch,
            monster.Spawn,
            sequence,
            authoritativeAt,
            expiresAt,
            firstTickAt);

        // All six authored kinds may coexist. Reserving full capacity before
        // the HP callback keeps the final dictionary replacement allocation
        // free even for the first effect on this life.
        _ = character.Effects.EnsureCapacity(
            EffectKinds.Length);
        var result = new MedusaMechanicHitResult(
            preview,
            active.Snapshot(),
            PeriodicDamage: null);
        var reservation = new MonsterHitReservation(
            this,
            character,
            active,
            result);
        return new(preview, reservation, PeriodicDamage: null);
    }

    internal MedusaMechanicHitResult FinalizeReservedMonsterHit(
        MonsterHitReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (!ReferenceEquals(reservation.Owner, this))
        {
            throw new ArgumentException(
                "The mechanic reservation belongs to another runtime.",
                nameof(reservation));
        }

        return reservation.FinalizeEffect();
    }

    internal void CancelReservedMonsterHit(
        MonsterHitReservation? reservation)
    {
        if (reservation is not null &&
            ReferenceEquals(reservation.Owner, this))
        {
            reservation.Cancel();
        }
    }
}
