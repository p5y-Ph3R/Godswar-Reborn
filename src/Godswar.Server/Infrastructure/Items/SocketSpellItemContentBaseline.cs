using System.Globalization;
using System.Text.Json;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed stock-client Socket Spell definitions used only while publishing
/// immutable item content. Runtime and developer commands consume the sealed
/// PostgreSQL projection instead of this compiled baseline.
/// </summary>
internal static class SocketSpellItemContentBaseline
{
    public const uint FirstItemId = 4270;
    public const uint LastItemId = 4273;
    public const short StackCap = 99;
    public const string ItemType = "consume item";
    public const string Texture =
        "./Localization/en_us/UI/Texture/Icon.gwo";
    public const string Icon = "108,900";

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
        Enumerable.Range(0, 4)
            .Select(Create)
            .ToArray();

    private static ItemTemplateSeed Create(int index)
    {
        var itemId = checked((int)FirstItemId + index);
        var ordinal = index + 1;
        var stats = new Dictionary<string, string>
        {
            ["ID"] = itemId.ToString(CultureInfo.InvariantCulture),
            ["Type"] = ItemType,
            ["Texture"] = Texture,
            ["Icon"] = Icon,
            ["Random"] = "0",
            ["Distribution"] = "0,0",
            ["Money"] = "0",
            ["Overlap"] = StackCap.ToString(CultureInfo.InvariantCulture)
        };
        return new ItemTemplateSeed(
            itemId,
            ItemType,
            $"Smithing{itemId}",
            $"Socket Spell {RomanOrdinal(ordinal)}",
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

    private static string RomanOrdinal(int ordinal) => ordinal switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        _ => throw new ArgumentOutOfRangeException(nameof(ordinal))
    };
}
