using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Deterministic, process-local Medusa run state. A MapInstance mailbox is
/// expected to be its sole owner; this type deliberately provides no locking.
/// It performs no persistence, reward grant, title grant, NPC admission, map
/// transfer, or monster mutation.
/// </summary>
internal sealed partial class MedusaRunRuntime
{
    internal sealed class SpawnState(
        MedusaRunSpawnDefinition definition,
        int scoreValue)
    {
        public MedusaRunSpawnDefinition Definition { get; } = definition;

        public int ScoreValue { get; } = scoreValue;

        public bool Defeated { get; set; }
    }

    private readonly HashSet<int> _admittedCharacters;
    private readonly List<int> _orderedAdmittedCharacters;
    private readonly Dictionary<uint, SpawnState> _spawnsByObjectId;
    private readonly SpawnState[] _orderedSpawns;
    private DateTimeOffset _lastObservedAt;
    private MedusaRunState _state = MedusaRunState.Active;
    private int _teamScore;
    private MedusaRunCompletionMarker? _completionMarker;

    public MedusaRunRuntime(
        WorldInstanceId worldInstanceId,
        MedusaEncounterDifficulty difficulty,
        IReadOnlyCollection<int> admittedCharacterIds,
        IReadOnlyCollection<MedusaRunSpawnDefinition> spawns,
        DateTimeOffset startedAt)
    {
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "A Medusa run requires an explicit world instance ID.",
                nameof(worldInstanceId));
        }
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var difficultyDefinition))
        {
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        }

        ArgumentNullException.ThrowIfNull(admittedCharacterIds);
        ArgumentNullException.ThrowIfNull(spawns);

        _orderedAdmittedCharacters = ValidateAndCopyCharacters(
            admittedCharacterIds).ToList();
        _admittedCharacters = [.. _orderedAdmittedCharacters];
        _orderedSpawns = ValidateAndCopySpawns(
            spawns,
            difficultyDefinition);
        _spawnsByObjectId = _orderedSpawns.ToDictionary(
            spawn => spawn.Definition.ObjectId);

        WorldInstanceId = worldInstanceId;
        Difficulty = difficulty;
        ContentMapId = difficultyDefinition.ContentMapId;
        StartedAt = startedAt.ToUniversalTime();
        try
        {
            Deadline = StartedAt.Add(MedusaIslandPolicy.TimeLimit);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startedAt),
                startedAt,
                "The Medusa deadline must fit in DateTimeOffset.");
        }

        _lastObservedAt = StartedAt;
    }

    public WorldInstanceId WorldInstanceId { get; }

    public MedusaEncounterDifficulty Difficulty { get; }

    public MapId ContentMapId { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset Deadline { get; }

    internal DateTimeOffset OwnerLastObservedAt => _lastObservedAt;

    internal MedusaRunState OwnerState => _state;

    internal int OwnerTeamScore => _teamScore;

    public MedusaDefeatClaimResult ClaimDefeat(
        int defeatedByCharacterId,
        uint objectId,
        uint spawnGeneration,
        DateTimeOffset occurredAt)
    {
        var authoritativeAt = occurredAt.ToUniversalTime();
        var preview = PreviewDefeatClaim(
            defeatedByCharacterId,
            objectId,
            spawnGeneration,
            authoritativeAt);
        if (preview != MedusaDefeatClaimPreviewOutcome.Eligible)
        {
            // Preserve the public claim contract: a valid identity observed
            // at/after the deadline advances (and, after it, terminalizes)
            // the authoritative run clock. The pure preview itself never
            // performs this mutation and is safe for pre-HP gating.
            if (preview is
                MedusaDefeatClaimPreviewOutcome.TimedOut or
                MedusaDefeatClaimPreviewOutcome
                    .DeadlineBoundaryUnresolved)
            {
                _ = AdvanceActiveClock(authoritativeAt);
            }

            return Result(ToClaimOutcome(preview));
        }

        var spawn = _spawnsByObjectId[objectId];

        var clockOutcome = AdvanceActiveClock(authoritativeAt);
        switch (clockOutcome)
        {
            case MedusaRunClockOutcome.TimestampMovedBackward:
                return Result(
                    MedusaDefeatClaimOutcome.TimestampMovedBackward);
            case MedusaRunClockOutcome.TimedOut:
                return Result(MedusaDefeatClaimOutcome.TimedOut);
            case MedusaRunClockOutcome.DeadlineBoundaryUnresolved:
                return Result(
                    MedusaDefeatClaimOutcome.DeadlineBoundaryUnresolved);
            case MedusaRunClockOutcome.RunNotActive:
                return Result(MedusaDefeatClaimOutcome.RunNotActive);
            case MedusaRunClockOutcome.Active:
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Medusa clock outcome {clockOutcome}.");
        }

        spawn.Defeated = true;
        var beforeScore = _teamScore;
        _teamScore = CappedScoreAfter(_teamScore, spawn.ScoreValue);
        var scoreAwarded = _teamScore - beforeScore;

        if (!FinalBossesDefeatedAfter(spawn))
        {
            return new(
                MedusaDefeatClaimOutcome.Applied,
                scoreAwarded,
                _teamScore);
        }

        var elapsed = authoritativeAt - StartedAt;
        MedusaEncounterTitleAward? title = null;
        if (MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                Difficulty,
                _teamScore,
                elapsed,
                out var resolvedTitle))
        {
            title = resolvedTitle;
        }

        _completionMarker = new(
            authoritativeAt,
            elapsed,
            _teamScore,
            title);
        _state = MedusaRunState.Completed;
        return new(
            MedusaDefeatClaimOutcome.Completed,
            scoreAwarded,
            _teamScore);
    }

    /// <summary>
    /// Pure eligibility check used by the owning map before it commits the
    /// corresponding lethal HP mutation. Rejected identities and clocks never
    /// mutate the run, which lets the owner prove that a later ClaimDefeat is
    /// accepted while it retains exclusive ownership.
    /// </summary>
    public MedusaDefeatClaimPreviewOutcome PreviewDefeatClaim(
        int defeatedByCharacterId,
        uint objectId,
        uint spawnGeneration,
        DateTimeOffset occurredAt)
    {
        // Rejected identities are observations from outside this run's
        // authority boundary. They must never advance or terminalize its
        // authoritative clock.
        if (!_admittedCharacters.Contains(defeatedByCharacterId))
        {
            return MedusaDefeatClaimPreviewOutcome.CharacterNotAdmitted;
        }
        if (!_spawnsByObjectId.TryGetValue(objectId, out var spawn))
        {
            return MedusaDefeatClaimPreviewOutcome.UnknownSpawn;
        }
        if (spawn.Definition.SpawnGeneration != spawnGeneration)
        {
            return MedusaDefeatClaimPreviewOutcome.StaleSpawnGeneration;
        }
        if (_state != MedusaRunState.Active)
        {
            return MedusaDefeatClaimPreviewOutcome.RunNotActive;
        }
        if (spawn.Defeated)
        {
            return MedusaDefeatClaimPreviewOutcome.DuplicateDefeat;
        }

        var authoritativeAt = occurredAt.ToUniversalTime();
        if (authoritativeAt < _lastObservedAt)
        {
            return MedusaDefeatClaimPreviewOutcome.TimestampMovedBackward;
        }
        if (authoritativeAt > Deadline)
        {
            return MedusaDefeatClaimPreviewOutcome.TimedOut;
        }
        if (authoritativeAt == Deadline)
        {
            return MedusaDefeatClaimPreviewOutcome
                .DeadlineBoundaryUnresolved;
        }

        return MedusaDefeatClaimPreviewOutcome.Eligible;
    }

    public MedusaRunClockOutcome ObserveTime(DateTimeOffset observedAt) =>
        AdvanceActiveClock(observedAt);

    /// <summary>
    /// Pure clock gate for a prospective owner transaction. The owning map
    /// uses this before monster HP mutation so deadline and backwards-time
    /// rejection cannot leave monster and run state divergent.
    /// </summary>
    public MedusaRunClockOutcome PreviewTime(DateTimeOffset observedAt)
    {
        observedAt = observedAt.ToUniversalTime();
        if (observedAt < _lastObservedAt)
        {
            return MedusaRunClockOutcome.TimestampMovedBackward;
        }
        if (_state != MedusaRunState.Active)
        {
            return MedusaRunClockOutcome.RunNotActive;
        }
        if (observedAt > Deadline)
        {
            return MedusaRunClockOutcome.TimedOut;
        }
        if (observedAt == Deadline)
        {
            return MedusaRunClockOutcome.DeadlineBoundaryUnresolved;
        }

        return MedusaRunClockOutcome.Active;
    }

    /// <summary>
    /// Explicitly abandons the whole run. A participant merely leaving the
    /// map must not call this method, because that would cancel the party's
    /// run; later ownership code must distinguish departure from abandonment.
    /// </summary>
    public MedusaRunAbandonOutcome AbandonRun(
        int requestedByCharacterId,
        DateTimeOffset abandonedAt)
    {
        // A foreign request is not an authoritative run-clock observation.
        if (!_admittedCharacters.Contains(requestedByCharacterId))
        {
            return MedusaRunAbandonOutcome.CharacterNotAdmitted;
        }

        var clockOutcome = AdvanceActiveClock(
            abandonedAt.ToUniversalTime());
        switch (clockOutcome)
        {
            case MedusaRunClockOutcome.TimestampMovedBackward:
                return MedusaRunAbandonOutcome.TimestampMovedBackward;
            case MedusaRunClockOutcome.TimedOut:
                return MedusaRunAbandonOutcome.TimedOut;
            case MedusaRunClockOutcome.DeadlineBoundaryUnresolved:
                return MedusaRunAbandonOutcome.DeadlineBoundaryUnresolved;
            case MedusaRunClockOutcome.RunNotActive:
                return MedusaRunAbandonOutcome.RunNotActive;
            case MedusaRunClockOutcome.Active:
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Medusa clock outcome {clockOutcome}.");
        }

        _state = MedusaRunState.VoluntarilyExited;
        _completionMarker = null;
        return MedusaRunAbandonOutcome.Exited;
    }

    public MedusaRunSnapshot Snapshot()
    {
        var admitted = Array.AsReadOnly(
            _orderedAdmittedCharacters.ToArray());
        var spawns = Array.AsReadOnly(
            _orderedSpawns.Select(spawn => new MedusaRunSpawnSnapshot(
                    spawn.Definition.RosterSpawnId,
                    spawn.Definition.ObjectId,
                    spawn.Definition.SpawnGeneration,
                    spawn.Definition.TemplateKey,
                    spawn.Definition.Role,
                    spawn.Definition.Rank,
                    spawn.ScoreValue,
                    spawn.Defeated))
                .ToArray());

        return new(
            WorldInstanceId,
            Difficulty,
            ContentMapId,
            StartedAt,
            Deadline,
            _lastObservedAt,
            _state,
            _teamScore,
            admitted,
            spawns,
            _completionMarker);
    }

    public bool IsCharacterAdmitted(int characterId) =>
        _admittedCharacters.Contains(characterId);

    private MedusaRunClockOutcome AdvanceActiveClock(
        DateTimeOffset observedAt)
    {
        observedAt = observedAt.ToUniversalTime();
        var preview = PreviewTime(observedAt);
        if (preview is MedusaRunClockOutcome.TimestampMovedBackward or
            MedusaRunClockOutcome.RunNotActive)
        {
            return preview;
        }

        _lastObservedAt = observedAt;
        if (preview == MedusaRunClockOutcome.TimedOut)
        {
            _state = MedusaRunState.TimedOut;
            _completionMarker = null;
            return MedusaRunClockOutcome.TimedOut;
        }
        if (preview ==
            MedusaRunClockOutcome.DeadlineBoundaryUnresolved)
        {
            return MedusaRunClockOutcome.DeadlineBoundaryUnresolved;
        }

        return MedusaRunClockOutcome.Active;
    }

    private MedusaDefeatClaimResult Result(
        MedusaDefeatClaimOutcome outcome) =>
        new(outcome, ScoreAwarded: 0, _teamScore);

    private bool FinalBossesDefeatedAfter(SpawnState currentDefeat) =>
        RoleIsDefeatedAfter(
            MedusaEncounterEnemyRole.Stheno,
            currentDefeat) &&
        RoleIsDefeatedAfter(
            MedusaEncounterEnemyRole.Medusa,
            currentDefeat);

    private bool RoleIsDefeatedAfter(
        MedusaEncounterEnemyRole role,
        SpawnState currentDefeat) =>
        _orderedSpawns.Any(spawn =>
            spawn.Definition.Role == role &&
            (spawn.Defeated || ReferenceEquals(spawn, currentDefeat)));

    private static MedusaDefeatClaimOutcome ToClaimOutcome(
        MedusaDefeatClaimPreviewOutcome outcome) => outcome switch
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
            MedusaDefeatClaimPreviewOutcome.TimedOut =>
                MedusaDefeatClaimOutcome.TimedOut,
            MedusaDefeatClaimPreviewOutcome
                .DeadlineBoundaryUnresolved =>
                MedusaDefeatClaimOutcome.DeadlineBoundaryUnresolved,
            MedusaDefeatClaimPreviewOutcome.RunNotActive =>
                MedusaDefeatClaimOutcome.RunNotActive,
            _ => throw new InvalidOperationException(
                $"Preview outcome {outcome} is not a rejection.")
        };

}
