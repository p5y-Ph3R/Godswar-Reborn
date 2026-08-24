using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed stock Storage Box Key. It is consumed only by the Warehouse
/// Manager workflow and deliberately has no client-side Use or Skill field.
/// </summary>
internal static class WarehouseItemContentBaseline
{
    public const int StorageBoxKeyItemId = 4102;
    public const short MaximumStack = 99;

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
    [
        new(
            StorageBoxKeyItemId,
            "consume item",
            "Storage1",
            "Storage Box Key",
            EquipmentSlot: 0,
            ClassIds: [],
            MinLevel: null,
            MaxLevel: null,
            Hand: null,
            SkillFlag: null,
            "./Localization/en_us/UI/Texture/Icon2.gwo",
            "252,36",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["ID"] = "4102",
                ["Type"] = "consume item",
                ["Texture"] =
                    "./Localization/en_us/UI/Texture/Icon2.gwo",
                ["Icon"] = "252,36",
                ["Random"] = "0",
                ["Distribution"] = "0,0",
                ["Money"] = "0",
                ["Overlap"] = "99",
                ["BindType"] = "1"
            }))
    ];
}
