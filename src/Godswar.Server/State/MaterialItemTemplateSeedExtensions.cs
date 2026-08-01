using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// Publisher-bound mapping from reviewed material baselines to item templates.
/// Runtime gameplay consumes the revision-pinned application catalog instead.
/// </summary>
internal static class MaterialItemTemplateSeedExtensions
{
    public static ItemTemplateSeed ToItemTemplateSeed(
        this GearEnhancementMaterialDefinition value) =>
        Create(
            value.ItemId,
            GearEnhancementMaterialCatalog.ConsumeItemType,
            value.NameKey,
            value.DisplayName,
            value.Texture,
            value.Icon,
            value.StackCap,
            value.Random,
            value.Distribution,
            bindType: null);

    public static ItemTemplateSeed ToItemTemplateSeed(
        this AttributeDustDefinition value) =>
        Create(
            value.ItemId,
            GearMentorMaterialCatalog.ConsumeItemType,
            value.NameKey,
            value.DisplayName,
            value.Texture,
            value.Icon,
            value.StackCap,
            random: 0,
            distribution: "50,150",
            bindType: null);

    public static ItemTemplateSeed ToItemTemplateSeed(
        this ForgingMaterialDefinition value) =>
        Create(
            value.ItemId,
            value.ItemType,
            value.NameKey,
            value.DisplayName,
            value.Texture,
            value.Icon,
            value.StackCap,
            value.Random,
            value.Distribution,
            value.BindType);

    private static ItemTemplateSeed Create(
        uint itemId,
        string itemType,
        string nameKey,
        string displayName,
        string texture,
        string icon,
        short stackCap,
        int random,
        string distribution,
        short? bindType)
    {
        var stats = new Dictionary<string, string>
        {
            ["ID"] = itemId.ToString(),
            ["Type"] = itemType,
            ["Texture"] = texture,
            ["Icon"] = icon,
            ["Random"] = random.ToString(),
            ["Distribution"] = distribution,
            ["Money"] = "0",
            ["Overlap"] = stackCap.ToString()
        };
        if (bindType.HasValue) stats["BindType"] = bindType.Value.ToString();
        return new ItemTemplateSeed(
            checked((int)itemId),
            itemType,
            nameKey,
            displayName,
            EquipmentSlot: 0,
            ClassIds: [],
            MinLevel: null,
            MaxLevel: null,
            Hand: null,
            SkillFlag: null,
            texture,
            icon,
            JsonSerializer.Serialize(stats));
    }
}
