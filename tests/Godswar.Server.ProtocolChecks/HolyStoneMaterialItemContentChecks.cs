using System.Text.Json;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.ProtocolChecks;

internal static class HolyStoneMaterialItemContentChecks
{
    public const string CheckName =
        "Immutable stock Holy Stone material content";

    public static Task RunAsync()
    {
        var seeds = HolyStoneMaterialItemContentBaseline.ItemTemplates;
        Check.Equal(32, seeds.Count, "stock Holy Stone material count");
        Check.True(
            seeds.Select(static value => value.Id).Distinct().Count() == 32 &&
            seeds.Select(static value => value.Id)
                .SequenceEqual(seeds.Select(static value => value.Id).Order()),
            "stock Holy Stone materials have unique ordered IDs");

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

        return Task.CompletedTask;
    }

    private const string Icon =
        "./Localization/en_us/UI/Texture/Icon.gwo";
    private const string Icon2 =
        "./Localization/en_us/UI/Texture/Icon2.gwo";

    private static readonly ExpectedItem[] ExpectedItems =
    [
        E(9030, "Stone9030", "Heated Holy Stone", Icon2, "252,0", "1",
            "PreStone"),
        E(9031, "Stone9031", "Cooled Holy Stone", Icon2, "288,0", "1",
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
            "864,36")
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
