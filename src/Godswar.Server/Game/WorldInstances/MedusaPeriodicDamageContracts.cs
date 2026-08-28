using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Process-local, exact identity for one authored periodic damage unit. The
/// identity is stable before player HP is touched and is never inferred from
/// a later character or monster snapshot.
/// </summary>
internal readonly record struct MedusaPeriodicDamageIdentity(
    WorldInstanceId WorldInstanceId,
    int TargetCharacterId,
    PlayerOwnershipFence TargetOwnership,
    long TargetLifeRevision,
    long TargetWorldMembershipEpoch,
    string SourceRosterSpawnId,
    uint SourceObjectId,
    uint SourceSpawnGeneration,
    ulong ApplicationSequence,
    int TickNumber,
    DateTimeOffset DueAt,
    MedusaPeriodicDamageKind DamageKind,
    uint Damage)
{
    public bool IsValid =>
        WorldInstanceId.IsValid &&
        TargetCharacterId > 0 &&
        TargetOwnership.IsValid &&
        TargetLifeRevision >= 0 &&
        TargetWorldMembershipEpoch > 0 &&
        !string.IsNullOrWhiteSpace(SourceRosterSpawnId) &&
        SourceObjectId > 0 &&
        SourceSpawnGeneration > 0 &&
        ApplicationSequence > 0 &&
        TickNumber > 0 &&
        DueAt != default &&
        DueAt.Offset == TimeSpan.Zero &&
        DamageKind == MedusaPeriodicDamageKind.DirectHealthLoss &&
        Damage > 0;
}

internal enum MedusaPeriodicDamageReserveOutcome : byte
{
    NoneDue = 1,
    Reserved = 2,
    TimestampMovedBackward = 3,
    DeadlineBoundaryUnresolved = 4,
    InvariantFault = 5
}

internal enum MedusaPeriodicDamageDispositionOutcome : byte
{
    Applied = 1,
    Terminal = 2,
    Canceled = 3,
    AlreadyCompleted = 4,
    ForeignReservation = 5,
    InvariantFault = 6
}

internal readonly record struct MedusaPeriodicDamageReserveResult(
    MedusaPeriodicDamageReserveOutcome Outcome,
    MedusaEncounterMechanicsRuntime.PeriodicDamageReservation? Reservation);
