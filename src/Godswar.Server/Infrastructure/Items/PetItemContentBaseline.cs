using System.Globalization;
using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed stock-client pet consumables, Pet Manager materials, skill-slot
/// definitions, and inert talent-stick artifacts published as immutable item
/// content. Talents are innate aptitude capabilities; the stick records must
/// never be activatable.
/// </summary>
internal static class PetItemContentBaseline
{
    public const string ItemType = "consume item";
    public const string Texture =
        "./Localization/en_us/UI/Texture/Icon2.gwo";

    private static readonly ReviewedItem[] ReviewedItems =
    [
        new(
            4109,
            "AddPetNum",
            "Special Pet Shed",
            "432,972",
            1,
            BindType: 1,
            Skill: 4720,
            Mode: 4,
            Use: 1),
        new(10084, "Pet10084", "Mysterious Tuck Net", "900,936", 99,
            ItemType: 0, Skill: 4734, Use: 1),
        new(10099, "Pet10099", "Pet Enhance Spring", "648,936", 99, 5, Use: 1),
        new(10100, "Pet10100", "Golden Apple Juice", "504,936", 99, 1, Use: 1),
        new(10101, "Pet10101", "Strong Purge Potion", "612,936", 99),
        new(10102, "Pet10102", "Weak Purge Potion", "468,936", 99, 2, Use: 1),
        new(10103, "Pet10103", "Merged Spirit", "756,972", 99, 15),
        new(10104, "Pet10104", "Rebirth Spirit", "792,972", 99, 16),
        new(10105, "Pet10105", "Contract Spirit", "828,972", 99, 17),
        new(10106, "Pet10106", "Pixie Tear", "864,972", 99),
        new(10107, "Pet10107", "Spring Water", "900,972", 99, 12, Use: 1),
        new(10108, "Pet10108", "Seal Jade (Empty)", "936,972", 99),
        new(10109, "Pet10109", "Seal Jade(Packed)", "972,972", 1, 13,
            Use: 1),
        new(10110, "Pet10110", "Stick: Random Event", "720,936", 1),
        new(10111, "Pet10111", "Stick: Quest Dispatch", "720,936", 1),
        new(10112, "Pet10112", "Stick: Work", "720,936", 1),
        new(10113, "Pet10113", "Stick: Healing", "720,936", 1),
        new(10114, "Pet10114", "Stick: Merge", "720,936", 1),
        new(10130, "Pet10130", "Morning Dew 1", "0,756", 99, 18, 10_000, Skill: 4721, Use: 1),
        new(10131, "Pet10131", "Morning Dew 2", "36,756", 99, 18, 80_000, Skill: 4721, Use: 1),
        new(10132, "Pet10132", "Morning Dew 3", "72,756", 99, 18, 1_000_000, Skill: 4721, Use: 1),
        new(10133, "Pet10133", "Morning Dew 4", "108,756", 99, 18, 2_000_000, Skill: 4721, Use: 1),
        new(10134, "Pet10134", "Morning Dew 5", "144,756", 99, 18, 10_000_000, Skill: 4721, Use: 1),
        new(10140, "Pet10140", "Morning Dew 1 (Restricted)", "0,756", 99, 18, 10_000, Skill: 4721, Use: 1, PetLimit: 1),
        new(10141, "Pet10141", "Morning Dew 2 (Restricted)", "36,756", 99, 18, 80_000, Skill: 4721, Use: 1, PetLimit: 1),
        new(10142, "Pet10142", "Morning Dew 3 (Restricted)", "72,756", 99, 18, 1_000_000, Skill: 4721, Use: 1, PetLimit: 1),
        new(10143, "Pet10143", "Morning Dew 4 (Restricted)", "108,756", 99, 18, 2_000_000, Skill: 4721, Use: 1, PetLimit: 1),
        new(10144, "Pet10144", "Morning Dew 5 (Restricted)", "144,756", 99, 18, 10_000_000, Skill: 4721, Use: 1, PetLimit: 1),
        new(11003, "Pet11003", "Charm: Pet Call", "432,756", 1, 20,
            Skill: 4721, Use: 1),
        new(11004, "Pet11004", "Charm: Merge", "864,936", 1, 21,
            Skill: 4721, Use: 1),
        new(11015, "Pet11015", "Pet Gender Reverser", "72,900", 1,
            Texture: "./Localization/en_us/UI/Texture/Icon.gwo")
    ];

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        ReviewedItems
            .Select(Create)
            .Concat(PetSkillBookItemContentBaseline.ItemTemplates)
            .Concat(PetMagicJadeItemContentBaseline.ItemTemplates)
            .OrderBy(static value => value.Id)
            .ToArray();

    private static ItemTemplateSeed Create(ReviewedItem item)
    {
        var texture = item.Texture ?? Texture;
        var stats = new Dictionary<string, string>
        {
            ["ID"] = item.Id.ToString(CultureInfo.InvariantCulture),
            ["Type"] = ItemType,
            ["Texture"] = texture,
            ["Icon"] = item.Icon,
            ["Random"] = "0",
            ["Distribution"] = "0,0",
            ["Money"] = "0",
            ["Overlap"] = item.Overlap.ToString(CultureInfo.InvariantCulture)
        };
        if (item.ItemType is not null)
        {
            stats.Add(
                "ItemType",
                item.ItemType.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (item.Values is not null)
        {
            stats.Add(
                "Values",
                item.Values.Value.ToString(CultureInfo.InvariantCulture));
        }
        AddOptional(stats, "Use", item.Use);
        AddOptional(stats, "BindType", item.BindType);
        AddOptional(stats, "Skill", item.Skill);
        AddOptional(stats, "Mode", item.Mode);
        AddOptional(stats, "Petlimit", item.PetLimit);

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
            texture,
            item.Icon,
            JsonSerializer.Serialize(stats));
    }

    private static void AddOptional(
        IDictionary<string, string> stats,
        string name,
        short? value)
    {
        if (value is { } present)
        {
            stats.Add(
                name,
                present.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddOptional(
        IDictionary<string, string> stats,
        string name,
        long? value)
    {
        if (value is { } present)
        {
            stats.Add(
                name,
                present.ToString(CultureInfo.InvariantCulture));
        }
    }

    private sealed record ReviewedItem(
        int Id,
        string NameKey,
        string DisplayName,
        string Icon,
        short Overlap,
        short? ItemType = null,
        long? Values = null,
        short? BindType = null,
        short? Skill = null,
        short? Mode = null,
        short? Use = null,
        short? PetLimit = null,
        string? Texture = null);
}
