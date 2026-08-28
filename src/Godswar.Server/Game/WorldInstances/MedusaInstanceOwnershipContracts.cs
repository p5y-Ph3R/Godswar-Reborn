using System.Collections.Immutable;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaInstanceBindOutcome : byte
{
    Bound = 1,
    AlreadyBound = 2,
    LifecycleNotCreating = 3,
    WrongInstanceKind = 4,
    UnknownDifficulty = 5,
    ContentMapMismatch = 6,
    RuntimeNotEmpty = 7,
    MonsterRuntimeAlreadyInitialized = 8,
    InvalidRunDefinition = 9,
    AdmittedRosterExceedsPlayerCapacity = 10
}

internal enum MedusaOwnedOperationGateOutcome : byte
{
    Delegated = 1,
    RunNotActive = 2,
    TimestampMovedBackward = 3,
    DeadlineBoundaryUnresolved = 4,
    TimedOut = 5,
    PeriodicDamageRequired = 6,
    InvariantFault = 7
}

internal readonly record struct MedusaOwnedMonsterIdentity(
    uint ObjectId,
    uint SpawnGeneration);

/// <summary>
/// Immutable identity metadata retained by the owning map instance. Difficulty
/// is explicit because Enhanced and Mythic intentionally share content map 200.
/// </summary>
internal readonly record struct MedusaOwnedMonsterBinding(
    MedusaOwnedMonsterIdentity Identity,
    string RosterSpawnId,
    string TemplateKey,
    MedusaEncounterEnemyRole Role,
    MedusaMonsterRank Rank,
    MedusaEncounterDifficulty Difficulty,
    MapId ContentMapId);

internal sealed record MedusaInstanceOwnershipSnapshot(
    WorldInstanceId WorldInstanceId,
    MedusaEncounterDifficulty Difficulty,
    MapId ContentMapId,
    ImmutableArray<MedusaOwnedMonsterBinding> MonsterBindings,
    MedusaRunSnapshot Run,
    MedusaEncounterMechanicsSnapshot Mechanics);

internal readonly record struct MedusaInstanceBindResult(
    MedusaInstanceBindOutcome Outcome,
    MedusaInstanceOwnershipSnapshot? Snapshot)
{
    public bool IsBound => Outcome == MedusaInstanceBindOutcome.Bound;
}

internal readonly record struct MedusaOwnedDefeatResult(
    MedusaOwnedOperationGateOutcome GateOutcome,
    MedusaDefeatClaimResult? Claim,
    MedusaMechanicSourceRetireResult? SourceRetirement,
    MedusaMechanicsClockResult? MechanicsClockResult)
{
    public MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
        PeriodicDamage { get; init; }
}

internal readonly record struct MedusaOwnedMechanicHitResult(
    MedusaOwnedOperationGateOutcome GateOutcome,
    MedusaRunClockOutcome? RunClockOutcome,
    MedusaMechanicsClockResult? MechanicsClockResult,
    MedusaMechanicHitResult? MechanicsResult);

internal readonly record struct MedusaOwnedClockResult(
    MedusaOwnedOperationGateOutcome GateOutcome,
    MedusaRunClockOutcome? RunOutcome,
    MedusaMechanicsClockResult? MechanicsResult);

internal readonly record struct MedusaOwnedOutgoingDamageResult(
    MedusaOwnedOperationGateOutcome GateOutcome,
    MedusaOutgoingDamageResult? MechanicsResult);

internal readonly record struct MedusaOwnedAbandonResult(
    MedusaOwnedOperationGateOutcome GateOutcome,
    MedusaRunAbandonOutcome? RunOutcome,
    MedusaMechanicsClockResult? MechanicsClockResult)
{
    public MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
        PeriodicDamage { get; init; }
}
