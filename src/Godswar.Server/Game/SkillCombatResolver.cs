using Godswar.Server.State;

namespace Godswar.Server.Game;

internal static class SkillCombatResolver
{
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
        const int targetSelf = 1;
        const int targetMonster = 8;

        // Meteor Blast and the other self-centred hostile area skills select the
        // caster (Target=TARGET_SELF) but collect monsters inside Range through
        // AffectObj. They therefore do not have a selected monster target.
        return (skill.Target & targetSelf) != 0 &&
               (skill.AffectObj & targetMonster) != 0 &&
               skill.Range > 0f;
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
        var stats = character.CalculatedStats ?? CharacterStats.FromCharacter(character);
        var usesMagicAttack = skill.Property == 1;
        var attack = usesMagicAttack ? stats.MagicAttack : stats.PhysicalAttack;
        var damageBonus = usesMagicAttack ? stats.MagicDamageBonus : stats.PhysicalDamageBonus;
        var appendDamage = usesMagicAttack ? stats.MagicAppendDamage : stats.PhysicalAppendDamage;

        // The original CalculateAttackDamage treats Power1 as an adjustment to
        // the full attack coefficient: (attack - defence) * (1 + Power1) +
        // Power2. Monster defence is not modeled yet, so use the full attack as
        // the pre-defence value while preserving that coefficient exactly.
        var attackCoefficient = Math.Max(0m, 1m + skill.Power1);
        var rawDamage = (attackCoefficient * Math.Max(0, attack)) + skill.Power2;
        rawDamage *= 1m + (Math.Max(0, damageBonus) / 10_000m);
        rawDamage += Math.Max(0, appendDamage);
        if (rawDamage <= 0)
        {
            return 0;
        }

        return (uint)Math.Clamp(
            decimal.ToInt64(decimal.Round(rawDamage, 0, MidpointRounding.AwayFromZero)),
            1L,
            uint.MaxValue);
    }
}
