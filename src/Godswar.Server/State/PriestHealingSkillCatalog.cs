namespace Godswar.Server.State;

internal enum PriestHealingSkillKind
{
    SingleTarget = 1,
    Area = 2
}

internal readonly record struct PriestHealingSkillDefinition(
    int SkillId,
    PriestHealingSkillKind Kind,
    int HealAmount);

/// <summary>
/// Recognizes the native Priest Heal and Area Heal families only when their
/// published combat data still describes a friendly healing operation.
/// Power2 is the server-owned heal amount copied from Magic.ini.
/// </summary>
internal static class PriestHealingSkillCatalog
{
    private const int FriendlyTargetMask = 3;
    private const int HealingProperty = 2;
    private const decimal HealingPowerCoefficient = -1m;

    public static bool TryResolve(
        SkillCombatDefinition combat,
        out PriestHealingSkillDefinition definition)
    {
        definition = default;
        if (!TryGetKind(combat.SkillId, out var kind) ||
            combat.AffectObj != FriendlyTargetMask ||
            combat.Property != HealingProperty ||
            combat.Power1 != HealingPowerCoefficient ||
            !TryResolveHealAmount(combat.Power2, out var healAmount) ||
            !HasExpectedTargetShape(combat, kind))
        {
            return false;
        }

        definition = new PriestHealingSkillDefinition(
            combat.SkillId,
            kind,
            healAmount);
        return true;
    }

    private static bool TryGetKind(
        int skillId,
        out PriestHealingSkillKind kind)
    {
        if (skillId is >= 750 and <= 754)
        {
            kind = PriestHealingSkillKind.SingleTarget;
            return true;
        }

        if (skillId is >= 760 and <= 764)
        {
            kind = PriestHealingSkillKind.Area;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool HasExpectedTargetShape(
        SkillCombatDefinition combat,
        PriestHealingSkillKind kind) =>
        kind switch
        {
            PriestHealingSkillKind.SingleTarget =>
                combat.Target == 3 &&
                combat.Distance == 11f &&
                combat.Range == 0f,
            PriestHealingSkillKind.Area =>
                combat.Target == 1 &&
                combat.Distance == 0f &&
                combat.Range == 12f,
            _ => false
        };

    private static bool TryResolveHealAmount(
        decimal power2,
        out int healAmount)
    {
        healAmount = 0;
        if (power2 <= 0m ||
            power2 > int.MaxValue ||
            power2 != decimal.Truncate(power2))
        {
            return false;
        }

        healAmount = decimal.ToInt32(power2);
        return true;
    }
}
