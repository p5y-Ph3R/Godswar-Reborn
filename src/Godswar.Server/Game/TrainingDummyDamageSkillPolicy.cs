using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.Game;

/// <summary>
/// Local-development policy for authoritative instantaneous damage skills
/// against exact training dummies. It is profession-neutral: admission is
/// derived from the process-pinned skill catalog and the attacker's class,
/// never from a hard-coded profession list.
/// </summary>
internal static class TrainingDummyDamageSkillPolicy
{
    public static TrainingDummySkillRejectionReason ValidateScalar(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill,
        byte attackerProfession) =>
        Validate(
            catalogs,
            skill,
            attackerProfession,
            SkillCombatResolver.IsHostileMonsterSingleTargetSkill);

    public static bool IsAuthoritativeScalar(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill) =>
        IsAuthoritative(
            catalogs,
            skill,
            SkillCombatResolver.IsHostileMonsterSingleTargetSkill);

    public static TrainingDummySkillRejectionReason ValidateArea(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill,
        byte attackerProfession) =>
        Validate(
            catalogs,
            skill,
            attackerProfession,
            SkillCombatResolver.IsHostileMonsterSelfAreaSkill);

    public static bool IsAuthoritativeArea(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill) =>
        IsAuthoritative(
            catalogs,
            skill,
            SkillCombatResolver.IsHostileMonsterSelfAreaSkill);

    public static bool IsSupportedScalar(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill,
        byte attackerProfession) =>
        ValidateScalar(catalogs, skill, attackerProfession) ==
        TrainingDummySkillRejectionReason.None;

    public static bool IsSupportedArea(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill,
        byte attackerProfession) =>
        ValidateArea(catalogs, skill, attackerProfession) ==
        TrainingDummySkillRejectionReason.None;

    public static PlayerCombatSkillSnapshot Snapshot(
        in SkillCombatDefinition skill) =>
        new(
            checked((uint)skill.SkillId),
            skill.Target,
            skill.AffectObj,
            skill.Distance,
            skill.Range,
            skill.Mp,
            skill.Property,
            skill.Power1,
            skill.Power2);

    public static string DisplayName(
        GameplayRuntimeCatalogs catalogs,
        int skillId)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var published = catalogs.Content.SkillCombatDefinitions
            .FirstOrDefault(value => value.SkillId == skillId);
        if (!string.IsNullOrWhiteSpace(published?.DisplayName))
        {
            return published.DisplayName;
        }
        if (!string.IsNullOrWhiteSpace(published?.BaseName))
        {
            return published.BaseName;
        }
        return $"skill {skillId}";
    }

    private static TrainingDummySkillRejectionReason Validate(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill,
        byte attackerProfession,
        Func<SkillCombatDefinition, bool> shapePredicate)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(shapePredicate);
        if (!IsAuthoritative(catalogs, skill, shapePredicate))
        {
            return TrainingDummySkillRejectionReason.UnsupportedSkill;
        }

        var skillId = skill.SkillId;
        var published = catalogs.Content.SkillCombatDefinitions
            .FirstOrDefault(value => value.SkillId == skillId);
        if (published is null ||
            published.ClassIds.Count == 0 ||
            !published.ClassIds.Contains(checked((short)attackerProfession)))
        {
            return TrainingDummySkillRejectionReason.
                AttackerProfessionMismatch;
        }

        return TrainingDummySkillRejectionReason.None;
    }

    private static bool IsAuthoritative(
        GameplayRuntimeCatalogs catalogs,
        in SkillCombatDefinition skill,
        Func<SkillCombatDefinition, bool> shapePredicate)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(shapePredicate);
        return catalogs.SkillCombat.TryGet(
                   skill.SkillId,
                   out var authoritative) &&
               authoritative == skill &&
               skill.CastTime == TimeSpan.Zero &&
               IsDamaging(skill) &&
               shapePredicate(skill);
    }

    private static bool IsDamaging(in SkillCombatDefinition skill) =>
        // Power1=-1 and Power2=0 is the stock status-only sentinel. A zero
        // Power1 still means one times post-defense attack and is damaging.
        skill.Power1 > -1m || skill.Power2 > 0m;
}
