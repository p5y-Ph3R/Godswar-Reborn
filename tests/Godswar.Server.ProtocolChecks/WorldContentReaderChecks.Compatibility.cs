using Godswar.Server.Application.World;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderChecks
{
    private static void CheckMonsterSpawnGameplayCompatibility()
    {
        var spawn = CreateTierFourMonster(10074, 1);
        var compatible = CreateGameplay(
            new GameplayMonsterTemplateDefinition(
                "test:1",
                "test",
                1,
                "Athens",
                spawn.TemplateKey,
                spawn.DisplayName,
                "normal",
                false,
                false,
                false,
                2f));

        _ = PinnedWorldContentReader.Create(
            "test-published-v1",
            [1],
            [],
            [spawn],
            [],
            FixedLoadTime,
            gameplay: compatible);

        var incompatible = CaptureUnavailable(() =>
            PinnedWorldContentReader.Create(
                "test-published-v1",
                [1],
                [],
                [spawn],
                [],
                FixedLoadTime,
                gameplay: CreateGameplay(
                    compatible.MonsterTemplates[0] with
                    {
                        TemplateKey = "different_template"
                    })));
        Check.Equal(
            "gameplay",
            incompatible.Family,
            "cross-family monster mismatch failure family");
        Check.True(
            incompatible.Reason == WorldContentFailureReason.Invalid,
            "cross-family monster mismatch is rejected as invalid");
    }

    private static GameplayContentCatalog CreateGameplay(
        GameplayMonsterTemplateDefinition monster) =>
        new(
            [new GameplayMapDefinition(1, "Athens", "Athens", 1)],
            [],
            [],
            [monster],
            [],
            [],
            []);
}
