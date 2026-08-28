using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaPlayerMonsterDamageOutcome : byte
{
    AppliedUnbound = 1,
    AppliedMedusa = 2,
    InvalidResolution = 3,
    AttachmentStateConflict = 4,
    CharacterNotAdmitted = 5,
    UnknownMonster = 6,
    StaleMonsterGeneration = 7,
    StaleHealthRevision = 8,
    RunNotActive = 9,
    TimestampMovedBackward = 10,
    DeadlineBoundaryUnresolved = 11,
    TimedOut = 12,
    DuplicateDefeat = 13,
    RuntimeRejected = 14,
    DefeatPreflightRejected = 15,
    PeriodicDamageHandoffUnavailable = 16,
    CurrentMembershipRequired = 17,
    OwnerClockInvariantFault = 18
}

internal readonly record struct PlayerMonsterCombatAuthority(
    WorldInstanceId WorldInstanceId,
    long WorldRevision,
    PlayerOwnershipFence Ownership,
    long LifeRevision,
    long WorldMembershipEpoch)
{
    public bool IsValid =>
        WorldInstanceId.IsValid &&
        WorldRevision >= 0 &&
        LifeRevision >= 0 &&
        WorldMembershipEpoch > 0;
}

internal readonly record struct MedusaOwnedDefeatPreview(
    MedusaDefeatClaimPreviewOutcome RunOutcome,
    MedusaMechanicSourceRetireOutcome MechanicsOutcome,
    bool HasDuePeriodicDamage)
{
    public bool IsEligible =>
        RunOutcome == MedusaDefeatClaimPreviewOutcome.Eligible &&
        MechanicsOutcome == MedusaMechanicSourceRetireOutcome.Retired &&
        !HasDuePeriodicDamage;
}

/// <summary>
/// One authoritative player-to-monster mutation. Resolution is the exact
/// typed damage committed to HP after owner-bound amplifiers and boss
/// reduction. Defeat is present only for the same transaction's lethal hit.
/// </summary>
internal readonly record struct MedusaPlayerMonsterDamageCommit(
    MedusaPlayerMonsterDamageOutcome Outcome,
    CombatResolution Resolution,
    MonsterDamageResult? DamageResult,
    MedusaOwnedDefeatResult? Defeat)
{
    public bool Applied => Outcome is
        MedusaPlayerMonsterDamageOutcome.AppliedUnbound or
        MedusaPlayerMonsterDamageOutcome.AppliedMedusa;

    public bool IsMedusaOwned =>
        Outcome == MedusaPlayerMonsterDamageOutcome.AppliedMedusa;
}
