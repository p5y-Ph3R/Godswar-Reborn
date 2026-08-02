namespace Godswar.Server.Application.Items;

internal enum GearEnhancementMaterialKind
{
    AttributeStone,
    QuartzPlate,
    FlameSpark,
    WaterGrain
}

internal enum GearMentorMaterialRecipeKind
{
    CrystalTransform,
    GemPieceCombination
}

internal sealed record GearMentorMaterialRecipeDefinition(
    uint SourceItemId,
    uint TargetItemId,
    GearMentorMaterialRecipeKind Kind,
    int SourceQuantity,
    int TargetQuantity);

internal sealed record GearEnhancementMaterialDefinition(
    uint ItemId,
    string NameKey,
    string DisplayName,
    GearEnhancementMaterialKind Kind,
    string Texture,
    string Icon,
    short StackCap,
    int Random,
    string Distribution,
    string? AttributeName = null,
    IReadOnlyList<int>? AttributeChain = null,
    bool CanEnhance = false,
    short? SourceAttributeLevel = null,
    short? TargetAttributeLevel = null)
{
    public IReadOnlyList<int> AllowedAttributeIds => AttributeChain ?? [];
}

internal sealed record AttributeDustDefinition(
    uint ItemId,
    string NameKey,
    string DisplayName,
    uint AttributeStoneItemId,
    string Texture,
    string Icon,
    short StackCap,
    int RecipeQuantity,
    short GrantedBound = 0);

internal sealed record ForgingMaterialDefinition(
    uint ItemId,
    string NameKey,
    string DisplayName,
    string ItemType,
    short StackCap,
    string Material,
    int? Level,
    bool IsPiece,
    string Texture,
    string Icon,
    short? BindType = null,
    int Random = 0,
    string Distribution = "0,0")
{
    public string CanonicalAlias => IsPiece
        ? $"{Material}{Level}pieces"
        : $"{Material}{Level}";

    public short GrantedBound => BindType.HasValue ? (short)1 : (short)0;
}

internal sealed record DeveloperGrantMaterialDefinition(
    uint ItemId,
    string DisplayName,
    short StackCap,
    short GrantedBound);

internal interface IDeveloperItemGrantCatalog
{
    bool TryResolveDeveloper(
        uint itemId,
        out DeveloperGrantMaterialDefinition item);

    bool TryResolveDeveloper(
        string alias,
        out DeveloperGrantMaterialDefinition item);
}

internal interface IItemMaterialCatalog : IDeveloperItemGrantCatalog
{
    IReadOnlyList<ForgingMaterialDefinition> ForgingMaterials { get; }

    IReadOnlyList<GearEnhancementMaterialDefinition> GearEnhancementMaterials { get; }

    IReadOnlyList<GearEnhancementMaterialDefinition> AttributeStones { get; }

    IReadOnlyList<AttributeDustDefinition> AttributeDusts { get; }

    IReadOnlyList<GearMentorMaterialRecipeDefinition> GearMentorRecipes { get; }

    IReadOnlyList<DeveloperGrantMaterialDefinition> DeveloperMaterials { get; }

    bool TryResolveForging(uint itemId, out ForgingMaterialDefinition material);

    bool TryResolveForging(string alias, out ForgingMaterialDefinition material);

    bool TryGetGearEnhancement(uint itemId, out GearEnhancementMaterialDefinition material);

    bool TryGetAttributeStone(uint itemId, out GearEnhancementMaterialDefinition material);

    bool TryGetDust(uint itemId, out AttributeDustDefinition dust);

    bool TryGetDustForStone(uint stoneItemId, out AttributeDustDefinition dust);

    bool TryGetDustForAttribute(int attributeId, out AttributeDustDefinition dust);

    bool TryResolveCrystalTransform(
        uint sourceItemId,
        out GearMentorMaterialRecipeDefinition recipe);

    bool TryResolveGemPieceCombination(
        uint sourceItemId,
        out GearMentorMaterialRecipeDefinition recipe);

    int ResolveStackCap(uint itemId);
}
