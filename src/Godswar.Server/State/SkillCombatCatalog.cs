using System.Collections.Frozen;

namespace Godswar.Server.State;

/// <summary>
/// Server-owned combat values copied by GenerateSkillTalentTemplates.ps1 from
/// Localization/en_us/Settings/Sys/Magic.ini. Property names intentionally match
/// the source fields so packet/combat work can be checked against the client data.
/// </summary>
internal readonly record struct SkillCombatDefinition(
    int SkillId,
    int Target,
    int AffectObj,
    float Distance,
    float Range,
    int Property,
    int Mp,
    decimal Power1,
    decimal Power2);

internal static class SkillCombatCatalog
{
    private static readonly FrozenDictionary<int, SkillCombatDefinition> Definitions =
        SkillTalentSeeds.Skills.ToFrozenDictionary(
            skill => skill.SkillId,
            skill => new SkillCombatDefinition(
                skill.SkillId,
                skill.Target,
                skill.AffectObj,
                (float)skill.Distance,
                (float)skill.Range,
                skill.Property,
                skill.Mp,
                skill.Power1,
                skill.Power2));

    public static int Count => Definitions.Count;

    public static bool TryGet(int skillId, out SkillCombatDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);
}
