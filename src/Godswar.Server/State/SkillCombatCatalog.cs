using System.Collections.Frozen;
using Godswar.Server.Application.World;

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
    decimal Power2,
    TimeSpan CastTime = default,
    TimeSpan Cooldown = default);

internal sealed class SkillCombatCatalog
{
    private readonly FrozenDictionary<int, SkillCombatDefinition> _definitions;

    private SkillCombatCatalog(
        FrozenDictionary<int, SkillCombatDefinition> definitions)
    {
        _definitions = definitions;
    }

    public static SkillCombatCatalog Empty { get; } = new(
        Array.Empty<SkillCombatDefinition>().ToFrozenDictionary(
            static value => value.SkillId));

    public static SkillCombatCatalog Create(GameplayContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new SkillCombatCatalog(
            content.SkillCombatDefinitions.ToFrozenDictionary(
                static skill => skill.SkillId,
                static skill => new SkillCombatDefinition(
                    skill.SkillId,
                    skill.Target,
                    skill.AffectObj,
                    skill.Distance,
                    skill.Range,
                    skill.Property,
                    skill.Mp,
                    skill.Power1,
                    skill.Power2,
                    skill.CastTime,
                    skill.Cooldown)));
    }

    public int Count => _definitions.Count;

    public bool TryGet(int skillId, out SkillCombatDefinition definition) =>
        _definitions.TryGetValue(skillId, out definition);
}
