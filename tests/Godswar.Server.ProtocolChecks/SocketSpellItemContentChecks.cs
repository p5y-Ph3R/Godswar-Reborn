using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Items;

namespace Godswar.Server.ProtocolChecks;

internal static class SocketSpellItemContentChecks
{
    public const string CheckName =
        "Immutable Socket Spell content and developer grants";

    public static Task RunAsync()
    {
        var seeds = SocketSpellItemContentBaseline.ItemTemplates;
        Check.Equal(4, seeds.Count, "stock Socket Spell count");
        for (var index = 0; index < seeds.Count; index++)
        {
            var itemId = 4270 + index;
            var ordinal = index + 1;
            var expectedName = ordinal switch
            {
                1 => "Socket Spell I",
                2 => "Socket Spell II",
                3 => "Socket Spell III",
                4 => "Socket Spell IV",
                _ => throw new InvalidOperationException()
            };
            var seed = seeds[index];
            Check.True(
                seed.Id == itemId &&
                seed.Kind == "consume item" &&
                seed.NameKey == $"Smithing{itemId}" &&
                seed.DisplayName == expectedName &&
                seed.EquipmentSlot == 0 &&
                seed.ClassIds.Length == 0 &&
                seed.MinLevel is null &&
                seed.MaxLevel is null &&
                seed.Hand is null &&
                seed.SkillFlag is null &&
                seed.Texture ==
                    "./Localization/en_us/UI/Texture/Icon.gwo" &&
                seed.Icon == "108,900",
                $"Socket Spell {ordinal} keeps stock-client metadata");
            using var stats = JsonDocument.Parse(seed.StatsJson);
            Check.True(
                stats.RootElement.GetProperty("ID").GetString() ==
                    itemId.ToString() &&
                stats.RootElement.GetProperty("Type").GetString() ==
                    "consume item" &&
                stats.RootElement.GetProperty("Overlap").GetString() == "99" &&
                stats.RootElement.GetProperty("Money").GetString() == "0" &&
                !stats.RootElement.TryGetProperty("BindType", out _),
                $"Socket Spell {ordinal} is a native unbound stack of 99");
        }

        var developerItems = TestItemContent.Content.DeveloperItems;
        for (var ordinal = 1; ordinal <= 4; ordinal++)
        {
            var itemId = checked((uint)(4269 + ordinal));
            Check.True(
                developerItems.TryResolveDeveloper(
                    $"socketspell{ordinal}",
                    out var alias) &&
                alias.ItemId == itemId &&
                alias.StackCap == 99 &&
                alias.GrantedBound == 0,
                $"socketspell{ordinal} resolves from pinned item content");
            Check.True(
                developerItems.TryResolveDeveloper(itemId, out var numeric) &&
                numeric == alias,
                $"Socket Spell {itemId} resolves by numeric ID");
        }

        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add socketspell1 99",
                out var compact,
                out var compactError,
                TestItemContent.Content.DeveloperMounts,
                developerItems) &&
            string.IsNullOrEmpty(compactError) &&
            compact is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 4270,
                Quantity: 99
            },
            "developer command accepts compact Socket Spell aliases");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add socketspell 4 99",
                out var split,
                out var splitError,
                TestItemContent.Content.DeveloperMounts,
                developerItems) &&
            string.IsNullOrEmpty(splitError) &&
            split is
            {
                Material.ItemId: 4273,
                Quantity: 99
            },
            "developer command accepts split Socket Spell aliases");
        Check.True(
            DeveloperItemCommand.TryParse(
                "/item add 4272 99",
                out var numericRequest,
                out var numericError,
                TestItemContent.Content.DeveloperMounts,
                developerItems) &&
            string.IsNullOrEmpty(numericError) &&
            numericRequest is
            {
                Material.ItemId: 4272,
                Quantity: 99
            },
            "developer command accepts allowlisted Socket Spell IDs");
        Check.True(
            !developerItems.TryResolveDeveloper(4274, out _),
            "developer catalog does not widen past the four stock spells");
        return Task.CompletedTask;
    }
}
