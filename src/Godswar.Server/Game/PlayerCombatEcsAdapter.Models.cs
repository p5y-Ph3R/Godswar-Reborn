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
            Skill: default);

    public static PlayerCombatEcsRequest HostileSkill(
        PlayerCombatIntentKind kind,
        DateTimeOffset requestedAt,
        uint targetObjectId,
        in SkillCombatDefinition skill,
        uint? expectedTargetSpawnGeneration = null) =>
        new(
            kind,
            requestedAt,
            targetObjectId,
            ReportedAttackerX: 0f,
            ReportedAttackerZ: 0f,
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
    ImmutableArray<PlayerCombatEcsHit> Hits)
{
    public bool IntentAccepted =>
        RejectionReason == PlayerCombatRejectionReason.None;
}

internal readonly record struct PlayerCombatEcsProjectionDecision(
    bool Applied,
    ulong ProjectionId,
    MonsterKillProgressionRejectionReason RejectionReason);
