using System.Text.Json;
using System.Xml.Linq;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.ProtocolChecks;

internal static class HolyStoneMaterialItemContentChecks
{
    public const string CheckName =
        "Immutable Holy Stone material content";

    public static Task RunAsync()
    {
        var seeds = HolyStoneMaterialItemContentBaseline.ItemTemplates;
        Check.Equal(
            ExpectedItems.Length,
            seeds.Count,
            "reviewed Holy Stone material count");
        Check.True(
            seeds.Select(static value => value.Id).Distinct().Count() ==
                ExpectedItems.Length &&
            seeds.Select(static value => value.Id)
                .SequenceEqual(ExpectedItems.Select(static value => value.Id)),
            "reviewed Holy Stone materials have the exact unique ordered IDs");

        foreach (var expected in ExpectedItems)
        {
            var seed = seeds.Single(value => value.Id == expected.Id);
            Check.True(
                seed.Kind == "consume item" &&
                seed.NameKey == expected.NameKey &&
                seed.DisplayName == expected.DisplayName &&
                seed.EquipmentSlot == 0 &&
                seed.ClassIds.Length == 0 &&
                seed.MinLevel is null &&
                seed.MaxLevel is null &&
                seed.Hand is null &&
                seed.SkillFlag is null &&
                seed.Texture == expected.Texture &&
                seed.Icon == expected.Icon,
                $"Holy Stone material {expected.Id} keeps client metadata");

            using var stats = JsonDocument.Parse(seed.StatsJson);
            var root = stats.RootElement;
            Check.True(
                root.GetProperty("ID").GetString() ==
                    expected.Id.ToString() &&
                root.GetProperty("Type").GetString() == "consume item" &&
                root.GetProperty("Texture").GetString() == expected.Texture &&
                root.GetProperty("Icon").GetString() == expected.Icon &&
                root.GetProperty("Random").GetString() == "0" &&
                root.GetProperty("Distribution").GetString() == "0,0" &&
                root.GetProperty("Money").GetString() == "0" &&
                root.GetProperty("Overlap").GetString() == expected.Overlap,
                $"Holy Stone material {expected.Id} keeps client stats");
            var hasSpecialFlag =
                root.TryGetProperty("SpecialFlag", out var specialFlag);
            Check.True(
                expected.SpecialFlag is null
                    ? !hasSpecialFlag
                    : hasSpecialFlag &&
                      specialFlag.GetString() == expected.SpecialFlag,
                $"Holy Stone material {expected.Id} keeps SpecialFlag");
            Check.Equal(
                expected.SpecialFlag is null ? 8 : 9,
                root.EnumerateObject().Count(),
                $"Holy Stone material {expected.Id} has no invented stats");
        }

        AssertClientItemsUseNativeItemSections();
        AssertZephyrResultLocalization();
        AssertZephyrResultDecoderInstaller();
        AssertZephyrSocketDisplayMetadata();

        return Task.CompletedTask;
    }

    private static void AssertZephyrResultLocalization()
    {
        var root = FindRepositoryRoot();
        var lines = File.ReadLines(Path.Combine(
            root,
            "Localization",
            "en_us",
            "UI",
            "Base",
            "LuaText.lua"));
        var keys = lines
            .Select(static line => line.Split('=', 2)[0].Trim())
            .Where(static key => key.Length > 0)
            .GroupBy(static key => key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);

        foreach (var key in ZephyrLuaTextKeys)
        {
            Check.True(
                keys.TryGetValue(key, out var count) && count == 1,
                $"Zephyr client result key {key} exists exactly once");
        }
    }

    private static void AssertZephyrResultDecoderInstaller()
    {
        var installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "PatchClientMountGearZephyrItems.ps1"));
        foreach (var fragment in ZephyrDecoderFragments)
        {
            Check.True(
                installer.Contains(fragment, StringComparison.Ordinal),
                $"Zephyr result decoder installer owns {fragment}");
        }
    }

    private static void AssertZephyrSocketDisplayMetadata()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "Localization",
            "en_us",
            "Settings",
            "Sys",
            "EquipStoneInfo.xml"));

        foreach (var expected in ZephyrSocketMetadata)
        {
            var matches = document.Root!
                .Elements()
                .Where(element =>
                    (string?)element.Attribute("ID") == expected.Id)
                .ToArray();
            Check.True(
                matches.Length == 1 &&
                matches[0].Name.LocalName == expected.Name &&
                (string?)matches[0].Attribute("Percent") == "1" &&
                (string?)matches[0].Attribute("Texture") ==
                    "./Localization/en_us/UI/Texture/Icon5.gwo" &&
                (string?)matches[0].Attribute("IconPos") == expected.Icon,
                $"Zephyr socket effect {expected.Id} has exact client display metadata");
        }

        var contentInstaller = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "PatchClientMountGearZephyrItems.ps1"));
        Check.True(
            contentInstaller.Contains(
                "PatchClientZephyrSocketDisplay.ps1",
                StringComparison.Ordinal),
            "Zephyr content installer owns socket display installation");
    }

    private static void AssertClientItemsUseNativeItemSections()
    {
        var root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(
            root,
            "Localization",
            "en_us",
            "Settings",
            "Sys",
            "ItemBaseAttribute.xml"));

        foreach (var expected in ExpectedItems)
        {
            var matches = document
                .Descendants()
                .Where(element =>
                    (string?)element.Attribute("ID") ==
                    expected.Id.ToString())
                .ToArray();
            Check.True(
                matches.Length == 1 &&
                matches[0].Parent?.Name.LocalName == "Item",
                $"Holy Stone material {expected.Id} is a native Item child");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate GodswarServer.sln.");
    }

    private const string Icon =
        "./Localization/en_us/UI/Texture/Icon.gwo";
    private const string Icon2 =
        "./Localization/en_us/UI/Texture/Icon2.gwo";
    private const string Icon5 =
        "./Localization/en_us/UI/Texture/Icon5.gwo";

    private static readonly string[] ZephyrLuaTextKeys =
    [
        "hallo_9032",
        "NF_L0_ZBXQ2103",
        "NF_L0_ZBXQ2203",
        "NF_L0_ZBXQ2303",
        "NF_L0_ZBXQ2403",
        "NF_L0_ZBXQ903204",
        "NF_L0_ZBXQ903205"
    ];

    private static readonly string[] ZephyrDecoderFragments =
    [
        "[21]={15,30},[22]={10,20},[23]={100,200},[24]={75,150}",
        "[21]=NF_L0_ZBXQ2103, [22]=NF_L0_ZBXQ2203, " +
            "[23]=NF_L0_ZBXQ2303, [24]=NF_L0_ZBXQ2403",
        "stil_2 ==21 or stil_2 ==22 or stil_2 ==23 or stil_2 ==24",
        "-ZephyrTextKey 'NF_L0_ZBXQ903204'",
        "-ZephyrTextKey 'NF_L0_ZBXQ903205'"
    ];

    private static readonly (string Id, string Name, string Icon)[]
        ZephyrSocketMetadata =
    [
        ("21", "ZephyrAttunement", "620,8"),
        ("22", "ZephyrTempering", "620,8"),
        ("23", "ZephyrManaBurnResistance", "620,8"),
        ("24", "ZephyrCooldownExtensionResistance", "620,8")
    ];

    private static readonly ExpectedItem[] ExpectedItems =
    [
        E(9030, "Stone9030", "Heated Holy Stone", Icon2, "252,0", "1",
            "PreStone"),
        E(9031, "Stone9031", "Cooled Holy Stone", Icon2, "288,0", "1",
            "PreStone"),
        E(9032, "Stone9032", "Zephyr Holy Stone", Icon5, "612,0", "1",
            "PreStone"),
        E(9040, "Stone9040", "Level 1 Eclipse Stone", Icon, "828,900"),
        E(9041, "Stone9041", "Level 2 Eclipse Stone", Icon, "864,900"),
        E(9042, "Stone9042", "Level 3 Eclipse Stone", Icon, "900,900"),
        E(9050, "Stone9050", "Goddess' Stone", Icon2, "324,0"),
        E(9051, "Stone9051", "Copper Evasion Signet", Icon, "540,936"),
        E(9052, "Stone9052", "Silver Evasion Signet", Icon, "576,936"),
        E(9053, "Stone9053", "Gold Evasion Signet", Icon, "612,936"),
        E(9054, "Stone9054", "Gold Evasion Signet", Icon, "612,936"),
        E(9055, "Stone9055", "Gold Evasion Signet", Icon, "612,936"),
        E(9056, "Stone9056", "Gold Evasion Signet", Icon, "612,936"),
        E(9060, "Firegholiness1", "Fire Spirit of Destruction", Icon2,
            "360,0"),
        E(9061, "Firegholiness2", "Fire Spirit of Penetration", Icon2,
            "396,0"),
        E(9062, "Firegholiness3", "Fire Spirit of Fist", Icon2, "432,0"),
        E(9063, "Firegholiness4", "Fire Spirit of Fiery", Icon2, "468,0"),
        E(9064, "Firegholiness5", "Fire Spirit of Blood", Icon2, "504,0"),
        E(9065, "Firegholiness6", "Fire Spirit of Pressure", Icon2,
            "540,0"),
        E(9066, "Firegholiness7", "Fire Spirit of Assail", Icon2, "864,0"),
        E(9067, "Firegholiness8", "Fire Spirit of Lightning", Icon2,
            "900,0"),
        E(9068, "Waterholiness9", "Water Spirit of Renewal", Icon2,
            "756,36"),
        E(9069, "Waterholiness10", "Water Spirit of Vitality", Icon2,
            "792,36"),
        E(9080, "Waterholiness1", "Water Spirit of Darkness", Icon2,
            "576,0"),
        E(9081, "Waterholiness2", "Water Spirit of Mist", Icon2, "612,0"),
        E(9082, "Waterholiness3", "Water Spirit of Silence", Icon2,
            "648,0"),
        E(9083, "Waterholiness4", "Water Spirit of Chillness", Icon2,
            "684,0"),
        E(9084, "Waterholiness5", "Water Spirit of Ice", Icon2, "720,0"),
        E(9085, "Waterholiness6", "Water Spirit of Frost", Icon2, "756,0"),
        E(9086, "Waterholiness7", "Water Spirit of Intent", Icon2, "792,0"),
        E(9087, "Waterholiness8", "Water Spirit of Resilience", Icon2,
            "828,0"),
        E(9088, "Firegholiness9", "Fire Spirit of Flow", Icon2, "828,36"),
        E(9089, "Firegholiness10", "Fire Spirit of Tranquility", Icon2,
            "864,36"),
        E(9090, "Zephyrholiness1", "Daedalus Spirit of Attunement",
            Icon5, "648,0"),
        E(9091, "Zephyrholiness2", "Hephaestus Spirit of Tempering",
            Icon5, "684,0"),
        E(9092, "Zephyrholiness3", "Mnemosyne Spirit of Preservation",
            Icon5, "720,0"),
        E(9093, "Zephyrholiness4", "Themis Spirit of Continuity",
            Icon5, "756,0")
    ];

    private static ExpectedItem E(
        int id,
        string nameKey,
        string displayName,
        string texture,
        string icon,
        string overlap = "99",
        string? specialFlag = null) =>
        new(id, nameKey, displayName, texture, icon, overlap, specialFlag);

    private sealed record ExpectedItem(
        int Id,
        string NameKey,
        string DisplayName,
        string Texture,
        string Icon,
        string Overlap,
        string? SpecialFlag);
}
