using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Process-local Medusa monster-effect state. It is intended for the same
/// single-owner mailbox as <see cref="MedusaRunRuntime"/> and deliberately
/// performs no player-vitals mutation, packet emission, or persistence.
/// Callers must only submit a hit after the direct monster hit is committed.
/// </summary>
internal sealed partial class MedusaEncounterMechanicsRuntime
{
    private const long PureCompatibilityWorldMembershipEpoch = 1;

    private static readonly PlayerOwnershipFence
        PureCompatibilityOwnership = new(
            new Guid("00000000-0000-0000-0000-000000000001"),
            Generation: 1);

    private static readonly MedusaEncounterEffectKind[] EffectKinds =
        Enum.GetValues<MedusaEncounterEffectKind>();

    internal sealed class MonsterState(
        MedusaRunSpawnSnapshot spawn,
        MedusaEncounterEffectDefinition? effect)
    {
        public MedusaRunSpawnSnapshot Spawn { get; } = spawn;

        public MedusaEncounterEffectDefinition? Effect { get; } = effect;

        public bool Retired { get; set; }
    }

    internal sealed class ActiveEffectState(
        MedusaEncounterEffectDefinition definition,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch,
        MedusaRunSpawnSnapshot source,
        ulong applicationSequence,
        DateTimeOffset appliedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? nextPeriodicTickAt)
    {
        public MedusaEncounterEffectDefinition Definition { get; } = definition;

        public PlayerOwnershipFence TargetOwnership { get; } =
            targetOwnership;

        public long TargetLifeRevision { get; } = targetLifeRevision;

        public long TargetWorldMembershipEpoch { get; } =
            targetWorldMembershipEpoch;

        public MedusaRunSpawnSnapshot Source { get; } = source;

        public ulong ApplicationSequence { get; } = applicationSequence;

        public DateTimeOffset AppliedAt { get; } = appliedAt;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public DateTimeOffset? NextPeriodicTickAt { get; set; } =
            nextPeriodicTickAt;

        public int EmittedPeriodicTicks { get; set; }

        public MedusaActiveEncounterEffectSnapshot Snapshot() => new(
            Definition,
            TargetOwnership,
            TargetLifeRevision,
            Source.RosterSpawnId,
            Source.ObjectId,
            Source.SpawnGeneration,
            ApplicationSequence,
            AppliedAt,
            ExpiresAt,
            NextPeriodicTickAt,
            EmittedPeriodicTicks,
            TargetWorldMembershipEpoch);
    }

    internal sealed class CharacterState(int characterId)
    {
        public int CharacterId { get; } = characterId;

        public Dictionary<MedusaEncounterEffectKind, ActiveEffectState>
            Effects { get; private set; } = [];

        public void RestoreEffects(
            Dictionary<MedusaEncounterEffectKind, ActiveEffectState>
                effects) => Effects = effects;
    }

    private readonly Dictionary<int, CharacterState> _charactersById;
    private readonly List<CharacterState> _orderedCharacters;
    private readonly Dictionary<uint, MonsterState> _monstersByObjectId;
    private DateTimeOffset _lastObservedAt;
    private ulong _nextApplicationSequence;
    private PeriodicDamageReservation? _pendingPeriodicDamage;

    public MedusaEncounterMechanicsRuntime(MedusaRunRuntime run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var source = run.Snapshot();
        if (source.State != MedusaRunState.Active)
        {
            throw new ArgumentException(
                "Medusa mechanics can only bind a newly active run.",
                nameof(run));
        }
        if (source.Spawns.Any(static spawn => spawn.Defeated))
        {
            throw new ArgumentException(
                "Medusa mechanics must bind before the first roster defeat.",
                nameof(run));
        }
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                source.Difficulty,
                out var difficulty) ||
            difficulty.ContentMapId != source.ContentMapId)
        {
            throw new ArgumentException(
                "The run difficulty and content map must remain explicit.",
                nameof(run));
        }

        WorldInstanceId = source.WorldInstanceId;
        Difficulty = source.Difficulty;
        ContentMapId = source.ContentMapId;
        StartedAt = source.StartedAt;
        Deadline = source.Deadline;
        _lastObservedAt = source.LastObservedAt;

        _orderedCharacters = source.AdmittedCharacterIds
            .Order()
            .Select(static id => new CharacterState(id))
            .ToList();
        _charactersById = _orderedCharacters.ToDictionary(
            static character => character.CharacterId);

        _monstersByObjectId = new(source.Spawns.Count);
        foreach (var spawn in source.Spawns)
        {
            if (!MedusaIslandRosterPolicy.TryGetSpawn(
                    spawn.RosterSpawnId,
                    out var rosterSpawn))
            {
                throw new ArgumentException(
                    "Every mechanics source must retain an authored roster ID.",
                    nameof(run));
            }

            MedusaEncounterEffectDefinition? effect = null;
            if (rosterSpawn.Skill is { } skill)
            {
                if (!MedusaEncounterMechanicsPolicy.TryGetEffectDefinition(
                        skill.Mechanic,
                        ContentMapId.Value,
                        out var resolved))
                {
                    throw new ArgumentException(
                        "Every authored mechanic must resolve on the run map.",
                        nameof(run));
                }
                effect = resolved;
            }

            _monstersByObjectId.Add(
                spawn.ObjectId,
                new MonsterState(spawn, effect));
        }
    }

    public WorldInstanceId WorldInstanceId { get; }

    public MedusaEncounterDifficulty Difficulty { get; }

    public MapId ContentMapId { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset Deadline { get; }

    internal DateTimeOffset OwnerLastObservedAt => _lastObservedAt;

    /// <summary>
    /// Purely validates a prospective committed hit. This lets the owning
    /// aggregate reject foreign or invalid identities before advancing either
    /// its run clock or mechanics clock.
    /// </summary>
    public MedusaMechanicHitOutcome PreviewMonsterHit(
        int targetCharacterId,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt) => PreviewMonsterHit(
        targetCharacterId,
        targetOwnership: CompatibilityOwnershipFor(sourceObjectId),
        targetLifeRevision: 0,
        sourceObjectId,
        sourceSpawnGeneration,
        committedAt);

    public MedusaMechanicHitOutcome PreviewMonsterHit(
        int targetCharacterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt) => PreviewMonsterHit(
        targetCharacterId,
        targetOwnership,
        targetLifeRevision,
        targetWorldMembershipEpoch:
            PureCompatibilityWorldMembershipEpoch,
        sourceObjectId,
        sourceSpawnGeneration,
        committedAt);

    public MedusaMechanicHitOutcome PreviewMonsterHit(
        int targetCharacterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt)
    {
        if (targetLifeRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetLifeRevision));
        }
        if (targetWorldMembershipEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWorldMembershipEpoch));
        }
        if (!_charactersById.TryGetValue(
                targetCharacterId,
                out var character))
        {
            return MedusaMechanicHitOutcome.CharacterNotAdmitted;
        }
        if (!_monstersByObjectId.TryGetValue(
                sourceObjectId,
                out var monster))
        {
            return MedusaMechanicHitOutcome.UnknownMonster;
        }
        if (monster.Spawn.SpawnGeneration != sourceSpawnGeneration)
        {
            return MedusaMechanicHitOutcome.StaleMonsterGeneration;
        }
        if (monster.Retired)
        {
            return MedusaMechanicHitOutcome.MonsterRetired;
        }

        var authoritativeAt = committedAt.ToUniversalTime();
        if (authoritativeAt < _lastObservedAt)
        {
            return MedusaMechanicHitOutcome.TimestampMovedBackward;
        }
        if (_pendingPeriodicDamage is not null)
        {
            return MedusaMechanicHitOutcome.PeriodicDamageRequired;
        }
        if (HasDuePeriodicDamage(authoritativeAt))
        {
            return MedusaMechanicHitOutcome.PeriodicDamageRequired;
        }
        if (monster.Effect is not { } definition)
        {
            return MedusaMechanicHitOutcome.MonsterHasNoAuthoredMechanic;
        }
        if (!TryAdd(
                authoritativeAt,
                definition.Duration,
                out _))
        {
            return MedusaMechanicHitOutcome.EffectWindowUnrepresentable;
        }
        if (_nextApplicationSequence == ulong.MaxValue)
        {
            return MedusaMechanicHitOutcome.ApplicationSequenceExhausted;
        }

        return character.Effects.TryGetValue(
                   definition.Kind,
                   out var current) &&
               current.TargetOwnership == targetOwnership &&
               current.TargetLifeRevision == targetLifeRevision &&
               current.TargetWorldMembershipEpoch ==
                   targetWorldMembershipEpoch &&
               authoritativeAt < current.ExpiresAt
            ? MedusaMechanicHitOutcome.Refreshed
            : MedusaMechanicHitOutcome.Applied;
    }

    public MedusaMechanicHitResult CommitMonsterHit(
        int targetCharacterId,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt) => CommitMonsterHit(
        targetCharacterId,
        targetOwnership: CompatibilityOwnershipFor(sourceObjectId),
        targetLifeRevision: 0,
        sourceObjectId,
        sourceSpawnGeneration,
        committedAt);

    public MedusaMechanicHitResult CommitMonsterHit(
        int targetCharacterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt) => CommitMonsterHit(
        targetCharacterId,
        targetOwnership,
        targetLifeRevision,
        targetWorldMembershipEpoch:
            PureCompatibilityWorldMembershipEpoch,
        sourceObjectId,
        sourceSpawnGeneration,
        committedAt);

    public MedusaMechanicHitResult CommitMonsterHit(
        int targetCharacterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt)
    {
        var reservation = ReserveMonsterHit(
            targetCharacterId,
            targetOwnership,
            targetLifeRevision,
            targetWorldMembershipEpoch,
            sourceObjectId,
            sourceSpawnGeneration,
            committedAt);
        if (reservation.Reservation is not { } prepared)
        {
            return RejectedHit(
                reservation.Outcome,
                reservation.PeriodicDamage);
        }

        return FinalizeReservedMonsterHit(prepared);
    }

    public MedusaPeriodicDamageReserveResult ClearCharacterLife(
        int characterId,
        long targetLifeRevision) => ClearCharacterLife(
        characterId,
        targetOwnership: default,
        targetLifeRevision,
        PureCompatibilityWorldMembershipEpoch,
        _lastObservedAt);

    public MedusaPeriodicDamageReserveResult ClearCharacterLife(
        int characterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision) => ClearCharacterLife(
        characterId,
        targetOwnership,
        targetLifeRevision,
        targetWorldMembershipEpoch:
            PureCompatibilityWorldMembershipEpoch,
        _lastObservedAt);

    public MedusaPeriodicDamageReserveResult ClearCharacterLife(
        int characterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch,
        DateTimeOffset observedAt)
    {
        if (targetLifeRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetLifeRevision));
        }
        if (targetWorldMembershipEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWorldMembershipEpoch));
        }
        var periodic = ReservePeriodicDamage(observedAt);
        if (periodic.Outcome != MedusaPeriodicDamageReserveOutcome.NoneDue)
        {
            return periodic;
        }
        AdvanceWithoutPeriodicDamage(observedAt.ToUniversalTime());
        if (!_charactersById.TryGetValue(characterId, out var character))
        {
            return periodic;
        }

        ClearCharacterLifeAtCurrentClock(
            character,
            targetOwnership,
            targetLifeRevision,
            targetWorldMembershipEpoch);
        return periodic;
    }

    private static void ClearCharacterLifeAtCurrentClock(
        CharacterState character,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch)
    {
        foreach (var kind in EffectKinds)
        {
            if (character.Effects.TryGetValue(kind, out var effect) &&
                effect.TargetOwnership == targetOwnership &&
                effect.TargetLifeRevision == targetLifeRevision &&
                effect.TargetWorldMembershipEpoch ==
                    targetWorldMembershipEpoch)
            {
                character.Effects.Remove(kind);
            }
        }
    }

    internal void ClearCharacterLifeAtCurrentClock(
        int characterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch)
    {
        if (_pendingPeriodicDamage is null &&
            _charactersById.TryGetValue(characterId, out var character))
        {
            ClearCharacterLifeAtCurrentClock(
                character,
                targetOwnership,
                targetLifeRevision,
                targetWorldMembershipEpoch);
        }
    }

    internal PeriodicDamageReservation? ClearAllEffectsAfterRunTerminal()
    {
        if (_pendingPeriodicDamage is { } pending)
        {
            return pending;
        }

        foreach (var character in _orderedCharacters)
        {
            character.Effects.Clear();
        }
        return null;
    }

    internal static PlayerOwnershipFence CompatibilityOwnership =>
        PureCompatibilityOwnership;

    private static PlayerOwnershipFence CompatibilityOwnershipFor(
        uint sourceObjectId) => PureCompatibilityOwnership;

}
