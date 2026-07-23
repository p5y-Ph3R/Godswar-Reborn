using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct PlayerCombatIntentRejectedEvent(
    ulong Sequence,
    EntityId Player,
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    PlayerCombatRejectionReason Reason,
    uint TargetObjectId,
    int CurrentMana,
    DateTimeOffset NextBasicAttackAt);

internal readonly record struct PlayerCombatResourceReservedEvent(
    ulong Sequence,
    EntityId Player,
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    int ReservedMana,
    int CurrentMana,
    long VitalsRevision,
    DateTimeOffset NextBasicAttackAt,
    ulong CombatRevision,
    int TargetCount);

internal readonly record struct PlayerCombatDamageIntentEvent(
    ulong Sequence,
    EntityId Player,
    int CharacterId,
    uint AttackerObjectId,
    byte MapId,
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    uint SkillId,
    int TargetOrder,
    int TargetCount,
    uint TargetObjectId,
    uint RequestedDamage,
    uint ExpectedSpawnGeneration,
    ulong ExpectedHealthRevision);

internal readonly record struct PlayerCombatMutationOutcomeIgnoredEvent(
    ulong Sequence,
    EntityId Player,
    ulong IntentId,
    uint TargetObjectId,
    PlayerCombatMutationRejectionReason Reason);

internal readonly record struct PlayerCombatTargetMutationRejectedEvent(
    ulong Sequence,
    EntityId Player,
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    uint TargetObjectId,
    uint ExpectedSpawnGeneration,
    ulong ExpectedHealthRevision,
    PlayerCombatMutationRejectionReason Reason,
    bool RefundEligible);

internal readonly record struct PlayerCombatResourceRefundedEvent(
    ulong Sequence,
    EntityId Player,
    ulong IntentId,
    int RefundedMana,
    int CurrentMana,
    long VitalsRevision,
    DateTimeOffset NextBasicAttackAt,
    ulong CombatRevision);

internal readonly record struct PlayerCombatTargetMutationCommittedEvent(
    ulong Sequence,
    EntityId Player,
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    uint SkillId,
    int TargetOrder,
    uint TargetObjectId,
    uint ReportedDamage,
    uint AppliedDamage,
    uint BeforeHealth,
    uint AfterHealth,
    uint SpawnGeneration,
    ulong BeforeHealthRevision,
    ulong AfterHealthRevision,
    bool Killed);

internal readonly record struct MonsterKilledByPlayerCombatEvent(
    ulong Sequence,
    EntityId Player,
    int CharacterId,
    ulong CombatIntentId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    ulong MonsterHealthRevision);

internal readonly record struct PlayerCombatReservationCompletedEvent(
    ulong Sequence,
    EntityId Player,
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    int AcceptedTargetCount,
    int RejectedTargetCount,
    bool ResourcesRefunded);

internal readonly record struct MonsterKillProgressionRejectedEvent(
    ulong Sequence,
    EntityId Player,
    ulong ProjectionId,
    ulong CombatIntentId,
    uint MonsterObjectId,
    MonsterKillProgressionRejectionReason Reason);

internal enum MonsterKillProgressionRejectionReason : byte
{
    None = 0,
    ProjectionOutOfOrder = 1,
    ProgressionRevisionMismatch = 2,
    KillGuardMissing = 3
}

internal readonly record struct MonsterKillLevelUpProjectedEvent(
    ulong Sequence,
    int ProjectionOrder,
    EntityId Player,
    ulong ProjectionId,
    int Level,
    int CurrentExperience,
    int NextLevelExperience);

internal readonly record struct MonsterKillExperienceProjectedEvent(
    ulong Sequence,
    int ProjectionOrder,
    EntityId Player,
    ulong ProjectionId,
    int ExperienceGained,
    int CurrentExperience,
    int NextLevelExperience);

internal readonly record struct MonsterKillTalentExperienceProjectedEvent(
    ulong Sequence,
    int ProjectionOrder,
    EntityId Player,
    ulong ProjectionId,
    int TalentExperienceGained,
    int CurrentTalentExperience);

internal readonly record struct MonsterDeathProgressionProjectedEvent(
    ulong Sequence,
    int ProjectionOrder,
    EntityId Player,
    ulong ProjectionId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    int CurrentExperience,
    int CurrentTalentExperience,
    int CurrentTalentPoints);

internal readonly record struct MonsterKillTalentPointsProjectedEvent(
    ulong Sequence,
    int ProjectionOrder,
    EntityId Player,
    ulong ProjectionId,
    int TalentPointsGained,
    int CurrentTalentPoints);

internal readonly record struct MonsterKillProgressionAppliedEvent(
    ulong Sequence,
    int ProjectionOrder,
    EntityId Player,
    ulong ProjectionId,
    int PreviousLevel,
    int CurrentLevel,
    long ProgressionRevision);
