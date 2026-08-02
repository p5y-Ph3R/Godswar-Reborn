using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Items;

/// <summary>
/// Reviewed original-client Holy Suit content used only by the publication
/// boundary. Runtime gameplay consumes the sealed PostgreSQL revision.
/// </summary>
internal static class HolySuitContentBaseline
{
    private const string ItemSource =
        "ItemBaseAttribute.xml + EquipName.dat + EquipDescription.dat";
    private const string TierSource =
        "EquipSuitInfoIni.xml + ItemBaseAttribute.xml";
    private const string UpgradeSource = "EquipEffect.xml";
    private const string Icon2 =
        "./Localization/en_us/UI/Texture/Icon2.gwo";

    public static IReadOnlyList<ItemTemplateSeed> ItemTemplates { get; } =
    [
        Item(9010, "Shenqi9010", "Bronze Ware", "0,0", 0, "0,0", 99),
        Item(9011, "Shenqi9011", "Silver Ware", "36,0", 0, "0,0", 99),
        Item(9012, "Shenqi9012", "Gold Ware", "72,0", 0, "0,0", 99),
        Item(9013, "Shenqi9013", "Platinum Ingot", "936,0", 0, "0,0", 99),
        Item(9014, "Shenqi9014", "Mithril Ingot", "972,0", 0, "0,0", 99),
        Item(9015, "Shenqi9015", "Orichalcum Ingot", "936,36", 0, "0,0", 99),
        Item(9016, "Shenqi9016", "Adamantite", "972,36", 0, "0,0", 99),
        Item(9020, "Congregation1", "Holy Box I", "108,0", 25, "100,200", 1, true),
        Item(9021, "Congregation2", "Holy Box II", "144,0", 15, "100,200", 1, true),
        Item(9022, "Congregation3", "Holy Box III", "180,0", 5, "150,200", 1, true),
        Item(9023, "Congregation4", "Holy Box IV", "216,0", 2, "150,200", 1, true),
        Item(9024, "Congregation5", "Holy Box V", "180,72", 2, "150,200", 1, true),
        Item(9025, "Congregation6", "Experience Prism", "216,72", 2, "150,200", 99)
    ];

    public static IReadOnlyList<HolySuitTierDefinition> Tiers { get; } =
    [
        new(0, "Common", 0, null, TierSource),
        new(1, "Bronze", 10, 9010, TierSource),
        new(2, "Silver", 10, 9011, TierSource),
        new(3, "Gold", 10, 9012, TierSource),
        new(4, "Platinum", 10, 9013, TierSource),
        new(5, "Mithril", 10, 9014, TierSource),
        new(6, "Orichalcum", 10, 9015, TierSource),
        new(7, "Adamantium", 10, 9016, TierSource)
    ];

    public static IReadOnlyList<HolySuitConsumableDefinition> Consumables
        { get; } =
    [
        Ware(9010, 1), Ware(9011, 2), Ware(9012, 3), Ware(9013, 4),
        Ware(9014, 5), Ware(9015, 6), Ware(9016, 7),
        Box(9020, 100_000),
        Box(9021, 1_000_000),
        Box(9022, 10_000_000),
        Box(9023, 100_000_000),
        Box(9024, 400_000_000),
        new(9025, HolySuitConsumableRole.ExperiencePrism, null,
            100_000_000, 99, 1, ItemSource)
    ];

    public static HolySuitOperationPolicy OperationPolicy { get; } = new(
        MinimumPlayerLevel: 70,
        MinimumGearLevel: 70,
        LegacyDailyExperiencePerPlayerLevel: 1_000_000,
        DailyExperiencePerPlayer: 2_000_000_000,
        PerOperationExperienceMaximum: 400_000_000,
        GearExperienceCapacity: 2_000_000_000,
        ExperiencePrismCost: 100_000_000,
        RealmDayTimeZone: "Asia/Singapore",
        DailyQuotaBypassEntitlement: "battle_pass",
        Source: "alpha-policy-2026-08-02-box-capacity");

    public static IReadOnlyList<HolySuitUpgradeDefinition> Upgrades { get; } =
    [
        Upgrade(0, 0, 1, 1, 9_688),
        Upgrade(1, 1, 1, 2, 58_127),
        Upgrade(1, 2, 1, 3, 174_380),
        Upgrade(1, 3, 1, 4, 348_759),
        Upgrade(1, 4, 1, 5, 581_265),
        Upgrade(1, 5, 1, 6, 4_198_026),
        Upgrade(1, 6, 1, 7, 4_843_876),
        Upgrade(1, 7, 1, 8, 5_489_727),
        Upgrade(1, 8, 1, 9, 6_458_502),
        Upgrade(1, 9, 1, 10, 7_427_277),
        Upgrade(1, 10, 2, 1, 3_875_101),
        Upgrade(2, 1, 2, 2, 5_649_898),
        Upgrade(2, 2, 2, 3, 9_416_496),
        Upgrade(2, 3, 2, 4, 14_647_883),
        Upgrade(2, 4, 2, 5, 18_832_991),
        Upgrade(2, 5, 2, 6, 23_018_100),
        Upgrade(2, 6, 2, 7, 27_278_774),
        Upgrade(2, 7, 2, 8, 31_475_509),
        Upgrade(2, 8, 2, 9, 35_672_243),
        Upgrade(2, 9, 2, 10, 41_967_345),
        Upgrade(2, 10, 3, 1, 57_661_505),
        Upgrade(3, 1, 3, 2, 61_505_605),
        Upgrade(3, 2, 3, 3, 65_349_705),
        Upgrade(3, 3, 3, 4, 69_193_805),
        Upgrade(3, 4, 3, 5, 73_037_906),
        Upgrade(3, 5, 3, 6, 76_882_006),
        Upgrade(3, 6, 3, 7, 80_726_106),
        Upgrade(3, 7, 3, 8, 88_414_306),
        Upgrade(3, 8, 3, 9, 96_102_508),
        Upgrade(3, 9, 3, 10, 100_000_000),
        Upgrade(3, 10, 4, 1, 133_833_876),
        Upgrade(4, 1, 4, 2, 155_886_267),
        Upgrade(4, 2, 4, 3, 184_272_577),
        Upgrade(4, 3, 4, 4, 234_454_153),
        Upgrade(4, 4, 4, 5, 295_435_479),
        Upgrade(4, 5, 4, 6, 373_355_467),
        Upgrade(4, 6, 4, 7, 485_358_297),
        Upgrade(4, 7, 4, 8, 616_532_565),
        Upgrade(4, 8, 4, 9, 735_697_878),
        Upgrade(4, 9, 4, 10, 866_697_995),
        Upgrade(4, 10, 5, 1, 999_999_999),
        PrismUpgrade(5, 1, 5, 2, 12),
        PrismUpgrade(5, 2, 5, 3, 15),
        PrismUpgrade(5, 3, 5, 4, 18),
        PrismUpgrade(5, 4, 5, 5, 21),
        PrismUpgrade(5, 5, 5, 6, 24),
        PrismUpgrade(5, 6, 5, 7, 27),
        PrismUpgrade(5, 7, 5, 8, 30),
        PrismUpgrade(5, 8, 5, 9, 33),
        PrismUpgrade(5, 9, 5, 10, 36),
        PrismUpgrade(5, 10, 6, 1, 39),
        PrismUpgrade(6, 1, 6, 2, 42),
        PrismUpgrade(6, 2, 6, 3, 45),
        PrismUpgrade(6, 3, 6, 4, 48),
        PrismUpgrade(6, 4, 6, 5, 51),
        PrismUpgrade(6, 5, 6, 6, 54),
        PrismUpgrade(6, 6, 6, 7, 57),
        PrismUpgrade(6, 7, 6, 8, 60),
        PrismUpgrade(6, 8, 6, 9, 63),
        PrismUpgrade(6, 9, 6, 10, 66),
        PrismUpgrade(6, 10, 7, 1, 69),
        PrismUpgrade(7, 1, 7, 2, 72),
        PrismUpgrade(7, 2, 7, 3, 75),
        PrismUpgrade(7, 3, 7, 4, 78),
        PrismUpgrade(7, 4, 7, 5, 81),
        PrismUpgrade(7, 5, 7, 6, 84),
        PrismUpgrade(7, 6, 7, 7, 87),
        PrismUpgrade(7, 7, 7, 8, 90),
        PrismUpgrade(7, 8, 7, 9, 93),
        PrismUpgrade(7, 9, 7, 10, 96)
    ];

    private static ItemTemplateSeed Item(
        int id,
        string nameKey,
        string displayName,
        string icon,
        int random,
        string distribution,
        short stackCap,
        bool experienceBall = false)
    {
        var stats = new Dictionary<string, string>
        {
            ["ID"] = id.ToString(),
            ["Type"] = "consume item",
            ["Texture"] = Icon2,
            ["Icon"] = icon,
            ["Random"] = random.ToString(),
            ["Distribution"] = distribution,
            ["Money"] = "0",
            ["Overlap"] = stackCap.ToString()
        };
        if (experienceBall)
        {
            stats["SpecialFlag"] = "ExpBall";
        }

        return new ItemTemplateSeed(
            id, "consume item", nameKey, displayName, 0, [], null, null,
            null, null, Icon2, icon, JsonSerializer.Serialize(stats));
    }

    private static HolySuitConsumableDefinition Ware(uint itemId, short type) =>
        new(itemId, HolySuitConsumableRole.Ware, type, 0, 99, 0,
            ItemSource);

    private static HolySuitConsumableDefinition Box(uint itemId, long capacity) =>
        new(itemId, HolySuitConsumableRole.HolyBox, null, capacity, 1, 1,
            ItemSource);

    private static HolySuitUpgradeDefinition Upgrade(
        short currentType,
        short currentLevel,
        short targetType,
        short targetLevel,
        long experience) =>
        new(currentType, currentLevel, targetType, targetLevel, experience,
            checked((uint)(9009 + targetType)), targetLevel, 0,
            UpgradeSource);

    private static HolySuitUpgradeDefinition PrismUpgrade(
        short currentType,
        short currentLevel,
        short targetType,
        short targetLevel,
        int prisms) =>
        new(currentType, currentLevel, targetType, targetLevel, 0,
            checked((uint)(9009 + targetType)), targetLevel, prisms,
            UpgradeSource);
}
