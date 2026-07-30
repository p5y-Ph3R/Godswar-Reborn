using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct MonsterCombatTarget(
    int CharacterId,
    float X,
    float Z,
    bool IsAlive,
    uint ObjectId = 0,
    long LifeRevision = 0);

internal enum MonsterCombatPhase
{
    None,
    Chasing,
    Attacking,
    Returning,
    AwaitingRetirement
}

internal sealed record MonsterRuntimeSnapshot(
    CapturedMonsterSpawn Definition,
    float HomeX,
    float HomeZ,
    float X,
    float Y,
    float Z,
    float Facing,
    uint CurrentHealth,
    uint MaximumHealth,
    bool IsAlive,
    bool IsSpawned,
    bool IsMoving,
    float VelocityX,
    float VelocityY,
    float VelocityZ,
    uint MovementTicks,
    uint RemainingMovementTicks,
    DateTimeOffset NextMovementAt,
    DateTimeOffset? DespawnAt,
    DateTimeOffset? RespawnAt,
    MonsterCombatPhase CombatPhase,
    DateTimeOffset? StunnedUntil,
    uint SpawnGeneration,
    ulong HealthRevision,
    Guid RuntimeInstanceId)
{
    public uint ObjectId => Definition.ObjectId;

    public bool IsStunned => StunnedUntil is not null;

    public MonsterAppearanceVersion AppearanceVersion => new(
        SpawnGeneration,
        HealthRevision);

    public CapturedMonsterAppearanceState Appearance => new(
        Definition,
        X,
        Z,
        Facing,
        CurrentHealth,
        MaximumHealth);
}

internal static class MonsterRuntimeIdentity
{
    // Direct runtime construction is used by deterministic parity tests. The
    // production factory supplies a fresh per-map UUID; this process UUID is
    // the safe fallback for direct construction.
    public static Guid ProcessInstanceId { get; } = Guid.NewGuid();

    public static Guid Resolve(Guid? runtimeInstanceId)
    {
        var resolved = runtimeInstanceId ?? ProcessInstanceId;
        if (resolved == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeInstanceId),
                "A monster runtime identity cannot be empty.");
        }

        return resolved;
    }
}

internal enum MonsterRuntimeUpdateKind
{
    Started,
    Arrived,
    Attacked,
    Returned,
    Died,
    Despawned,
    Respawned
}

internal sealed record MonsterRuntimeUpdate(
    MonsterRuntimeUpdateKind Kind,
    MonsterRuntimeSnapshot Monster,
    uint MovementMode = 1,
    uint? MovementEndField = null,
    int? TargetCharacterId = null,
    float TargetX = 0,
    float TargetZ = 0,
    uint? TargetObjectId = null,
    long? TargetLifeRevision = null,
    long? TargetVitalsRevision = null,
    ulong AttackEventId = 0);

internal sealed record MonsterRuntimeTick(
    bool PositionsChanged,
    IReadOnlyList<MonsterRuntimeUpdate> Updates);

internal sealed record MonsterDamageResult(
    uint ObjectId,
    uint BeforeHealth,
    uint AfterHealth,
    bool Killed,
    MonsterRuntimeSnapshot Monster,
    MonsterHealthMutation? HealthMutation);

internal readonly record struct MonsterAppearanceVersion(
    uint SpawnGeneration,
    ulong HealthRevision);

internal readonly record struct MonsterHealthMutation(
    uint ObjectId,
    uint SpawnGeneration,
    ulong BeforeHealthRevision,
    ulong AfterHealthRevision)
{
    public MonsterAppearanceVersion BeforeVersion => new(
        SpawnGeneration,
        BeforeHealthRevision);

    public MonsterAppearanceVersion AfterVersion => new(
        SpawnGeneration,
        AfterHealthRevision);
}

internal sealed record MonsterStunResult(
    uint ObjectId,
    bool Applied,
    DateTimeOffset? StunnedUntil,
    MonsterRuntimeSnapshot Monster);
