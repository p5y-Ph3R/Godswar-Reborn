using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
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
    private static readonly Lazy<GameplayItemContent> LazyHolySuitContent =
        new(CreateWithHolySuit);

    public static GameplayItemContent Content => LazyContent.Value;

    public static GameplayItemContent HolySuitContent =>
        LazyHolySuitContent.Value;

    public static IItemTemplateCatalog Catalog => Content.Templates;

    private static GameplayItemContent Create()
    {
        var definitions = CreateDefinitions(includeHolySuit: false);
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

    private static GameplayItemContent CreateWithHolySuit()
    {
        var definitions = CreateDefinitions(includeHolySuit: true);
        return new GameplayItemContent(
            PinnedItemTemplateCatalog.Create(
                "protocol-check-reviewed-holy-suit-baseline",
                definitions,
                [],
                [],
                [],
                ForgingMaterialCatalog.All,
                GearEnhancementMaterialCatalog.All,
                GearMentorMaterialCatalog.AttributeDusts,
                GearMentorRecipes,
                HolySuitContentBaseline.Tiers,
                HolySuitContentBaseline.Upgrades,
                HolySuitContentBaseline.Consumables,
                HolySuitContentBaseline.OperationPolicy));
    }

    private static ItemTemplateDefinition[] CreateDefinitions(
        bool includeHolySuit)
    {
        var seeds = ItemTemplateSeeds.All
            .Concat(ForgingMaterialCatalog.All.Select(
                static value => value.ToItemTemplateSeed()))
            .Concat(GearEnhancementMaterialCatalog.All.Select(
                static value => value.ToItemTemplateSeed()))
            .Concat(GearMentorMaterialCatalog.AttributeDusts.Select(
                static value => value.ToItemTemplateSeed()))
            .Concat(SocketSpellItemContentBaseline.ItemTemplates)
            .Concat(PetItemContentBaseline.ItemTemplates)
            .Concat(ClassSuitItemContentBaseline.PromotionalInsignias);
        if (includeHolySuit)
        {
            seeds = seeds.Concat(HolySuitContentBaseline.ItemTemplates);
        }

        return seeds.Select(
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
    }
}
