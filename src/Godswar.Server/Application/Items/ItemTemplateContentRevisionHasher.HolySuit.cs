using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Godswar.Server.Application.Items;

internal static partial class ItemTemplateContentRevisionHasher
{
    public static string Compute(
        IReadOnlyList<ItemTemplateDefinition> definitions,
        IReadOnlyList<ItemAttributeDefinition> attributes,
        IReadOnlyList<EquipmentRankDefinition> equipmentRanks,
        IReadOnlyList<HolySuitEffectDefinition> holySuitEffects,
        IReadOnlyList<ForgingMaterialDefinition> forgingMaterials,
        IReadOnlyList<GearEnhancementMaterialDefinition> enhancementMaterials,
        IReadOnlyList<AttributeDustDefinition> attributeDusts,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> recipes,
        IReadOnlyList<HolySuitTierDefinition> holySuitTiers,
        IReadOnlyList<HolySuitUpgradeDefinition> holySuitUpgrades,
        IReadOnlyList<HolySuitConsumableDefinition> holySuitConsumables,
        HolySuitOperationPolicy holySuitOperationPolicy)
    {
        ArgumentNullException.ThrowIfNull(holySuitOperationPolicy);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendManifestCore(
            hash,
            "item-content-manifest-v5",
            definitions,
            attributes,
            equipmentRanks,
            holySuitEffects);
        AppendMaterialDefinitions(
            hash,
            forgingMaterials,
            enhancementMaterials,
            attributeDusts);
        AppendRecipes(hash, recipes);
        AppendHolySuitContent(
            hash,
            holySuitTiers,
            holySuitUpgrades,
            holySuitConsumables,
            holySuitOperationPolicy);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendRecipes(
        IncrementalHash hash,
        IReadOnlyList<GearMentorMaterialRecipeDefinition> recipes)
    {
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
    }

    private static void AppendHolySuitContent(
        IncrementalHash hash,
        IReadOnlyList<HolySuitTierDefinition> tiers,
        IReadOnlyList<HolySuitUpgradeDefinition> upgrades,
        IReadOnlyList<HolySuitConsumableDefinition> consumables,
        HolySuitOperationPolicy policy)
    {
        Append(hash, tiers.Count);
        foreach (var value in tiers.OrderBy(static value => value.SuitType))
        {
            Append(hash, value.SuitType);
            Append(hash, value.Name);
            Append(hash, value.MaxLevel);
            AppendNullable(hash, value.WareItemId);
            Append(hash, value.Source);
        }

        Append(hash, upgrades.Count);
        foreach (var value in upgrades
                     .OrderBy(static value => value.CurrentSuitType)
                     .ThenBy(static value => value.CurrentLevel))
        {
            Append(hash, value.CurrentSuitType);
            Append(hash, value.CurrentLevel);
            Append(hash, value.TargetSuitType);
            Append(hash, value.TargetLevel);
            Append(hash, value.RequiredItemExperience);
            Append(hash, value.WareItemId);
            Append(hash, value.WareQuantity);
            Append(hash, value.RequiredPrisms);
            Append(hash, value.Source);
        }

        Append(hash, consumables.Count);
        foreach (var value in consumables.OrderBy(static value => value.ItemId))
        {
            Append(hash, value.ItemId);
            Append(hash, (short)value.Role);
            AppendNullable(hash, value.SuitType);
            Append(hash, value.ExperienceCapacity);
            Append(hash, value.StackCap);
            Append(hash, value.GrantedBound);
            Append(hash, value.Source);
        }

        Append(hash, 1);
        Append(hash, policy.MinimumPlayerLevel);
        Append(hash, policy.MinimumGearLevel);
        Append(hash, policy.LegacyDailyExperiencePerPlayerLevel);
        if (policy.DailyExperiencePerPlayer.HasValue)
        {
            Append(hash, "fixed-daily-player-cap-v1");
            Append(hash, policy.DailyExperiencePerPlayer.Value);
        }
        Append(hash, policy.PerOperationExperienceMaximum);
        Append(hash, policy.GearExperienceCapacity);
        Append(hash, policy.ExperiencePrismCost);
        Append(hash, policy.RealmDayTimeZone);
        Append(hash, policy.DailyQuotaBypassEntitlement);
        Append(hash, policy.Source);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendNullable(IncrementalHash hash, uint? value)
    {
        hash.AppendData([value.HasValue ? (byte)1 : (byte)0]);
        if (value.HasValue)
        {
            Append(hash, value.Value);
        }
    }
}
