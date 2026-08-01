using System.Collections.Frozen;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct BackhaulSkillDefinition(
    uint SkillId,
    string DisplayName,
    string ScriptId,
    short RequiredCamp,
    byte TargetMapId,
    float TargetX,
    float TargetZ,
    int ManaCost,
    TimeSpan CastTime,
    TimeSpan Cooldown);

internal static class BackhaulSkillCatalog
{
    public const uint CitySkillId = 3062;
    public const uint SuburbSkillId = 3063;

    private static readonly FrozenDictionary<uint, BackhaulSkillDefinition>
        Definitions = CreateDefinitions();

    public static IReadOnlyList<BackhaulSkillDefinition> All { get; } =
        Definitions.Values
            .OrderBy(static definition => definition.SkillId)
            .ToArray();

    public static bool TryGet(
        uint skillId,
        out BackhaulSkillDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);

    private static FrozenDictionary<uint, BackhaulSkillDefinition>
        CreateDefinitions()
    {
        var definitions = new[]
        {
            new BackhaulSkillDefinition(
                CitySkillId,
                "Sparta City",
                "FlyToSparta1",
                GameDefaults.SpartaCamp,
                GameDefaults.SpartaCapitalMap,
                GameDefaults.StartingPositionX,
                GameDefaults.StartingPositionZ,
                ManaCost: 50,
                CastTime: TimeSpan.FromSeconds(6),
                Cooldown: TimeSpan.FromSeconds(300)),
            new BackhaulSkillDefinition(
                SuburbSkillId,
                "Sparta Suburb",
                "FlyToSparta2",
                GameDefaults.SpartaCamp,
                TargetMapId: 4,
                TargetX: 102f,
                TargetZ: -217f,
                ManaCost: 50,
                CastTime: TimeSpan.FromSeconds(6),
                Cooldown: TimeSpan.FromSeconds(600))
        };

        foreach (var definition in definitions)
        {
            if (!MapTraversalLimits.IsFiniteAndBounded(
                    new MapTraversalPosition(
                        definition.TargetX,
                        definition.TargetZ)) ||
                definition.ManaCost < 0 ||
                definition.CastTime < TimeSpan.Zero ||
                definition.Cooldown < TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"Invalid backhaul skill definition {definition.SkillId}.");
            }
        }

        return definitions.ToFrozenDictionary(
            static definition => definition.SkillId);
    }
}
