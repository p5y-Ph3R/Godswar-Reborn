using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    public MedusaMechanicsClockResult ObserveTime(
        DateTimeOffset observedAt)
    {
        var periodic = ReservePeriodicDamage(observedAt);
        if (periodic.Outcome ==
            MedusaPeriodicDamageReserveOutcome.Reserved)
        {
            return new(
                MedusaMechanicsClockOutcome.PeriodicDamageRequired,
                periodic.Reservation);
        }
        if (periodic.Outcome ==
            MedusaPeriodicDamageReserveOutcome.TimestampMovedBackward)
        {
            return new(
                MedusaMechanicsClockOutcome.TimestampMovedBackward,
                PeriodicDamage: null);
        }
        if (periodic.Outcome ==
            MedusaPeriodicDamageReserveOutcome
                .DeadlineBoundaryUnresolved)
        {
            AdvanceWithoutPeriodicDamage(Deadline);
            return new(
                MedusaMechanicsClockOutcome.DeadlineBoundaryUnresolved,
                PeriodicDamage: null);
        }

        AdvanceWithoutPeriodicDamage(observedAt.ToUniversalTime());
        return new(
            MedusaMechanicsClockOutcome.Advanced,
            PeriodicDamage: null);
    }

    public MedusaMechanicSourceRetireResult RetireMonster(
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset retiredAt)
    {
        var preview = PreviewRetireMonsterIdentity(
            sourceObjectId,
            sourceSpawnGeneration,
            retiredAt);
        if (preview != MedusaMechanicSourceRetireOutcome.Retired)
        {
            return RejectedRetire(preview);
        }
        if (_pendingPeriodicDamage is { } pending)
        {
            return new(
                MedusaMechanicSourceRetireOutcome
                    .PeriodicDamageRequired,
                pending);
        }

        var periodic = ReservePeriodicDamage(retiredAt);
        if (periodic.Outcome ==
            MedusaPeriodicDamageReserveOutcome.Reserved)
        {
            return new(
                MedusaMechanicSourceRetireOutcome
                    .PeriodicDamageRequired,
                periodic.Reservation);
        }
        if (periodic.Outcome ==
            MedusaPeriodicDamageReserveOutcome
                .DeadlineBoundaryUnresolved)
        {
            AdvanceWithoutPeriodicDamage(Deadline);
            return new(
                MedusaMechanicSourceRetireOutcome
                    .DeadlineBoundaryUnresolved,
                PeriodicDamage: null);
        }
        if (periodic.Outcome ==
            MedusaPeriodicDamageReserveOutcome.TimestampMovedBackward)
        {
            return RejectedRetire(
                MedusaMechanicSourceRetireOutcome.TimestampMovedBackward);
        }

        var authoritativeAt = retiredAt.ToUniversalTime();
        AdvanceWithoutPeriodicDamage(authoritativeAt);
        _monstersByObjectId[sourceObjectId].Retired = true;
        return new(
            MedusaMechanicSourceRetireOutcome.Retired,
            PeriodicDamage: null);
    }

    public MedusaMechanicSourceRetireOutcome PreviewRetireMonster(
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset retiredAt)
    {
        var preview = PreviewRetireMonsterIdentity(
            sourceObjectId,
            sourceSpawnGeneration,
            retiredAt);
        if (preview != MedusaMechanicSourceRetireOutcome.Retired)
        {
            return preview;
        }
        if (_pendingPeriodicDamage is not null)
        {
            return MedusaMechanicSourceRetireOutcome
                .PeriodicDamageRequired;
        }

        var authoritativeAt = retiredAt.ToUniversalTime();
        if (HasDuePeriodicDamage(authoritativeAt))
        {
            return MedusaMechanicSourceRetireOutcome
                .PeriodicDamageRequired;
        }
        return authoritativeAt == Deadline
            ? MedusaMechanicSourceRetireOutcome
                .DeadlineBoundaryUnresolved
            : MedusaMechanicSourceRetireOutcome.Retired;
    }

    public bool HasDuePeriodicDamage(DateTimeOffset observedAt)
    {
        if (_pendingPeriodicDamage is not null)
        {
            return true;
        }

        var authoritativeAt = observedAt.ToUniversalTime();
        foreach (var character in _orderedCharacters)
        {
            foreach (var effect in character.Effects.Values)
            {
                if (effect.Definition.Bleed is { } bleed &&
                    effect.NextPeriodicTickAt is { } next &&
                    next <= authoritativeAt &&
                    next < effect.ExpiresAt &&
                    next < Deadline &&
                    effect.EmittedPeriodicTicks < bleed.MaximumTicks)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public MedusaEncounterMechanicsSnapshot Snapshot()
    {
        var characters = _orderedCharacters.Select(character =>
        {
            var effects = character.Effects.Values
                .OrderBy(static effect => effect.Definition.Kind)
                .Select(static effect => effect.Snapshot())
                .ToImmutableArray();
            var control = effects.Aggregate(
                MedusaEncounterControlRestriction.None,
                static (current, effect) =>
                    current | effect.Definition.ControlRestriction);
            var effectTarget = effects.IsEmpty
                ? (MedusaEncounterEffectTarget?)null
                : new(
                    effects[0].TargetOwnership,
                    effects[0].TargetLifeRevision,
                    effects[0].TargetWorldMembershipEpoch);
            System.Diagnostics.Debug.Assert(effects.All(effect =>
                effect.TargetOwnership == effectTarget!.Value.Ownership &&
                effect.TargetLifeRevision ==
                    effectTarget.Value.LifeRevision &&
                effect.TargetWorldMembershipEpoch ==
                    effectTarget.Value.WorldMembershipEpoch));

            return new MedusaEncounterCharacterMechanicsSnapshot(
                character.CharacterId,
                effectTarget,
                control,
                MultiplierFor(effects, MedusaDamageChannel.Physical),
                MultiplierFor(effects, MedusaDamageChannel.Magical),
                effects);
        }).ToImmutableArray();

        return new(
            WorldInstanceId,
            Difficulty,
            ContentMapId,
            StartedAt,
            _lastObservedAt,
            characters)
        {
            OutstandingPeriodicDamage =
                _pendingPeriodicDamage?.Identity
        };
    }

    private MedusaMechanicSourceRetireOutcome PreviewRetireMonsterIdentity(
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset retiredAt)
    {
        if (!_monstersByObjectId.TryGetValue(
                sourceObjectId,
                out var monster))
        {
            return MedusaMechanicSourceRetireOutcome.UnknownMonster;
        }
        if (monster.Spawn.SpawnGeneration != sourceSpawnGeneration)
        {
            return MedusaMechanicSourceRetireOutcome
                .StaleMonsterGeneration;
        }
        if (monster.Retired)
        {
            return MedusaMechanicSourceRetireOutcome.AlreadyRetired;
        }
        if (retiredAt.ToUniversalTime() < _lastObservedAt)
        {
            return MedusaMechanicSourceRetireOutcome
                .TimestampMovedBackward;
        }

        return MedusaMechanicSourceRetireOutcome.Retired;
    }

    private void AdvanceWithoutPeriodicDamage(
        DateTimeOffset authoritativeAt)
    {
        foreach (var character in _orderedCharacters)
        {
            foreach (var kind in EffectKinds)
            {
                if (character.Effects.TryGetValue(kind, out var effect) &&
                    (authoritativeAt >= effect.ExpiresAt ||
                     authoritativeAt > Deadline))
                {
                    character.Effects.Remove(kind);
                }
            }
        }

        _lastObservedAt = authoritativeAt;
    }

    private static int MultiplierFor(
        ImmutableArray<MedusaActiveEncounterEffectSnapshot> effects,
        MedusaDamageChannel channel) => effects
        .Where(effect =>
            effect.Definition.OutgoingDamageChannel == channel)
        .Select(static effect => effect.Definition.OutgoingDamageMultiplier)
        .DefaultIfEmpty(1)
        .Max();

    private static bool TryAdd(
        DateTimeOffset value,
        TimeSpan duration,
        out DateTimeOffset result)
    {
        try
        {
            result = value.Add(duration);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }

    private static MedusaMechanicHitResult RejectedHit(
        MedusaMechanicHitOutcome outcome,
        PeriodicDamageReservation? periodicDamage = null) => new(
        outcome,
        Effect: null,
        periodicDamage);

    private static MedusaMechanicSourceRetireResult RejectedRetire(
        MedusaMechanicSourceRetireOutcome outcome) => new(
        outcome,
        PeriodicDamage: null);
}
