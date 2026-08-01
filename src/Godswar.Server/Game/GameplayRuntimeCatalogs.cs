using Godswar.Server.Application.World;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// Immutable gameplay views derived once from the process-pinned PostgreSQL
/// world-content snapshot. Runtime systems receive this instance through
/// composition and never consult generated seed declarations directly.
/// </summary>
internal sealed record GameplayRuntimeCatalogs(
    GameplayContentCatalog Content,
    MapTraversalCatalog MapTraversal,
    WorldBossCatalog WorldBosses,
    SkillCombatCatalog SkillCombat,
    MonsterCombatRangeCatalog MonsterCombatRanges)
{
    public static GameplayRuntimeCatalogs Empty { get; } = new(
        GameplayContentCatalog.Empty,
        MapTraversalCatalog.Empty,
        WorldBossCatalog.Empty,
        SkillCombatCatalog.Empty,
        MonsterCombatRangeCatalog.Empty);

    public static GameplayRuntimeCatalogs Create(
        GameplayContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content == GameplayContentCatalog.Empty ||
            content.Maps.Count == 0 &&
            content.AddressPoints.Count == 0 &&
            content.Links.Count == 0 &&
            content.MonsterTemplates.Count == 0 &&
            content.WorldBosses.Count == 0 &&
            content.PendingWorldBossAreas.Count == 0 &&
            content.SkillCombatDefinitions.Count == 0)
        {
            return Empty;
        }

        return new GameplayRuntimeCatalogs(
            content,
            MapTraversalCatalog.Create(content),
            WorldBossCatalog.Create(content),
            SkillCombatCatalog.Create(content),
            MonsterCombatRangeCatalog.Create(content));
    }
}
