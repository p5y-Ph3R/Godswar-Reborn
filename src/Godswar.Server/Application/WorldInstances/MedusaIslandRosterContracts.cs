using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

internal enum MedusaIslandRosterIsland : byte
{
    First = 1,
    Second = 2,
    Final = 3
}

internal enum MedusaIslandRosterLane : byte
{
    None = 0,
    Stun = 1,
    Freeze = 2,
    Bleed = 3
}

/// <summary>
/// Coordinate-free placement intent retained from gameplay design. These are
/// not world positions and must be resolved through a walkability-certified
/// placement pass before live spawning.
/// </summary>
internal enum MedusaIslandRosterAnchor : byte
{
    None = 0,
    FirstIslandTopLeft = 1,
    FirstIslandTopRight = 2
}

internal enum MedusaIslandRosterSpawnKind : byte
{
    Ordinary = 1,
    UtilityCarrier = 2,
    Elite = 3,
    Boss = 4
}

internal enum MedusaIslandRosterMechanic : byte
{
    Stun = 1,
    Freeze = 2,
    Bleed = 3,
    Shackle = 4,
    OutgoingPhysicalAmplifier = 5,
    OutgoingMagicalAmplifier = 6
}

internal enum MedusaIslandStatusApplicationRule : byte
{
    GuaranteedOnCommittedHit = 1,
    DeterministicRatingProcOnCommittedHit = 2
}

/// <summary>
/// An authored binding to stock Magic.ini and Status.ini content. The native
/// StatusOdds value is evidence only and is never interpreted as a percentage.
/// </summary>
internal readonly record struct MedusaIslandRosterSkillBinding(
    MedusaIslandRosterMechanic Mechanic,
    int SkillId,
    string AuthoredSkillLabel,
    uint StatusId,
    string AuthoredStatusLabel,
    int NativeStatusOddsRating,
    TimeSpan Duration,
    MedusaIslandStatusApplicationRule ApplicationRule,
    int OutgoingDamageMultiplier,
    ImmutableArray<short> NativeAffectedClientSceneIds)
{
    public bool UsesNativeStatusOddsAsProbability => false;

    public bool RequiresDeterministicRatingProc =>
        ApplicationRule == MedusaIslandStatusApplicationRule
            .DeterministicRatingProcOnCommittedHit;

    public bool HasNativeClientSceneRestriction =>
        !NativeAffectedClientSceneIds.IsDefaultOrEmpty;

    public bool CanUseUnmodifiedNativeStatusInClientScene(
        short clientSceneId) =>
        !HasNativeClientSceneRestriction ||
        NativeAffectedClientSceneIds.Contains(clientSceneId);
}

internal readonly record struct MedusaIslandRosterTemplatePair(
    string Alias,
    string DisplayName,
    MedusaMonsterRank Rank,
    short ClientAttackType,
    string EnhancedTemplateKey,
    string NormalTemplateKey);

internal readonly record struct MedusaIslandResolvedTemplate(
    short MapId,
    string SceneKey,
    string Alias,
    string TemplateKey,
    string DisplayName,
    MedusaMonsterRank Rank,
    short ClientAttackType);

internal sealed record MedusaIslandRosterSpawn(
    string SpawnId,
    int? EliteGroupId,
    MedusaIslandRosterIsland Island,
    MedusaIslandRosterLane Lane,
    MedusaIslandRosterSpawnKind Kind,
    MedusaEncounterEnemyRole EncounterRole,
    MedusaMonsterRank Rank,
    string TemplateAlias,
    MedusaIslandRosterSkillBinding? Skill,
    MedusaIslandRosterAnchor Anchor = MedusaIslandRosterAnchor.None);

internal sealed record MedusaIslandEliteGroup(
    int Id,
    MedusaIslandRosterIsland Island,
    MedusaIslandRosterLane Lane,
    string EliteSpawnId,
    ImmutableArray<string> OrdinaryEscortSpawnIds,
    string? AssociatedBossSpawnId);
