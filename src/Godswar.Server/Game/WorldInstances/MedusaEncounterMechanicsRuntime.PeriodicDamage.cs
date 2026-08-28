namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    internal enum PeriodicReservationState : byte
    {
        Pending = 1,
        Applied = 2,
        Terminal = 3,
        Canceled = 4
    }

    /// <summary>
    /// Owner-bound, process-local capability for one exact Bleed tick. While
    /// pending, every reserve attempt receives this same object. The effect's
    /// tick cursor and encounter clock remain unchanged until disposition.
    /// </summary>
    internal sealed class PeriodicDamageReservation
    {
        internal PeriodicDamageReservation(
            MedusaEncounterMechanicsRuntime owner,
            CharacterState character,
            ActiveEffectState effect,
            MedusaPeriodicDamageIdentity identity,
            DateTimeOffset? nextTickAt)
        {
            Owner = owner;
            Character = character;
            Effect = effect;
            Identity = identity;
            NextTickAt = nextTickAt;
        }

        internal MedusaEncounterMechanicsRuntime Owner { get; }

        internal CharacterState Character { get; }

        internal ActiveEffectState Effect { get; }

        internal DateTimeOffset? NextTickAt { get; }

        internal PeriodicReservationState State { get; set; } =
            PeriodicReservationState.Pending;

        public MedusaPeriodicDamageIdentity Identity { get; }
    }

    public MedusaPeriodicDamageReserveResult ReservePeriodicDamage(
        DateTimeOffset observedAt)
    {
        var authoritativeAt = observedAt.ToUniversalTime();
        if (authoritativeAt < _lastObservedAt)
        {
            return new(
                MedusaPeriodicDamageReserveOutcome.TimestampMovedBackward,
                Reservation: null);
        }
        if (_pendingPeriodicDamage is { } pending)
        {
            return new(
                MedusaPeriodicDamageReserveOutcome.Reserved,
                pending);
        }

        CharacterState? selectedCharacter = null;
        ActiveEffectState? selectedEffect = null;
        DateTimeOffset selectedDueAt = default;
        foreach (var character in _orderedCharacters)
        {
            foreach (var kind in EffectKinds)
            {
                if (!character.Effects.TryGetValue(kind, out var effect) ||
                    effect.Definition.Bleed is not { } bleed ||
                    effect.NextPeriodicTickAt is not { } dueAt ||
                    dueAt > authoritativeAt ||
                    dueAt >= effect.ExpiresAt ||
                    dueAt >= Deadline ||
                    effect.EmittedPeriodicTicks >= bleed.MaximumTicks)
                {
                    continue;
                }

                if (selectedEffect is null ||
                    ComparePeriodicWork(
                        dueAt,
                        character,
                        effect,
                        selectedDueAt,
                        selectedCharacter!,
                        selectedEffect) < 0)
                {
                    selectedCharacter = character;
                    selectedEffect = effect;
                    selectedDueAt = dueAt;
                }
            }
        }

        if (selectedCharacter is null || selectedEffect is null)
        {
            return new(
                authoritativeAt == Deadline
                    ? MedusaPeriodicDamageReserveOutcome
                        .DeadlineBoundaryUnresolved
                    : MedusaPeriodicDamageReserveOutcome.NoneDue,
                Reservation: null);
        }

        var profile = selectedEffect.Definition.Bleed!.Value;
        var tickNumber = checked(selectedEffect.EmittedPeriodicTicks + 1);
        DateTimeOffset? nextTickAt = null;
        if (tickNumber < profile.MaximumTicks &&
            TryAdd(selectedDueAt, profile.TickInterval, out var next) &&
            next < selectedEffect.ExpiresAt)
        {
            nextTickAt = next;
        }

        var identity = new MedusaPeriodicDamageIdentity(
            WorldInstanceId,
            selectedCharacter.CharacterId,
            selectedEffect.TargetOwnership,
            selectedEffect.TargetLifeRevision,
            selectedEffect.TargetWorldMembershipEpoch,
            selectedEffect.Source.RosterSpawnId,
            selectedEffect.Source.ObjectId,
            selectedEffect.Source.SpawnGeneration,
            selectedEffect.ApplicationSequence,
            tickNumber,
            selectedDueAt,
            profile.DamageKind,
            profile.DamagePerTick);
        if (!identity.IsValid)
        {
            throw new InvalidOperationException(
                "Authored Medusa periodic damage identity is invalid.");
        }

        var reservation = new PeriodicDamageReservation(
            this,
            selectedCharacter,
            selectedEffect,
            identity,
            nextTickAt);
        _pendingPeriodicDamage = reservation;
        return new(
            MedusaPeriodicDamageReserveOutcome.Reserved,
            reservation);
    }

    public MedusaPeriodicDamageDispositionOutcome
        CancelPeriodicDamage(
            PeriodicDamageReservation? reservation)
    {
        if (!BelongsToThisOwner(reservation))
        {
            return MedusaPeriodicDamageDispositionOutcome
                .ForeignReservation;
        }
        if (reservation!.State != PeriodicReservationState.Pending)
        {
            return MedusaPeriodicDamageDispositionOutcome
                .AlreadyCompleted;
        }
        if (!ReferenceEquals(_pendingPeriodicDamage, reservation))
        {
            return MedusaPeriodicDamageDispositionOutcome
                .ForeignReservation;
        }

        reservation.State = PeriodicReservationState.Canceled;
        _pendingPeriodicDamage = null;
        return MedusaPeriodicDamageDispositionOutcome.Canceled;
    }

    public MedusaPeriodicDamageDispositionOutcome
        CompletePeriodicDamageApplied(
            PeriodicDamageReservation? reservation) =>
        CompletePeriodicDamage(
            reservation,
            terminal: false);

    public MedusaPeriodicDamageDispositionOutcome
        CompletePeriodicDamageTerminal(
            PeriodicDamageReservation? reservation) =>
        CompletePeriodicDamage(
            reservation,
            terminal: true);

    internal bool IsPendingPeriodicDamage(
        PeriodicDamageReservation? reservation) =>
        reservation is not null &&
        ReferenceEquals(reservation.Owner, this) &&
        reservation.State == PeriodicReservationState.Pending &&
        ReferenceEquals(_pendingPeriodicDamage, reservation);

    internal MedusaPeriodicDamageDispositionOutcome
        CompletePeriodicDamageInvariantFault(
            PeriodicDamageReservation? reservation)
    {
        if (!BelongsToThisOwner(reservation))
        {
            return MedusaPeriodicDamageDispositionOutcome
                .ForeignReservation;
        }
        if (reservation!.State != PeriodicReservationState.Pending)
        {
            return MedusaPeriodicDamageDispositionOutcome
                .AlreadyCompleted;
        }
        if (!ReferenceEquals(_pendingPeriodicDamage, reservation))
        {
            return MedusaPeriodicDamageDispositionOutcome
                .ForeignReservation;
        }

        reservation.State = PeriodicReservationState.Terminal;
        _pendingPeriodicDamage = null;
        if (reservation.Character.Effects.TryGetValue(
                reservation.Effect.Definition.Kind,
                out var current) &&
            ReferenceEquals(current, reservation.Effect))
        {
            reservation.Character.Effects.Remove(
                reservation.Effect.Definition.Kind);
        }
        return MedusaPeriodicDamageDispositionOutcome.InvariantFault;
    }

    private MedusaPeriodicDamageDispositionOutcome CompletePeriodicDamage(
        PeriodicDamageReservation? reservation,
        bool terminal)
    {
        if (!BelongsToThisOwner(reservation))
        {
            return MedusaPeriodicDamageDispositionOutcome
                .ForeignReservation;
        }
        if (reservation!.State != PeriodicReservationState.Pending)
        {
            return MedusaPeriodicDamageDispositionOutcome
                .AlreadyCompleted;
        }
        if (!ReferenceEquals(_pendingPeriodicDamage, reservation))
        {
            return MedusaPeriodicDamageDispositionOutcome
                .ForeignReservation;
        }

        // The capability is the one-time marker. Consume it before touching
        // any newer effect that may have replaced the referenced application.
        reservation.State = terminal
            ? PeriodicReservationState.Terminal
            : PeriodicReservationState.Applied;
        _pendingPeriodicDamage = null;

        if (reservation.Character.Effects.TryGetValue(
                reservation.Effect.Definition.Kind,
                out var current) &&
            ReferenceEquals(current, reservation.Effect))
        {
            reservation.Effect.EmittedPeriodicTicks =
                reservation.Identity.TickNumber;
            reservation.Effect.NextPeriodicTickAt =
                reservation.NextTickAt;
            if (terminal)
            {
                reservation.Character.Effects.Remove(
                    reservation.Effect.Definition.Kind);
            }
        }

        AdvanceWithoutPeriodicDamage(reservation.Identity.DueAt);
        return terminal
            ? MedusaPeriodicDamageDispositionOutcome.Terminal
            : MedusaPeriodicDamageDispositionOutcome.Applied;
    }

    private bool BelongsToThisOwner(
        PeriodicDamageReservation? reservation) =>
        reservation is not null &&
        ReferenceEquals(reservation.Owner, this);

    private static int ComparePeriodicWork(
        DateTimeOffset leftDueAt,
        CharacterState leftCharacter,
        ActiveEffectState leftEffect,
        DateTimeOffset rightDueAt,
        CharacterState rightCharacter,
        ActiveEffectState rightEffect)
    {
        var comparison = leftDueAt.CompareTo(rightDueAt);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = leftCharacter.CharacterId.CompareTo(
            rightCharacter.CharacterId);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = leftEffect.ApplicationSequence.CompareTo(
            rightEffect.ApplicationSequence);
        return comparison != 0
            ? comparison
            : leftEffect.EmittedPeriodicTicks.CompareTo(
                rightEffect.EmittedPeriodicTicks);
    }
}
