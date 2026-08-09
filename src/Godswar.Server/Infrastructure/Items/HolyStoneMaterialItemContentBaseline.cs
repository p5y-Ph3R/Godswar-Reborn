using System.Globalization;
using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed stock-client and locally authored Holy Stone material definitions
/// used only while publishing immutable item content. Runtime code consumes
/// the sealed PostgreSQL projection rather than these compiled seeds.
/// </summary>
internal static class HolyStoneMaterialItemContentBaseline
{
    public const string ItemType = "consume item";
    public const string IconTexture =
        "./Localization/en_us/UI/Texture/Icon.gwo";
    public const string Icon2Texture =
        "./Localization/en_us/UI/Texture/Icon2.gwo";
    public const string Icon5Texture =
        "./Localization/en_us/UI/Texture/Icon5.gwo";

    private static readonly ReviewedItem[] ReviewedItems =
    [
        new(9030, "Stone9030", "Heated Holy Stone", Icon2Texture,
            "252,0", 1, "PreStone"),
        new(9031, "Stone9031", "Cooled Holy Stone", Icon2Texture,
            "288,0", 1, "PreStone"),
        new(9032, "Stone9032", "Zephyr Holy Stone", Icon5Texture,
            "612,0", 1, "PreStone"),
        new(9040, "Stone9040", "Level 1 Eclipse Stone", IconTexture,
            "828,900", 99),
        new(9041, "Stone9041", "Level 2 Eclipse Stone", IconTexture,
            "864,900", 99),
        new(9042, "Stone9042", "Level 3 Eclipse Stone", IconTexture,
            "900,900", 99),
        new(9050, "Stone9050", "Goddess' Stone", Icon2Texture,
            "324,0", 99),
        new(9051, "Stone9051", "Copper Evasion Signet", IconTexture,
            "540,936", 99),
        new(9052, "Stone9052", "Silver Evasion Signet", IconTexture,
            "576,936", 99),
        new(9053, "Stone9053", "Gold Evasion Signet", IconTexture,
            "612,936", 99),
        new(9054, "Stone9054", "Gold Evasion Signet", IconTexture,
            "612,936", 99),
        new(9055, "Stone9055", "Gold Evasion Signet", IconTexture,
            "612,936", 99),
        new(9056, "Stone9056", "Gold Evasion Signet", IconTexture,
            "612,936", 99),
        new(9060, "Firegholiness1", "Fire Spirit of Destruction",
            Icon2Texture, "360,0", 99),
        new(9061, "Firegholiness2", "Fire Spirit of Penetration",
            Icon2Texture, "396,0", 99),
        new(9062, "Firegholiness3", "Fire Spirit of Fist",
            Icon2Texture, "432,0", 99),
        new(9063, "Firegholiness4", "Fire Spirit of Fiery",
            Icon2Texture, "468,0", 99),
        new(9064, "Firegholiness5", "Fire Spirit of Blood",
            Icon2Texture, "504,0", 99),
        new(9065, "Firegholiness6", "Fire Spirit of Pressure",
            Icon2Texture, "540,0", 99),
        new(9066, "Firegholiness7", "Fire Spirit of Assail",
            Icon2Texture, "864,0", 99),
        new(9067, "Firegholiness8", "Fire Spirit of Lightning",
            Icon2Texture, "900,0", 99),
        new(9068, "Waterholiness9", "Water Spirit of Renewal",
            Icon2Texture, "756,36", 99),
        new(9069, "Waterholiness10", "Water Spirit of Vitality",
            Icon2Texture, "792,36", 99),
        new(9080, "Waterholiness1", "Water Spirit of Darkness",
            Icon2Texture, "576,0", 99),
        new(9081, "Waterholiness2", "Water Spirit of Mist",
            Icon2Texture, "612,0", 99),
        new(9082, "Waterholiness3", "Water Spirit of Silence",
            Icon2Texture, "648,0", 99),
        new(9083, "Waterholiness4", "Water Spirit of Chillness",
            Icon2Texture, "684,0", 99),
        new(9084, "Waterholiness5", "Water Spirit of Ice",
            Icon2Texture, "720,0", 99),
        new(9085, "Waterholiness6", "Water Spirit of Frost",
            Icon2Texture, "756,0", 99),
        new(9086, "Waterholiness7", "Water Spirit of Intent",
            Icon2Texture, "792,0", 99),
        new(9087, "Waterholiness8", "Water Spirit of Resilience",
            Icon2Texture, "828,0", 99),
        new(9088, "Firegholiness9", "Fire Spirit of Flow",
            Icon2Texture, "828,36", 99),
        new(9089, "Firegholiness10", "Fire Spirit of Tranquility",
            Icon2Texture, "864,36", 99),
        new(9090, "Zephyrholiness1", "Daedalus Spirit of Attunement",
            Icon5Texture, "648,0", 99),
        new(9091, "Zephyrholiness2", "Hephaestus Spirit of Tempering",
            Icon5Texture, "684,0", 99),
        new(9092, "Zephyrholiness3", "Mnemosyne Spirit of Preservation",
            Icon5Texture, "720,0", 99),
        new(9093, "Zephyrholiness4", "Themis Spirit of Continuity",
            Icon5Texture, "756,0", 99)
    ];

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        ReviewedItems.Select(Create).ToArray();

    private static ItemTemplateSeed Create(ReviewedItem item)
    {
        var stats = new Dictionary<string, string>
        {
            ["ID"] = item.Id.ToString(CultureInfo.InvariantCulture),
            ["Type"] = ItemType,
            ["Texture"] = item.Texture,
            ["Icon"] = item.Icon,
            ["Random"] = "0",
            ["Distribution"] = "0,0",
            ["Money"] = "0",
            ["Overlap"] = item.Overlap.ToString(
                CultureInfo.InvariantCulture)
        };
        if (item.SpecialFlag is not null)
        {
            stats.Add("SpecialFlag", item.SpecialFlag);
        }

        return new ItemTemplateSeed(
            item.Id,
            ItemType,
            item.NameKey,
            item.DisplayName,
            EquipmentSlot: 0,
            ClassIds: [],
            MinLevel: null,
            MaxLevel: null,
            Hand: null,
            SkillFlag: null,
            item.Texture,
            item.Icon,
            JsonSerializer.Serialize(stats));
    }

    private sealed record ReviewedItem(
        int Id,
        string NameKey,
        string DisplayName,
        string Texture,
        string Icon,
        short Overlap,
        string? SpecialFlag = null);
}
