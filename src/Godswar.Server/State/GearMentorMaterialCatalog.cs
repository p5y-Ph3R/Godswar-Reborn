using System.Text.Json;

namespace Godswar.Server.State;

internal sealed record AttributeDustDefinition(
    uint ItemId,
    string NameKey,
    string DisplayName,
    uint AttributeStoneItemId,
    string Texture,
    string Icon,
    short StackCap = GearMentorMaterialCatalog.StackCap)
{
    public short GrantedBound => 0;

    public ItemTemplateSeed ToItemTemplateSeed()
    {
        var stats = new Dictionary<string, string>
        {
            ["ID"] = ItemId.ToString(),
            ["Type"] = GearMentorMaterialCatalog.ConsumeItemType,
            ["Texture"] = Texture,
            ["Icon"] = Icon,
            ["Random"] = "0",
            ["Distribution"] = "50,150",
            ["Money"] = "0",
            ["Overlap"] = StackCap.ToString()
        };

        return new ItemTemplateSeed(
            checked((int)ItemId),
            GearMentorMaterialCatalog.ConsumeItemType,
            NameKey,
            DisplayName,
            EquipmentSlot: 0,
            ClassIds: [],
            MinLevel: null,
            MaxLevel: null,
            Hand: null,
            SkillFlag: null,
            Texture,
            Icon,
            JsonSerializer.Serialize(stats));
    }
}

/// <summary>
/// Native Gear Mentor dust definitions and their one-to-one Attribute Stone
/// recipes. IDs, names, icons, and the 99:1 recipes come from the shipped
/// ItemBaseAttribute/EquipName/EquipDescription data.
/// </summary>
internal static class GearMentorMaterialCatalog
{
    public const string ConsumeItemType = "consume item";
    public const short StackCap = 99;
    public const int StoneRecipeDustQuantity = 99;

    private const string Icon2 = "./Localization/en_us/UI/Texture/Icon2.gwo";

    public static IReadOnlyList<AttributeDustDefinition> AttributeDusts { get; } =
    [
        Dust(9900, "Rmaterial1", "Strength Dust", 9930, "504,432"),
        Dust(9901, "Rmaterial2", "Shield Dust", 9931, "540,432"),
        Dust(9902, "Rmaterial3", "Magic Dust", 9932, "576,432"),
        Dust(9903, "Rmaterial4", "Spell Dust", 9933, "612,432"),
        Dust(9904, "Rmaterial5", "Absorption Dust", 9934, "648,432"),
        Dust(9905, "Rmaterial6", "Health Dust", 9935, "684,432"),
        Dust(9906, "Rmaterial7", "Mana Dust", 9936, "720,432"),
        Dust(9907, "Rmaterial8", "Blood Dust", 9937, "756,432"),
        Dust(9908, "Rmaterial9", "Vigor Dust", 9938, "792,432"),

        Dust(9910, "Rmaterial10", "Accuracy Dust", 9940, "504,504"),
        Dust(9911, "Rmaterial11", "Psychic Dust", 9941, "540,504"),
        Dust(9912, "Rmaterial12", "Fury Dust", 9942, "576,504"),
        Dust(9913, "Rmaterial13", "Tenacity Dust", 9943, "612,504"),
        Dust(9914, "Rmaterial14", "Impact Dust", 9944, "648,504"),
        Dust(9915, "Rmaterial15", "Fervor Dust", 9945, "684,504"),
        Dust(9916, "Rmaterial16", "Punishment Dust", 9946, "720,504"),
        Dust(9917, "Rmaterial17", "Purge Dust", 9947, "756,504"),
        Dust(9918, "Rmaterial18", "Guard Dust", 9948, "792,504"),
        Dust(9919, "Rmaterial19", "Restoration Dust", 9949, "828,504"),
        Dust(9920, "Rmaterial20", "Dust of Destruction", 9958, "864,504"),
        Dust(9921, "Rmaterial21", "Dust of Penetration", 9959, "900,504")
    ];

    private static readonly IReadOnlyDictionary<uint, AttributeDustDefinition> ByItemId =
        AttributeDusts.ToDictionary(static dust => dust.ItemId);

    private static readonly IReadOnlyDictionary<uint, AttributeDustDefinition> ByStoneItemId =
        AttributeDusts.ToDictionary(static dust => dust.AttributeStoneItemId);

    private static readonly IReadOnlyDictionary<int, AttributeDustDefinition> ByAttributeId =
        CreateAttributeMap();

    public static bool TryGetDust(uint itemId, out AttributeDustDefinition dust)
    {
        return ByItemId.TryGetValue(itemId, out dust!);
    }

    public static bool TryGetDustForStone(uint stoneItemId, out AttributeDustDefinition dust)
    {
        return ByStoneItemId.TryGetValue(stoneItemId, out dust!);
    }

    public static bool TryGetDustForAttribute(int attributeId, out AttributeDustDefinition dust)
    {
        return ByAttributeId.TryGetValue(attributeId, out dust!);
    }

    internal static IEnumerable<string> GetAliases(AttributeDustDefinition dust)
    {
        yield return dust.NameKey;
        yield return dust.DisplayName;
        yield return dust.DisplayName.Replace(" Dust", "Dust", StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<int, AttributeDustDefinition> CreateAttributeMap()
    {
        var map = new Dictionary<int, AttributeDustDefinition>();
        foreach (var dust in AttributeDusts)
        {
            if (!GearEnhancementMaterialCatalog.TryGetAttributeStone(
                    dust.AttributeStoneItemId,
                    out var stone))
            {
                throw new InvalidOperationException(
                    $"Dust {dust.ItemId} references missing Attribute Stone {dust.AttributeStoneItemId}.");
            }

            foreach (var attributeId in stone.AllowedAttributeIds)
            {
                map.TryAdd(attributeId, dust);
            }
        }

        return map;
    }

    private static AttributeDustDefinition Dust(
        uint itemId,
        string nameKey,
        string displayName,
        uint stoneItemId,
        string icon)
    {
        return new AttributeDustDefinition(
            itemId,
            nameKey,
            displayName,
            stoneItemId,
            Icon2,
            icon);
    }
}
