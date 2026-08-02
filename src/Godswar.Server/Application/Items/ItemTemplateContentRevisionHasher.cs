using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Items;

internal static partial class ItemTemplateContentRevisionHasher
{
    public static string Compute(
        IReadOnlyList<ItemTemplateDefinition> definitions) =>
        Compute(definitions, [], [], []);

    /// <summary>
    /// Reproduces the canonical format used by manifest-v1 releases. Keep this
    /// only for fail-closed validation while upgrading an existing v1 pointer.
    /// </summary>
    public static string ComputeLegacyV1(
        IReadOnlyList<ItemTemplateDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendItemDefinitions(hash, definitions);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string Compute(
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects)
    {
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendManifestCore(
            hash,
            "item-content-manifest-v2",
            definitions,
            attributes,
            equipmentRanks,
            holySuitEffects);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string ComputeLegacyV2(
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects) =>
        Compute(definitions, attributes, equipmentRanks, holySuitEffects);

    public static string Compute(
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> attributeDusts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendManifestCore(
            hash,
            "item-content-manifest-v3",
            definitions,
            attributes,
            equipmentRanks,
            holySuitEffects);
        AppendMaterialDefinitions(
            hash,
            forgingMaterials,
            enhancementMaterials,
            attributeDusts);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string Compute(
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> attributeDusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> recipes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendManifestCore(
            hash,
            "item-content-manifest-v4",
            definitions,
            attributes,
            equipmentRanks,
            holySuitEffects);
        AppendMaterialDefinitions(
            hash,
            forgingMaterials,
            enhancementMaterials,
            attributeDusts);
        Append(hash, recipes.Count);
        foreach (var value in recipes
                     .OrderBy(static value => value.Kind)
                     .ThenBy(static value => value.SourceItemId))
        {
            Append(hash, value.SourceItemId);
            Append(hash, value.TargetItemId);
            Append(hash, value.Kind.ToString());
            Append(hash, value.SourceQuantity);
            Append(hash, value.TargetQuantity);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendMaterialDefinitions(
        IncrementalHash hash,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> attributeDusts)
    {
        Append(
            hash,
            forgingMaterials.Count +
            enhancementMaterials.Count +
            attributeDusts.Count);
        foreach (var value in forgingMaterials.OrderBy(static value => value.ItemId))
        {
            AppendMaterialCommon(hash, value.ItemId, "forging", value.StackCap,
                value.Random, value.Distribution, value.GrantedBound);
            Append(hash, value.NameKey);
            Append(hash, value.DisplayName);
            Append(hash, value.ItemType);
            Append(hash, value.Material);
            AppendNullable(hash, value.Level);
            hash.AppendData([value.IsPiece ? (byte)1 : (byte)0]);
            Append(hash, value.Texture);
            Append(hash, value.Icon);
        }
        foreach (var value in enhancementMaterials.OrderBy(static value => value.ItemId))
        {
            AppendMaterialCommon(hash, value.ItemId, value.Kind.ToString(), value.StackCap,
                value.Random, value.Distribution, 0);
            Append(hash, value.NameKey);
            Append(hash, value.DisplayName);
            Append(hash, value.Texture);
            Append(hash, value.Icon);
            Append(hash, value.AttributeName ?? string.Empty);
            Append(hash, value.AllowedAttributeIds.Count);
            foreach (var id in value.AllowedAttributeIds) Append(hash, id);
            hash.AppendData([value.CanEnhance ? (byte)1 : (byte)0]);
            AppendNullable(hash, value.SourceAttributeLevel);
            AppendNullable(hash, value.TargetAttributeLevel);
        }
        foreach (var value in attributeDusts.OrderBy(static value => value.ItemId))
        {
            AppendMaterialCommon(hash, value.ItemId, "attribute_dust", value.StackCap,
                0, "50,150", value.GrantedBound);
            Append(hash, value.NameKey);
            Append(hash, value.DisplayName);
            Append(hash, value.Texture);
            Append(hash, value.Icon);
            Append(hash, value.AttributeStoneItemId);
            Append(hash, value.RecipeQuantity);
        }
    }

    private static void AppendManifestCore(
        IncrementalHash hash,
        string manifestIdentity,
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects)
    {
        Append(hash, manifestIdentity);
        Append(hash, definitions.Count);
        AppendItemDefinitions(hash, definitions);

        Append(hash, attributes.Count);
        foreach (var definition in attributes.OrderBy(
                     static definition => definition.Id))
        {
            Append(hash, definition.Id);
            Append(hash, definition.NameKey);
            Append(hash, definition.StatType);
            Append(hash, definition.Distribution.Count);
            foreach (var value in definition.Distribution)
            {
                Append(hash, value);
            }
            hash.AppendData([definition.Percent ? (byte)1 : (byte)0]);
            Append(hash, definition.MaxLevel);
            Append(hash, definition.LevelValues);
            Append(hash, definition.StatsJson);
        }

        Append(hash, equipmentRanks.Count);
        foreach (var definition in equipmentRanks
                     .OrderBy(static definition => definition.RankKind,
                         StringComparer.Ordinal)
                     .ThenBy(static definition => definition.RankLevel))
        {
            Append(hash, definition.RankKind);
            Append(hash, definition.RankLevel);
            Append(hash, definition.RequiredScore);
            Append(hash, definition.AuraEffect);
            Append(hash, definition.Source);
        }

        Append(hash, holySuitEffects.Count);
        foreach (var definition in holySuitEffects.OrderBy(
                     static definition => definition.EffectKey,
                     StringComparer.Ordinal))
        {
            Append(hash, definition.EffectKey);
            Append(hash, definition.StatType);
            Append(hash, definition.UnlockPoints);
            Append(hash, definition.EffectValue);
            Append(hash, definition.Source);
        }

    }

    private static void AppendMaterialCommon(
        IncrementalHash hash,
        uint itemId,
        string kind,
        short stackCap,
        int random,
        string distribution,
        short grantedBound)
    {
        Append(hash, itemId);
        Append(hash, kind);
        Append(hash, stackCap);
        Append(hash, random);
        Append(hash, distribution);
        Append(hash, grantedBound);
    }

    private static void AppendItemDefinitions(
        IncrementalHash hash,
        IReadOnlyList<ItemTemplateDefinition> definitions)
    {
        foreach (var definition in definitions.OrderBy(
                     static definition => definition.Id))
        {
            Append(hash, definition.Id);
            Append(hash, definition.Kind);
            Append(hash, definition.NameKey);
            Append(hash, definition.DisplayName);
            Append(hash, definition.EquipmentSlot);
            Append(hash, definition.ClassIds.Count);
            foreach (var classId in definition.ClassIds)
            {
                Append(hash, classId);
            }
            AppendNullable(hash, definition.MinLevel);
            AppendNullable(hash, definition.MaxLevel);
            AppendNullable(hash, definition.Hand);
            AppendNullable(hash, definition.SkillFlag);
            Append(hash, definition.Texture);
            Append(hash, definition.Icon);
            Append(hash, definition.StatsJson);
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendNullable(
        IncrementalHash hash,
        int? value)
    {
        hash.AppendData([value.HasValue ? (byte)1 : (byte)0]);
        if (value.HasValue)
        {
            Append(hash, value.Value);
        }
    }

    private static void AppendNullable(
        IncrementalHash hash,
        short? value)
    {
        hash.AppendData([value.HasValue ? (byte)1 : (byte)0]);
        if (value.HasValue)
        {
            Append(hash, value.Value);
        }
    }
}
