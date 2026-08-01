using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.World;

internal sealed partial class PinnedWorldContentReader
{
    private static void EnsureMonsterSpawnsHaveGameplayTemplates(
        IReadOnlyList<CapturedMonsterSpawn> spawns,
        GameplayContentCatalog gameplay)
    {
        // Empty gameplay is retained only for narrow protocol fixtures. A
        // published gameplay family must describe every published spawn so
        // combat cannot silently fall back because two official revisions are
        // incompatible.
        if (gameplay.Maps.Count == 0 &&
            gameplay.MonsterTemplates.Count == 0)
        {
            return;
        }

        var exact = gameplay.MonsterTemplates
            .Where(static template => template.SourceMapId.HasValue)
            .Select(static template =>
                (template.SourceMapId!.Value, template.TemplateKey))
            .ToHashSet();
        var global = gameplay.MonsterTemplates
            .Where(static template => !template.SourceMapId.HasValue)
            .Select(static template => template.TemplateKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var spawn in spawns)
        {
            if (!exact.Contains((spawn.MapId, spawn.TemplateKey)) &&
                !global.Contains(spawn.TemplateKey))
            {
                throw Invalid(
                    "gameplay",
                    "Published monster spawn " +
                    $"{spawn.MapId}/{spawn.ObjectId} references absent " +
                    $"gameplay template '{spawn.TemplateKey}'.");
            }
        }
    }
}
