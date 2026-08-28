using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaRunState : byte
{
    Active = 1,
    Completed = 2,
    TimedOut = 3,
    VoluntarilyExited = 4
}

internal enum MedusaDefeatClaimOutcome : byte
{
    Applied = 1,
    Completed = 2,
    DuplicateDefeat = 3,
    UnknownSpawn = 4,
    StaleSpawnGeneration = 5,
    CharacterNotAdmitted = 6,
    TimestampMovedBackward = 7,
    TimedOut = 8,
    DeadlineBoundaryUnresolved = 9,
    RunNotActive = 10,
    InvariantFault = 11
}

internal enum MedusaDefeatClaimPreviewOutcome : byte
{
    Eligible = 1,
    DuplicateDefeat = 2,
    UnknownSpawn = 3,
    StaleSpawnGeneration = 4,
    CharacterNotAdmitted = 5,
    TimestampMovedBackward = 6,
    TimedOut = 7,
    DeadlineBoundaryUnresolved = 8,
    RunNotActive = 9
}

internal enum MedusaRunClockOutcome : byte
{
    Active = 1,
    TimedOut = 2,
    DeadlineBoundaryUnresolved = 3,
    TimestampMovedBackward = 4,
    RunNotActive = 5
}

internal enum MedusaRunAbandonOutcome : byte
{
    Exited = 1,
    CharacterNotAdmitted = 2,
    TimestampMovedBackward = 3,
    TimedOut = 4,
    DeadlineBoundaryUnresolved = 5,
    RunNotActive = 6
}

/// <summary>
/// Binds one authored roster slot to the identity of its one permitted live
/// spawn. The generation is fenced for the lifetime of the run: a later
/// generation is stale rather than a respawn.
/// </summary>
internal readonly record struct MedusaRunSpawnDefinition(
    string RosterSpawnId,
    uint ObjectId,
    uint SpawnGeneration,
    string TemplateKey,
    MedusaEncounterEnemyRole Role,
    MedusaMonsterRank Rank);

internal readonly record struct MedusaDefeatClaimResult(
    MedusaDefeatClaimOutcome Outcome,
    int ScoreAwarded,
    int TeamScore);

/// <summary>
/// In-memory evidence that both final bosses were defeated. The marker freezes
/// the score earned up to that point; it is not itself a durable reward grant.
/// </summary>
internal readonly record struct MedusaRunCompletionMarker(
    DateTimeOffset CompletedAt,
    TimeSpan Elapsed,
    int FinalScore,
    MedusaEncounterTitleAward? SelectedTitle);

internal readonly record struct MedusaRunSpawnSnapshot(
    string RosterSpawnId,
    uint ObjectId,
    uint SpawnGeneration,
    string TemplateKey,
    MedusaEncounterEnemyRole Role,
    MedusaMonsterRank Rank,
    int ScoreValue,
    bool Defeated);

internal sealed record MedusaRunSnapshot(
    WorldInstanceId WorldInstanceId,
    MedusaEncounterDifficulty Difficulty,
    MapId ContentMapId,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    DateTimeOffset LastObservedAt,
    MedusaRunState State,
    int TeamScore,
    IReadOnlyList<int> AdmittedCharacterIds,
    IReadOnlyList<MedusaRunSpawnSnapshot> Spawns,
    MedusaRunCompletionMarker? CompletionMarker);
