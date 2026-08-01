using System.Collections.Frozen;

namespace Godswar.Server.Application.Items;

internal sealed class PinnedItemTemplateCatalog : IItemTemplateCatalog
{
    private readonly FrozenDictionary<uint, ItemTemplateDefinition>
        _byItemId;

    private PinnedItemTemplateCatalog(
        ItemTemplateContentRevision revision,
        ItemTemplateDefinition[] definitions,
        ItemAttributeDefinition[] attributes,
        EquipmentRankDefinition[] equipmentRanks,
        HolySuitEffectDefinition[] holySuitEffects,
        PinnedItemMaterialCatalog materials)
    {
        Revision = revision;
        All = Array.AsReadOnly(definitions);
        _byItemId = definitions.ToFrozenDictionary(
            static definition => definition.Id);
        Attributes = Array.AsReadOnly(attributes);
        EquipmentRanks = Array.AsReadOnly(equipmentRanks);
        HolySuitEffects = Array.AsReadOnly(holySuitEffects);
        Materials = materials;
    }

    public ItemTemplateContentRevision Revision { get; }

    public IReadOnlyList<ItemTemplateDefinition> All { get; }

    public IReadOnlyList<ItemAttributeDefinition> Attributes { get; }

    public IReadOnlyList<EquipmentRankDefinition> EquipmentRanks { get; }

    public IReadOnlyList<HolySuitEffectDefinition> HolySuitEffects { get; }

    public IItemMaterialCatalog Materials { get; }

    public bool TryGet(
        uint itemId,
        out ItemTemplateDefinition definition) =>
        _byItemId.TryGetValue(itemId, out definition!);

    public static PinnedItemTemplateCatalog Create(
        string source,
        IReadOnlyList<ItemTemplateDefinition> definitions,
        string? expectedRevision = null) =>
        Create(
            source,
            definitions,
            [],
            [],
            [],
            expectedRevision);

    public static PinnedItemTemplateCatalog Create(
        string source,
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects,
        string? expectedRevision = null) =>
        CreateCore(
            source,
            definitions,
            attributes,
            equipmentRanks,
            holySuitEffects,
            [],
            [],
            [],
            [],
            expectedRevision,
            manifestVersion: 2);

    public static PinnedItemTemplateCatalog Create(
        string source,
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> attributeDusts,
        string? expectedRevision = null) =>
        CreateCore(
            source,
            definitions,
            attributes,
            equipmentRanks,
            holySuitEffects,
            forgingMaterials,
            enhancementMaterials,
            attributeDusts,
            [],
            expectedRevision,
            manifestVersion: 3);

    public static PinnedItemTemplateCatalog Create(
        string source,
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> attributeDusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> recipes,
        string? expectedRevision = null) =>
        CreateCore(
            source,
            definitions,
            attributes,
            equipmentRanks,
            holySuitEffects,
            forgingMaterials,
            enhancementMaterials,
            attributeDusts,
            recipes,
            expectedRevision,
            manifestVersion: 4);

    private static PinnedItemTemplateCatalog CreateCore(
        string source,
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> attributeDusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> recipes,
        string? expectedRevision,
        int manifestVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(definitions);
        var snapshot = definitions
            .Select(static definition => definition with
            {
                ClassIds = Array.AsReadOnly(
                    definition.ClassIds.ToArray())
            })
            .OrderBy(static definition => definition.Id)
            .ToArray();
        if (snapshot.Length == 0 ||
            snapshot.Any(static definition =>
                definition.Id == 0 ||
                string.IsNullOrWhiteSpace(definition.Kind) ||
                string.IsNullOrWhiteSpace(definition.NameKey) ||
                string.IsNullOrWhiteSpace(definition.DisplayName)) ||
            snapshot.Select(static definition => definition.Id)
                .Distinct().Count() != snapshot.Length)
        {
            throw new InvalidOperationException(
                "The published item-template snapshot is empty or invalid.");
        }

        var attributeSnapshot = attributes
            .Select(static definition => definition with
            {
                Distribution = Array.AsReadOnly(
                    definition.Distribution.ToArray())
            })
            .OrderBy(static definition => definition.Id)
            .ToArray();
        var rankSnapshot = equipmentRanks
            .OrderBy(static definition => definition.RankKind,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.RankLevel)
            .ToArray();
        var holySuitSnapshot = holySuitEffects
            .OrderBy(static definition => definition.EffectKey,
                StringComparer.Ordinal)
            .ToArray();

        var knownTemplateIds = snapshot
            .Select(static definition => definition.Id)
            .ToHashSet();
        var materialCatalog = manifestVersion == 4
            ? PinnedItemMaterialCatalog.Create(
                forgingMaterials,
                enhancementMaterials,
                attributeDusts,
                recipes,
                knownTemplateIds)
            : PinnedItemMaterialCatalog.Create(
                forgingMaterials,
                enhancementMaterials,
                attributeDusts,
                knownTemplateIds,
                allowEmpty: manifestVersion == 2);

        var revision = manifestVersion switch
        {
            2 => ItemTemplateContentRevisionHasher.Compute(
                snapshot,
                attributeSnapshot,
                rankSnapshot,
                holySuitSnapshot),
            3 => ItemTemplateContentRevisionHasher.Compute(
                snapshot,
                attributeSnapshot,
                rankSnapshot,
                holySuitSnapshot,
                materialCatalog.ForgingMaterials,
                materialCatalog.GearEnhancementMaterials,
                materialCatalog.AttributeDusts),
            4 => ItemTemplateContentRevisionHasher.Compute(
                snapshot,
                attributeSnapshot,
                rankSnapshot,
                holySuitSnapshot,
                materialCatalog.ForgingMaterials,
                materialCatalog.GearEnhancementMaterials,
                materialCatalog.AttributeDusts,
                materialCatalog.GearMentorRecipes),
            _ => throw new InvalidOperationException(
                $"Unsupported item-content manifest version {manifestVersion}.")
        };
        if (expectedRevision is not null &&
            !revision.Equals(expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The published item-template snapshot does not match its revision.");
        }

        return new PinnedItemTemplateCatalog(
            new ItemTemplateContentRevision(
                revision,
                snapshot.Length,
                source,
                ManifestVersion: manifestVersion,
                attributeSnapshot.Length,
                rankSnapshot.Length,
                holySuitSnapshot.Length,
                MaterialPolicyCount: materialCatalog.ForgingMaterials.Count +
                    materialCatalog.GearEnhancementMaterials.Count +
                    materialCatalog.AttributeDusts.Count,
                MaterialRecipeCount: materialCatalog.GearMentorRecipes.Count),
            snapshot,
            attributeSnapshot,
            rankSnapshot,
            holySuitSnapshot,
            materialCatalog);
    }
}
