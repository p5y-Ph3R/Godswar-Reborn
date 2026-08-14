using System.Collections.Immutable;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct PlayerCombatEcsRequest(
    PlayerCombatIntentKind Kind,
    DateTimeOffset RequestedAt,
    uint TargetObjectId,
    float ReportedAttackerX,
    float ReportedAttackerZ,
    bool HasReportedTargetPosition,
    float ReportedTargetX,
    float ReportedTargetZ,
    PlayerCombatSkillSnapshot Skill,
    uint? ExpectedTargetSpawnGeneration = null)
{
    public static PlayerCombatEcsRequest BasicAttack(
        DateTimeOffset requestedAt,
        uint targetObjectId,
        float reportedAttackerX,
        float reportedAttackerZ) =>
        new(
            PlayerCombatIntentKind.BasicAttack,
            requestedAt,
            targetObjectId,
            reportedAttackerX,
            reportedAttackerZ,
            HasReportedTargetPosition: false,
            ReportedTargetX: float.NaN,
            ReportedTargetZ: float.NaN,
            Skill: default);

    public static PlayerCombatEcsRequest HostileSkill(
        PlayerCombatIntentKind kind,
        DateTimeOffset requestedAt,
        uint targetObjectId,
        in SkillCombatDefinition skill,
        uint? expectedTargetSpawnGeneration = null,
        bool hasTargetPosition = false,
        float areaCenterX = 0f,
        float areaCenterZ = 0f) =>
        new(
            kind,
            requestedAt,
            targetObjectId,
            ReportedAttackerX: 0f,
            ReportedAttackerZ: 0f,
            HasReportedTargetPosition: hasTargetPosition,
            ReportedTargetX: areaCenterX,
            ReportedTargetZ: areaCenterZ,
            new PlayerCombatSkillSnapshot(
                checked((uint)skill.SkillId),
                skill.Target,
                skill.AffectObj,
                skill.Distance,
                skill.Range,
                skill.Mp,
                skill.Property,
                skill.Power1,
                skill.Power2),
            expectedTargetSpawnGeneration);
}

internal sealed record PlayerCombatEcsHit(
    MonsterDamageResult Result,
    uint ReportedDamage,
    PlayerCombatKillGuard? KillGuard);

internal readonly record struct PlayerCombatEcsResolvedTarget(
    uint TargetObjectId,
    uint SpawnGeneration,
    ulong HealthRevision,
    CombatResolution Resolution);

internal sealed record PlayerCombatEcsDecision(
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    PlayerCombatRejectionReason RejectionReason,
    PlayerCombatMutationRejectionReason MutationRejectionReason,
    int SelectedTargetCount,
    int AcceptedTargetCount,
    int RejectedTargetCount,
    int ReservedMana,
    bool ResourcesRefunded,
    int CurrentMana,
    long VitalsRevision,
    DateTimeOffset NextBasicAttackAt,
    ImmutableArray<PlayerCombatEcsHit> Hits,
    ImmutableArray<PlayerCombatEcsResolvedTarget> Resolutions)
{
    public bool IntentAccepted =>
        RejectionReason == PlayerCombatRejectionReason.None;

    public PlayerCombatEcsResolvedTarget? BasicAttackResolution =>
        Kind == PlayerCombatIntentKind.BasicAttack &&
        Resolutions is [var resolution]
            ? resolution
            : null;
}

internal readonly record struct PlayerCombatEcsProjectionDecision(
    bool Applied,
    ulong ProjectionId,
    MonsterKillProgressionRejectionReason RejectionReason);
