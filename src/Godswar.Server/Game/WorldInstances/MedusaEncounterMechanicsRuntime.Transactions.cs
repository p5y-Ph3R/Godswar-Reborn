namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    /// <summary>
    /// Complete process-local mechanics state needed to undo a prospective
    /// monster hit. The snapshot is built before player vitals are invoked;
    /// restoring it only swaps prebuilt dictionaries and scalar fields.
    /// </summary>
    internal sealed class MonsterHitTransactionSnapshot
    {
        internal MonsterHitTransactionSnapshot(
            MedusaEncounterMechanicsRuntime owner,
            DateTimeOffset lastObservedAt,
            ulong nextApplicationSequence,
            Dictionary<MedusaEncounterEffectKind, ActiveEffectState>[]
                characterEffects)
        {
            Owner = owner;
            LastObservedAt = lastObservedAt;
            NextApplicationSequence = nextApplicationSequence;
            CharacterEffects = characterEffects;
        }

        internal MedusaEncounterMechanicsRuntime Owner { get; }

        internal DateTimeOffset LastObservedAt { get; }

        internal ulong NextApplicationSequence { get; }

        internal Dictionary<MedusaEncounterEffectKind, ActiveEffectState>[]
            CharacterEffects { get; }
    }

    internal MonsterHitTransactionSnapshot
        CaptureMonsterHitTransactionSnapshot()
    {
        if (_pendingPeriodicDamage is not null)
        {
            throw new InvalidOperationException(
                "Periodic damage must be dispositioned before a monster " +
                "hit transaction snapshot is captured.");
        }

        var effects = new Dictionary<
            MedusaEncounterEffectKind,
            ActiveEffectState>[_orderedCharacters.Count];
        for (var index = 0; index < _orderedCharacters.Count; index++)
        {
            var source = _orderedCharacters[index].Effects;
            var copy = new Dictionary<
                MedusaEncounterEffectKind,
                ActiveEffectState>(EffectKinds.Length);
            foreach (var pair in source)
            {
                copy.Add(pair.Key, Clone(pair.Value));
            }

            effects[index] = copy;
        }

        return new(
            this,
            _lastObservedAt,
            _nextApplicationSequence,
            effects);
    }

    internal void RestoreMonsterHitTransactionSnapshot(
        MonsterHitTransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.Owner, this) ||
            snapshot.CharacterEffects.Length !=
                _orderedCharacters.Count)
        {
            throw new ArgumentException(
                "Mechanics transaction snapshot belongs to another runtime.",
                nameof(snapshot));
        }
        if (_pendingPeriodicDamage is not null)
        {
            throw new InvalidOperationException(
                "Periodic damage must be dispositioned before a monster " +
                "hit transaction snapshot is restored.");
        }

        _lastObservedAt = snapshot.LastObservedAt;
        _nextApplicationSequence = snapshot.NextApplicationSequence;
        for (var index = 0; index < _orderedCharacters.Count; index++)
        {
            _orderedCharacters[index].RestoreEffects(
                snapshot.CharacterEffects[index]);
        }
    }

    private static ActiveEffectState Clone(ActiveEffectState source)
    {
        var clone = new ActiveEffectState(
            source.Definition,
            source.TargetOwnership,
            source.TargetLifeRevision,
            source.TargetWorldMembershipEpoch,
            source.Source,
            source.ApplicationSequence,
            source.AppliedAt,
            source.ExpiresAt,
            source.NextPeriodicTickAt)
        {
            EmittedPeriodicTicks = source.EmittedPeriodicTicks
        };
        return clone;
    }
}
