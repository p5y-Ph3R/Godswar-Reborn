using System.Text.Json;

namespace Godswar.Server.State;

internal enum GearEnhancementMaterialKind
{
    AttributeStone,
    QuartzPlate,
    FlameSpark,
    WaterGrain
}

internal sealed record GearEnhancementMaterialDefinition(
    uint ItemId,
    string NameKey,
    string DisplayName,
    GearEnhancementMaterialKind Kind,
    string Texture,
    string Icon,
    short StackCap,
    int Random,
    string Distribution,
    string? AttributeName = null,
    IReadOnlyList<int>? AttributeChain = null,
    bool CanEnhance = false,
    short? SourceAttributeLevel = null,
    short? TargetAttributeLevel = null)
{
    public IReadOnlyList<int> AllowedAttributeIds => AttributeChain ?? [];

    public ItemTemplateSeed ToItemTemplateSeed()
    {
        var stats = new Dictionary<string, string>
        {
            ["ID"] = ItemId.ToString(),
            ["Type"] = GearEnhancementMaterialCatalog.ConsumeItemType,
            ["Texture"] = Texture,
            ["Icon"] = Icon,
            ["Random"] = Random.ToString(),
            ["Distribution"] = Distribution,
            ["Money"] = "0",
            ["Overlap"] = StackCap.ToString()
        };

        return new ItemTemplateSeed(
            checked((int)ItemId),
            GearEnhancementMaterialCatalog.ConsumeItemType,
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

internal static class GearEnhancementMaterialCatalog
{
    public const string ConsumeItemType = "consume item";
    public const short ShippedStackCap = 99;
    public const uint FlameSparkItemId = 9990;
    public const uint WaterGrainItemId = 9991;

    private const string Icon2 = "./Localization/en_us/UI/Texture/Icon2.gwo";
    private const string Icon3 = "./Localization/en_us/UI/Texture/Icon3.gwo";

    // Source of IDs, keys, icons, Random, Distribution, and Overlap:
    // Localization/en_us/Settings/Sys/ItemBaseAttribute.xml, Material1..45,
    // Macadam1..4, and Rmacadam1..2. Display names come from EquipName.dat;
    // the attribute families come from EquipDescription.dat and are joined to
    // ItemAppendAttribute.xml IDs. The shipped table deliberately has no 9939.
    public static IReadOnlyList<GearEnhancementMaterialDefinition> All { get; } =
    [
        Stone(9930, "Material1", "Strength Stone", "Physical Attack", Icon2, "504,468", ShippedStackCap, Chain(0, 4), canEnhance: true),
        Stone(9931, "Material2", "Shield Stone", "Physical Defense", Icon2, "540,468", ShippedStackCap, Chain(10, 14), canEnhance: true),
        Stone(9932, "Material3", "Magic Stone", "Magical Attack", Icon2, "576,468", ShippedStackCap, Chain(20, 24), canEnhance: true),
        Stone(9933, "Material4", "Spell Stone", "Magical Defense", Icon2, "612,468", ShippedStackCap, Chain(30, 34), canEnhance: true),
        Stone(9934, "Material5", "Absorption Stone", "Damage Absorption", Icon2, "648,468", ShippedStackCap, Chain(100, 104), canEnhance: true),
        Stone(9935, "Material6", "Health Stone", "Maximum HP", Icon2, "684,468", ShippedStackCap, Chain(130, 134), canEnhance: true),
        Stone(9936, "Material7", "Mana Stone", "Maximum MP", Icon2, "720,468", ShippedStackCap, Chain(140, 144), canEnhance: true),
        Stone(9937, "Material8", "Blood Stone", "HP Restoration Speed", Icon2, "756,468", ShippedStackCap, Chain(150, 154), canEnhance: true),
        Stone(9938, "Material9", "Vigor Stone", "MP Restoration Speed", Icon2, "792,468", ShippedStackCap, Chain(160, 164), canEnhance: true),

        Stone(9940, "Material10", "Accuracy Stone", "Hit", Icon2, "504,540", ShippedStackCap, [40]),
        Stone(9941, "Material11", "Psychic Stone", "Dodge", Icon2, "540,540", ShippedStackCap, [50]),
        Stone(9942, "Material12", "Fury Stone", "Crit Bonus", Icon2, "576,540", ShippedStackCap, [60]),
        Stone(9943, "Material13", "Tenacity Stone", "Crit Resistance", Icon2, "612,540", ShippedStackCap, [70]),
        Stone(9944, "Material14", "Impact Stone", "Physical Damage", Icon2, "648,540", ShippedStackCap, [80]),
        Stone(9945, "Material15", "Fervor Stone", "Magical Damage", Icon2, "684,540", ShippedStackCap, [90]),
        Stone(9946, "Material16", "Punishment Stone", "Status Success", Icon2, "720,540", ShippedStackCap, [110]),
        Stone(9947, "Material17", "Purge Stone", "Status Resistance", Icon2, "756,540", ShippedStackCap, [120]),
        Stone(9948, "Material18", "Guard Stone", "Healing Received", Icon2, "792,540", ShippedStackCap, [170]),
        Stone(9949, "Material19", "Restoration Stone", "Healing Done", Icon2, "828,540", ShippedStackCap, [180]),

        Stone(9950, "Material20", "Primal Stone", "Melee Physical Attack", Icon2, "504,576", 1, [200]),
        Stone(9951, "Material21", "Courage Stone", "Melee Hit", Icon2, "540,576", 1, [201]),
        Stone(9952, "Material22", "Energy Stone", "Melee Physical Damage", Icon2, "576,576", 1, [210]),
        Stone(9953, "Material23", "Rage Stone", "Melee Critical Chance", Icon2, "612,576", 1, [211]),
        Stone(9954, "Material24", "Holy Stone", "Caster Magical Attack", Icon2, "648,576", 1, [220]),
        Stone(9955, "Material25", "Blessing Stone", "Caster Healing", Icon2, "684,576", 1, [221]),
        Stone(9956, "Material26", "Rune Stone", "Caster Magical Damage", Icon2, "720,576", 1, [230]),
        Stone(9957, "Material27", "Force Stone", "Caster Critical Chance", Icon2, "756,576", 1, [231]),
        Stone(9958, "Material28", "Spirit of Destruction", "Disable Physical Defense", Icon2, "864,540", ShippedStackCap, [240]),
        Stone(9959, "Material29", "Spirit of Penetration", "Disable Magical Defense", Icon2, "900,540", ShippedStackCap, [250]),

        Quartz(9960, "Macadam1", "Quartz Plate 1", "828,468", random: 20, "100,200", sourceLevel: 1, targetLevel: 2),
        Quartz(9961, "Macadam2", "Quartz Plate 2", "864,468", random: 5, "100,200", sourceLevel: 2, targetLevel: 3),
        Quartz(9962, "Macadam3", "Quartz Plate 3", "900,468", random: 0, "150,200", sourceLevel: 3, targetLevel: 4),
        Quartz(9963, "Macadam4", "Quartz Plate 4", "936,468", random: 1, "202,202", sourceLevel: 4, targetLevel: 5),

        Stone(9970, "Material30", "Stone of Vitality", "Maximum Health", Icon3, "288,0", ShippedStackCap, Chain(300, 307)),
        Stone(9971, "Material31", "Stone of Wisdom", "Maximum Mana", Icon3, "250,0", ShippedStackCap, Chain(310, 317)),
        Stone(9972, "Material32", "Stone of Precision", "Hit Rating", Icon3, "468,72", ShippedStackCap, Chain(320, 327)),
        Stone(9973, "Material33", "Stone of Evasion", "Dodge Rating", Icon3, "432,72", ShippedStackCap, Chain(330, 337)),
        Stone(9974, "Material34", "Stone of Strength", "Physical Attack", Icon3, "468,0", ShippedStackCap, Chain(340, 347)),
        Stone(9975, "Material35", "Stone of Sorcery", "Magical Attack", Icon3, "396,0", ShippedStackCap, Chain(350, 357)),
        Stone(9976, "Material36", "Stone of Wrath", "Physical Damage Percent", Icon3, "324,72", ShippedStackCap, Chain(360, 367)),
        Stone(9977, "Material37", "Stone of Arcana", "Magical Damage Percent", Icon3, "288,72", ShippedStackCap, Chain(370, 377)),
        Stone(9978, "Material38", "Stone of Renewal", "Health Regeneration", Icon3, "216,0", ShippedStackCap, Chain(380, 387)),
        Stone(9979, "Material39", "Stone of Serenity", "Mana Regeneration", Icon3, "180,0", ShippedStackCap, Chain(390, 397)),
        Stone(9980, "Material40", "Stone of Ruin", "Disable Physical Defense", Icon3, "108,72", ShippedStackCap, Chain(400, 407)),
        Stone(9981, "Material41", "Stone of Negation", "Disable Magical Defense", Icon3, "72,72", ShippedStackCap, Chain(410, 417)),
        Stone(9982, "Material42", "Stone of Force", "Flat Physical Damage", Icon3, "468,108", ShippedStackCap, Chain(420, 427)),
        Stone(9983, "Material43", "Stone of Essence", "Flat Magical Damage", Icon3, "324,108", ShippedStackCap, Chain(430, 437)),
        Stone(9984, "Material44", "Stone of Fury", "Critical Hit Percent", Icon3, "360,108", ShippedStackCap, Chain(440, 447)),
        Stone(9985, "Material45", "Stone of Impact", "Flat Critical Damage", Icon3, "216,108", ShippedStackCap, Chain(450, 457)),

        new(FlameSparkItemId, "Rmacadam1", "Flame Spark", GearEnhancementMaterialKind.FlameSpark,
            Icon2, "972,468", ShippedStackCap, 0, "100,200"),
        new(WaterGrainItemId, "Rmacadam2", "Water Grain", GearEnhancementMaterialKind.WaterGrain,
            Icon2, "972,504", ShippedStackCap, 0, "100,200")
    ];

    public static IReadOnlyList<GearEnhancementMaterialDefinition> AttributeStones { get; } =
        All.Where(static material => material.Kind == GearEnhancementMaterialKind.AttributeStone).ToArray();

    private static readonly IReadOnlyDictionary<uint, GearEnhancementMaterialDefinition> ByItemId =
        All.ToDictionary(static material => material.ItemId);

    public static bool TryGet(uint itemId, out GearEnhancementMaterialDefinition material)
    {
        return ByItemId.TryGetValue(itemId, out material!);
    }

    public static bool TryGetAttributeStone(uint itemId, out GearEnhancementMaterialDefinition stone)
    {
        return TryGet(itemId, out stone!) && stone.Kind == GearEnhancementMaterialKind.AttributeStone;
    }

    private static GearEnhancementMaterialDefinition Stone(
        uint itemId,
        string nameKey,
        string displayName,
        string attributeName,
        string texture,
        string icon,
        short stackCap,
        IReadOnlyList<int> attributeChain,
        bool canEnhance = false)
    {
        return new GearEnhancementMaterialDefinition(
            itemId,
            nameKey,
            displayName,
            GearEnhancementMaterialKind.AttributeStone,
            texture,
            icon,
            stackCap,
            Random: 0,
            Distribution: itemId is >= 9930 and <= 9949 ? "50,150" : "0,0",
            attributeName,
            attributeChain,
            canEnhance);
    }

    private static GearEnhancementMaterialDefinition Quartz(
        uint itemId,
        string nameKey,
        string displayName,
        string icon,
        int random,
        string distribution,
        short sourceLevel,
        short targetLevel)
    {
        return new GearEnhancementMaterialDefinition(
            itemId,
            nameKey,
            displayName,
            GearEnhancementMaterialKind.QuartzPlate,
            Icon2,
            icon,
            ShippedStackCap,
            random,
            distribution,
            SourceAttributeLevel: sourceLevel,
            TargetAttributeLevel: targetLevel);
    }

    private static IReadOnlyList<int> Chain(int first, int last)
    {
        return Enumerable.Range(first, checked(last - first + 1)).ToArray();
    }
}
