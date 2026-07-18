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
        return skill.Target == 44 && skill.AffectObj == 28;
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

    public static uint CalculateDamage(GameCharacter character, SkillCombatDefinition skill)
    {
        var stats = character.CalculatedStats ?? CharacterStats.FromCharacter(character);
        var usesMagicAttack = skill.Property == 1;
        var attack = usesMagicAttack ? stats.MagicAttack : stats.PhysicalAttack;
        var damageBonus = usesMagicAttack ? stats.MagicDamageBonus : stats.PhysicalDamageBonus;
        var appendDamage = usesMagicAttack ? stats.MagicAppendDamage : stats.PhysicalAppendDamage;

        var rawDamage = (Math.Abs(skill.Power1) * Math.Max(0, attack)) + skill.Power2;
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
