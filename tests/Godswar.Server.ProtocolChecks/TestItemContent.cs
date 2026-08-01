using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class TestItemContent
{
    public static IReadOnlyList<GearMentorMaterialRecipeDefinition>
        GearMentorRecipes { get; } =
        Array.AsReadOnly<GearMentorMaterialRecipeDefinition>(
        [
            new(4234, 4233, GearMentorMaterialRecipeKind.CrystalTransform, 1, 2),
            new(4233, 4232, GearMentorMaterialRecipeKind.CrystalTransform, 1, 2),
            new(4232, 4231, GearMentorMaterialRecipeKind.CrystalTransform, 1, 4),
            new(4231, 4230, GearMentorMaterialRecipeKind.CrystalTransform, 1, 8),
            new(4214, 4213, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4224, 4223, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4216, 4215, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4226, 4225, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1),
            new(4235, 4234, GearMentorMaterialRecipeKind.GemPieceCombination, 99, 1)
        ]);

    private static readonly Lazy<GameplayItemContent> LazyContent =
        new(Create);

    public static GameplayItemContent Content => LazyContent.Value;

    public static IItemTemplateCatalog Catalog => Content.Templates;

    private static GameplayItemContent Create()
    {
        var seeds = ItemTemplateSeeds.All
            .Concat(ForgingMaterialCatalog.All.Select(
                static value => value.ToItemTemplateSeed()))
            .Concat(GearEnhancementMaterialCatalog.All.Select(
                static value => value.ToItemTemplateSeed()))
            .Concat(GearMentorMaterialCatalog.AttributeDusts.Select(
                static value => value.ToItemTemplateSeed()));
        var definitions = seeds.Select(
            static template => new ItemTemplateDefinition(
                checked((uint)template.Id),
                template.Kind,
                template.NameKey,
                template.DisplayName,
                template.EquipmentSlot,
                template.ClassIds,
                template.MinLevel,
                template.MaxLevel,
                template.Hand,
                template.SkillFlag,
                template.Texture,
                template.Icon,
                template.StatsJson)).ToArray();
        return new GameplayItemContent(
            PinnedItemTemplateCatalog.Create(
                "protocol-check-reviewed-baseline",
                definitions,
                [],
                [],
                [],
                ForgingMaterialCatalog.All,
                GearEnhancementMaterialCatalog.All,
                GearMentorMaterialCatalog.AttributeDusts,
                GearMentorRecipes));
    }
}
