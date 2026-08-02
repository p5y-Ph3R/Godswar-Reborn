namespace Godswar.Server.Application.Items;

/// <summary>
/// One process-pinned, immutable projection of the published item catalog.
/// A running process never changes revision.
/// </summary>
internal interface IItemTemplateCatalog
{
    ItemTemplateContentRevision Revision { get; }

    IReadOnlyList<ItemTemplateDefinition> All { get; }

    IReadOnlyList<ItemAttributeDefinition> Attributes { get; }

    IReadOnlyList<EquipmentRankDefinition> EquipmentRanks { get; }

    IReadOnlyList<HolySuitEffectDefinition> HolySuitEffects { get; }

    IItemMaterialCatalog Materials { get; }

    IHolySuitContentCatalog HolySuit { get; }

    bool TryGet(uint itemId, out ItemTemplateDefinition definition);
}

internal sealed record ItemTemplateContentRevision(
    string Sha256,
    int EntryCount,
    string Source,
    int ManifestVersion = 2,
    int AttributeCount = 0,
    int EquipmentRankCount = 0,
    int HolySuitEffectCount = 0,
    int MaterialPolicyCount = 0,
    int MaterialRecipeCount = 0,
    int HolySuitTierCount = 0,
    int HolySuitUpgradeCount = 0,
    int HolySuitConsumableCount = 0,
    int HolySuitPolicyCount = 0);

internal sealed record ItemTemplateDefinition(
    uint Id,
    string Kind,
    string NameKey,
    string DisplayName,
    short EquipmentSlot,
    IReadOnlyList<short> ClassIds,
    int? MinLevel,
    int? MaxLevel,
    short? Hand,
    int? SkillFlag,
    string Texture,
    string Icon,
    string StatsJson);

internal sealed record ItemAttributeDefinition(
    int Id,
    string NameKey,
    short StatType,
    IReadOnlyList<short> Distribution,
    bool Percent,
    short MaxLevel,
    string LevelValues,
    string StatsJson);

internal sealed record EquipmentRankDefinition(
    string RankKind,
    short RankLevel,
    int RequiredScore,
    int AuraEffect,
    string Source);

internal sealed record HolySuitEffectDefinition(
    string EffectKey,
    short StatType,
    short UnlockPoints,
    string EffectValue,
    string Source);
