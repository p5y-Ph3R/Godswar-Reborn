using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaMonsterBootstrapValidationOutcome : byte
{
    Prepared = 1,
    InvalidOwnership = 2,
    InvalidBindingCount = 3,
    InvalidBinding = 4,
    InvalidDefinitionCount = 5,
    DuplicateObjectId = 6,
    InvalidCapturedDefinition = 7,
    DefinitionSetMismatch = 8,
    MapMismatch = 9,
    SceneMismatch = 10,
    TemplateMismatch = 11,
    DisplayNameMismatch = 12,
    TierMismatch = 13,
    HealthMismatch = 14,
    PlacementNotCertified = 15,
    PlacementMismatch = 16,
    AmbientMismatch = 17
}

internal readonly record struct MedusaMonsterBootstrapPreparedSpawn(
    string RosterSpawnId,
    uint ObjectId,
    uint SpawnGeneration,
    MedusaEncounterEnemyRole Role,
    MedusaMonsterRank Rank,
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    uint Tier,
    uint MaximumHealth,
    float X,
    float Z,
    ImmutableArray<byte> Packet)
{
    public CapturedMonsterSpawn CreateCapturedDefinition() => new(
        MapId,
        SceneKey,
        TemplateKey,
        DisplayName,
        ObjectId,
        X,
        Z,
        Packet.ToArray());
}

internal readonly record struct MedusaMonsterBootstrapPreparedAmbientSpawn(
    string SpawnId,
    uint ObjectId,
    uint SpawnGeneration,
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    uint Tier,
    uint MaximumHealth,
    float X,
    float Z,
    ImmutableArray<byte> Packet)
{
    public CapturedMonsterSpawn CreateCapturedDefinition() => new(
        MapId,
        SceneKey,
        TemplateKey,
        DisplayName,
        ObjectId,
        X,
        Z,
        Packet.ToArray());
}

internal sealed record MedusaMonsterBootstrapPreparation(
    WorldInstanceId WorldInstanceId,
    MedusaEncounterDifficulty Difficulty,
    MapId ContentMapId,
    DateTimeOffset StartedAt,
    MonsterRespawnPolicy RespawnPolicy,
    string Fingerprint,
    ImmutableArray<MedusaMonsterBootstrapPreparedSpawn> Spawns,
    ImmutableArray<MedusaMonsterBootstrapPreparedAmbientSpawn> AmbientSpawns)
{
    public int RuntimeSpawnCount => Spawns.Length + AmbientSpawns.Length;

    public CapturedMonsterSpawn[] CreateCapturedDefinitions() =>
        Spawns.Select(static spawn => spawn.CreateCapturedDefinition())
            .Concat(AmbientSpawns.Select(static spawn =>
                spawn.CreateCapturedDefinition()))
            .ToArray();
}

internal readonly record struct MedusaMonsterBootstrapValidationResult(
    MedusaMonsterBootstrapValidationOutcome Outcome,
    string? RejectedSpawnId,
    MedusaMonsterBootstrapPreparation? Preparation)
{
    public bool IsPrepared =>
        Outcome == MedusaMonsterBootstrapValidationOutcome.Prepared &&
        Preparation is not null;
}
