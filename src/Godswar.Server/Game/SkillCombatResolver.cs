using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal static class SkillCombatResolver
{
    // No stored capture proves hostile player-skill result flags, miss
    // encoding, area entries, or local/world target translation.
    internal const bool HostilePlayerSkillWireSupported = false;

    public static bool MustRejectHostilePlayerTarget(
        bool selectedTargetIsOtherPlayer) =>
        selectedTargetIsOtherPlayer &&
        !HostilePlayerSkillWireSupported;

    // Captured skill distance is measured to the target's collision boundary,
    // while runtime positions are object centers. This covers the largest normal
    // monster collision radius in the Sparta client definitions.
    internal const float TargetCollisionAllowance = 3f;

    public static bool IsHostileMonsterSkill(SkillCombatDefinition skill)
    {
        return IsHostileMonsterSingleTargetSkill(skill) ||
               IsHostileMonsterAreaSkill(skill);
    }

    public static bool IsHostileMonsterSingleTargetSkill(SkillCombatDefinition skill)
    {
        // TARGET_DIFF_FACTION | TARGET_MONSTER | TARGET_PK_OBJ
        return skill.Target == 44 && skill.AffectObj == 28 && skill.Range <= 0f;
    }

    public static bool IsHostileMonsterAreaSkill(SkillCombatDefinition skill)
    {
        return IsHostileMonsterSelfAreaSkill(skill) ||
               IsHostileMonsterGroundAreaSkill(skill);
    }

    public static bool IsHostileMonsterSelfAreaSkill(
        SkillCombatDefinition skill)
    {
        const int targetSelf = 1;
        const int targetMonster = 8;
        const int targetPosition = 16;

        // Meteor Blast and the other self-centred hostile area skills select the
        // caster (Target=TARGET_SELF) but collect monsters inside Range through
        // AffectObj. They therefore do not have a selected monster target.
        return (skill.Target & targetSelf) != 0 &&
               (skill.Target & targetPosition) == 0 &&
               (skill.AffectObj & targetMonster) != 0 &&
               skill.Range > 0f;
    }

    public static bool IsHostileMonsterGroundAreaSkill(
        SkillCombatDefinition skill)
    {
        const int targetPosition = 16;
        const int targetMonster = 8;

        // Mage ground spells carry TARGET_POSITION. Flame Blast combines that
        // flag with other accepted target flags (Target=63), so position must
        // take precedence over the self bit when choosing the area centre.
        return (skill.Target & targetPosition) != 0 &&
               (skill.AffectObj & targetMonster) != 0 &&
               skill.Distance > 0f &&
               skill.Range > 0f;
    }

    public static bool TryResolveHostileMonsterAreaCenter(
        float casterX,
        float casterZ,
        float reportedTargetX,
        float reportedTargetZ,
        SkillCombatDefinition skill,
        out float centerX,
        out float centerZ)
    {
        centerX = casterX;
        centerZ = casterZ;
        if (IsHostileMonsterSelfAreaSkill(skill))
        {
            return float.IsFinite(casterX) && float.IsFinite(casterZ);
        }

        if (!IsHostileMonsterGroundAreaSkill(skill) ||
            !IsWithinGroundTargetRange(
                casterX,
                casterZ,
                reportedTargetX,
                reportedTargetZ,
                skill))
        {
            return false;
        }

        centerX = reportedTargetX;
        centerZ = reportedTargetZ;
        return true;
    }

    public static bool IsWithinGroundTargetRange(
        float casterX,
        float casterZ,
        float targetX,
        float targetZ,
        SkillCombatDefinition skill)
    {
        if (!float.IsFinite(casterX) ||
            !float.IsFinite(casterZ) ||
            !float.IsFinite(targetX) ||
            !float.IsFinite(targetZ) ||
            !float.IsFinite(skill.Distance))
        {
            return false;
        }

        var allowedRange = Math.Max(0f, skill.Distance);
        var deltaX = (double)targetX - casterX;
        var deltaZ = (double)targetZ - casterZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ) <=
               allowedRange * allowedRange;
    }

    public static bool IsWithinRange(
        float casterX,
        float casterZ,
        float targetX,
        float targetZ,
        SkillCombatDefinition skill)
    {
        if (!float.IsFinite(casterX) ||
            !float.IsFinite(casterZ) ||
            !float.IsFinite(targetX) ||
            !float.IsFinite(targetZ))
        {
            return false;
        }

        var allowedRange = Math.Max(0f, skill.Distance) + TargetCollisionAllowance;
        var deltaX = (double)targetX - casterX;
        var deltaZ = (double)targetZ - casterZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ) <= allowedRange * allowedRange;
    }

    public static bool IsWithinArea(
        float centerX,
        float centerZ,
        float targetX,
        float targetZ,
        SkillCombatDefinition skill)
    {
        if (!float.IsFinite(centerX) ||
            !float.IsFinite(centerZ) ||
            !float.IsFinite(targetX) ||
            !float.IsFinite(targetZ) ||
            skill.Range <= 0f)
        {
            return false;
        }

        var deltaX = (double)targetX - centerX;
        var deltaZ = (double)targetZ - centerZ;
        var radius = (double)skill.Range;
        // The original Region::CollectGameObjectSphere uses a strict radius.
        return (deltaX * deltaX) + (deltaZ * deltaZ) < radius * radius;
    }

    public static uint CalculateDamage(GameCharacter character, SkillCombatDefinition skill)
    {
        var attacker = CombatCharacterStatsAdapter.FromCharacter(character);
        return AuthoredCombatPveCurrent.ResolveSkillDamageForOutcome(
            attacker,
            target: default,
            skill.Property,
            skill.Power1,
            skill.Power2,
            CombatHitOutcome.Normal).Damage;
    }

    public static CombatResolution ResolveDamage(
        GameCharacter character,
        SkillCombatDefinition skill,
        in CombatTargetStats target,
        ulong combatEventId,
        int targetOrder = 0,
        ClientStatusAggregate runtimeModifiers = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        var attacker =
            CombatCharacterStatsAdapter.ApplyRuntimeAttackerModifiers(
                CombatCharacterStatsAdapter.FromCharacter(character),
                runtimeModifiers);
        return AuthoredCombatPveCurrent.ResolveSkillDamage(
            attacker,
            target,
            skill.Property,
            skill.Power1,
            skill.Power2,
            combatEventId,
            targetOrder);
    }
}
