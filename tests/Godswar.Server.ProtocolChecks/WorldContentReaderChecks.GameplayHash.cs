using Godswar.Server.Application.World;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderChecks
{
    private static void CheckGameplayHashDomainSeparation()
    {
        var mapWithoutScene = GameplayContentCatalog.Empty with
        {
            Maps = [new GameplayMapDefinition(1, "map", "Map", null)]
        };
        var mapWithFormerSentinel = mapWithoutScene with
        {
            Maps =
            [
                new GameplayMapDefinition(
                    1,
                    "map",
                    "Map",
                    int.MinValue)
            ]
        };
        Check.True(
            WorldContentRevisionHasher.HashGameplay(mapWithoutScene).Sha256 !=
            WorldContentRevisionHasher.HashGameplay(mapWithFormerSentinel)
                .Sha256,
            "nullable client scene ID has a distinct hash domain");

        var monsterWithoutCollision = GameplayContentCatalog.Empty with
        {
            MonsterTemplates =
            [
                new GameplayMonsterTemplateDefinition(
                    "source",
                    "map",
                    1,
                    "map",
                    "monster",
                    "Monster",
                    "normal",
                    false,
                    false,
                    false,
                    null)
            ]
        };
        var monsterWithNegativeZero = monsterWithoutCollision with
        {
            MonsterTemplates =
            [monsterWithoutCollision.MonsterTemplates[0] with
                { CollisionRange = -0.0f }]
        };
        Check.True(
            WorldContentRevisionHasher.HashGameplay(
                monsterWithoutCollision).Sha256 !=
            WorldContentRevisionHasher.HashGameplay(
                monsterWithNegativeZero).Sha256,
            "nullable collision range cannot collide with negative zero");

        var progression = GameplayContentCatalog.Empty with
        {
            Classes =
            [new GameplayClassDefinition(1, "class", "Class", "source")]
        };
        Check.True(
            WorldContentRevisionHasher.HashGameplay(progression).Sha256 !=
            WorldContentRevisionHasher.HashGameplay(
                progression with
                {
                    Classes =
                    [
                        new GameplayClassDefinition(
                            1,
                            "class",
                            "Changed",
                            "source")
                    ]
                }).Sha256,
            "progression metadata participates in the gameplay revision");
    }
}
