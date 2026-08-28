using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaIslandRosterPolicyChecks
{
    private static void CheckTemplatePairs()
    {
        var templates = MedusaIslandRosterPolicy.Templates;
        Check.Equal(40, templates.Length,
            "four boss, 26 elite, and ten normal template pairs exist");
        Check.Equal(40, templates.Select(template => template.Alias)
            .Distinct(StringComparer.Ordinal).Count(),
            "logical template aliases are unique");
        Check.Equal(40, templates.Select(template => template.EnhancedTemplateKey)
            .Distinct(StringComparer.Ordinal).Count(),
            "map 200 template keys are unique");
        Check.Equal(40, templates.Select(template => template.NormalTemplateKey)
            .Distinct(StringComparer.Ordinal).Count(),
            "map 204 template keys are unique");
        var aliases = templates.Select(template => template.Alias)
            .ToHashSet(StringComparer.Ordinal);
        Check.True(MedusaIslandRosterPolicy.Spawns.All(spawn =>
                aliases.Contains(spawn.TemplateAlias)),
            "every captured roster entry resolves a selected client template");

        foreach (var pair in templates)
        {
            CheckResolvedTemplate(
                MedusaEncounterDifficulty.Enhanced, 200,
                "Medusa_Island", pair.EnhancedTemplateKey, pair);
            CheckResolvedTemplate(
                MedusaEncounterDifficulty.Mythic, 200,
                "Medusa_Island", pair.EnhancedTemplateKey, pair);
            CheckResolvedTemplate(
                MedusaEncounterDifficulty.Normal, 204,
                "Medusa_Island2", pair.NormalTemplateKey, pair);

            Check.True(MedusaIslandRosterPolicy.TryResolveTemplateByMap(
                    200, pair.Alias, out var enhancedByMap) &&
                enhancedByMap.TemplateKey == pair.EnhancedTemplateKey &&
                MedusaIslandRosterPolicy.TryResolveTemplateByMap(
                    204, pair.Alias, out var normalByMap) &&
                normalByMap.TemplateKey == pair.NormalTemplateKey,
                $"{pair.Alias} resolves through both exact map identities");
        }
    }
}
