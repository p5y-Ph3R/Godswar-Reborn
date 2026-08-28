using Godswar.Server.Game;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.ProtocolChecks;

internal static class WorldBossCatalogChecks
{
    public static Task RunAsync()
    {
        var gameplay = GameplayContentTestFixtures.Published;
        var catalog = WorldBossCatalog.Create(gameplay);
        var expectedMaps = new short[]
        {
            3, 5, 6, 7, 8, 9, 10, 11, 12, 13,
            14, 15, 16, 17, 18, 19, 20, 21, 22
        };

        Check.Equal(TimeSpan.FromHours(12), catalog.RespawnInterval, "world bosses refresh twice daily");
        var firstBoss = catalog.Definitions[0];
        Check.Equal(
            firstBoss.RespawnInterval,
            catalog.ResolveRespawnInterval(
                firstBoss.MapId,
                firstBoss.TemplateKey,
                TimeSpan.FromSeconds(10)),
            "published per-boss respawn interval overrides ordinary respawn");
        Check.Equal(expectedMaps.Length, catalog.Definitions.Count, "one selected boss per ready outdoor area");
        Check.True(
            expectedMaps.All(catalog.IsEligibleArea),
            "all configured outdoor areas are eligible");
        Check.True(
            catalog.Definitions.GroupBy(definition => definition.MapId).All(group => group.Count() == 1),
            "each eligible area selects exactly one world boss");
        Check.True(catalog.IsEligibleArea(68), "Parnassus is classified as an outdoor eligible area");
        Check.True(!catalog.TryGet(68, out _), "Parnassus remains disabled until a neutral boss is authored");
        Check.Equal(1, catalog.PendingAreas.Count, "one eligible outdoor area is pending a neutral boss");
        Check.Equal((short)68, catalog.PendingAreas[0].MapId, "Parnassus is the explicitly pending area");
        var pendingOnly = WorldBossCatalog.Create(
            gameplay with { WorldBosses = [] });
        Check.True(
            pendingOnly.IsEligibleArea(68) &&
            pendingOnly.Definitions.Count == 0,
            "a publication may retain pending areas before any boss is enabled");

        foreach (var excludedMap in new short[] { 0, 1, 2, 4, 23, 30, 31, 40, 42, 44, 46, 69, 200, 210 })
        {
            Check.True(!catalog.IsEligibleArea(excludedMap), $"map {excludedMap} is city, suburb, or special content");
        }

        foreach (var definition in catalog.Definitions)
        {
            var template = gameplay.MonsterTemplates.Single(candidate =>
                candidate.SourceMapId == definition.MapId &&
                candidate.TemplateKey == definition.TemplateKey);
            Check.True(template.IsBoss, $"{definition.DisplayName} uses a boss template");
            Check.True(!template.IsElite, $"{definition.DisplayName} is not an elite template");
            Check.True(!template.IsPet, $"{definition.DisplayName} is not a pet template");
            Check.True(
                catalog.IsWorldBoss(definition.MapId, definition.TemplateKey),
                $"{definition.DisplayName} is explicitly selected for its area");
        }

        var rangedTemplate = gameplay.MonsterTemplates.First(template =>
            template.SourceMapId is not null &&
            template.CollisionRange is > 0);
        var rangedSpawn = new CapturedMonsterSpawn(
            rangedTemplate.SourceMapId!.Value,
            rangedTemplate.SceneKey,
            rangedTemplate.TemplateKey,
            rangedTemplate.DisplayName,
            ObjectId: 1,
            X: 0f,
            Z: 0f,
            Packet: []);
        Check.Equal(
            rangedTemplate.CollisionRange!.Value,
            MonsterCombatResolver.ResolvePlayerBasicAttackRange(
                rangedSpawn,
                GameplayContentTestFixtures.Runtime.MonsterCombatRanges),
            "basic attack range comes from the published monster template");
        Check.Equal(
            rangedTemplate.CollisionRange.Value,
            MonsterCombatResolver.ResolvePlayerBasicAttackRange(
                rangedSpawn,
                GameplayContentTestFixtures.Runtime.MonsterCombatRanges,
                authoredPlayerRange: 1f),
            "monster collision reach prevents a short authored basic attack from being rejected");
        Check.Equal(
            8f,
            MonsterCombatResolver.ResolvePlayerBasicAttackRange(
                rangedSpawn,
                GameplayContentTestFixtures.Runtime.MonsterCombatRanges,
                authoredPlayerRange: 8f),
            "a longer authored basic attack reach is preserved");
        Check.Equal(
            MonsterCombatResolver.DefaultPlayerBasicAttackRange,
            MonsterCombatResolver.ResolvePlayerBasicAttackRange(
                rangedSpawn,
                MonsterCombatRangeCatalog.Empty),
            "missing published collision metadata uses the bounded default");

        Check.True(
            !catalog.IsWorldBoss(15, "B_boss_harpies_001"),
            "Harpy Queen remains a secondary boss, not Derveni's selected world boss");
        Check.True(
            !catalog.IsWorldBoss(19, "B_boss_element_005"),
            "Peya remains a secondary boss, not Plataea's selected world boss");
        Check.True(
            !catalog.IsWorldBoss(21, "C_boss_godsguard_010"),
            "Ares' Guard remains a secondary boss, not Olympus' selected world boss");
        Check.True(
            !catalog.IsWorldBoss(3, "C_boss_greecewarrior_001"),
            "an elite-general boss template is not treated as a world boss");

        Check.Throws<InvalidDataException>(
            () => WorldBossCatalog.Create(
                [
                    new WorldBossDefinition(3, "Parnitha_1", "boss-one", "Boss One"),
                    new WorldBossDefinition(3, "Parnitha_1", "boss-two", "Boss Two")
                ],
                TimeSpan.FromHours(12)),
            "duplicate map selections are rejected");
        Check.Throws<InvalidDataException>(
            () => WorldBossCatalog.Create(
                [new WorldBossDefinition(3, "Parnitha_1", "elite", "[Elite]Not A World Boss")],
                TimeSpan.FromHours(12)),
            "elite-labelled monsters cannot be configured as world bosses");

        return Task.CompletedTask;
    }
}
