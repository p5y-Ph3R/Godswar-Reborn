using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class BagConsumableCooldownPolicyChecks
{
    public const string CheckName =
        "Stock bag-consumable cooldown policy";

    public static Task RunAsync()
    {
        var catalog = PinnedItemTemplateCatalog.Create(
            "bag-consumable-cooldown-check",
            [
                Consumable(4109, "1", "4720"),
                Consumable(10130, "1", "4721"),
                Consumable(10144, "1", "4721"),
                Consumable(10150, "1", "4740"),
                Consumable(4110, "1", "4501"),
                Consumable(3986, "1", "2993"),
                Consumable(10099, "1", null),
                Consumable(10100, null, null),
                Equipment(1000)
            ]);

        AssertRule(catalog, 4109, 4720, TimeSpan.FromSeconds(1));
        AssertRule(catalog, 10130, 4721, TimeSpan.FromSeconds(1));
        AssertRule(catalog, 10144, 4721, TimeSpan.FromSeconds(1));
        AssertRule(catalog, 10150, 4740, TimeSpan.FromSeconds(1));
        AssertRule(catalog, 4110, 4501, TimeSpan.FromSeconds(2));
        Check.True(
            !BagConsumableCooldownPolicy.TryResolve(
                catalog,
                3986,
                out _) &&
            !BagConsumableCooldownPolicy.TryResolve(
                catalog,
                10099,
                out _) &&
            !BagConsumableCooldownPolicy.TryResolve(
                catalog,
                10100,
                out _) &&
            !BagConsumableCooldownPolicy.TryResolve(
                catalog,
                1000,
                out _),
            "zero/missing timing, missing activation metadata, and equipment do not acquire a consumable cooldown");
        return Task.CompletedTask;
    }

    private static void AssertRule(
        IItemTemplateCatalog catalog,
        uint itemId,
        int group,
        TimeSpan duration)
    {
        Check.True(
            BagConsumableCooldownPolicy.TryResolve(
                catalog,
                itemId,
                out var rule) &&
            rule.Group == group &&
            rule.Duration == duration,
            $"item {itemId} resolves stock cooldown group {group} for {duration.TotalSeconds} seconds");
    }

    private static ItemTemplateDefinition Consumable(
        uint id,
        string? use,
        string? skill)
    {
        var properties = new List<string>
        {
            $"\"ID\":\"{id}\"",
            "\"Type\":\"consume item\""
        };
        if (use is not null)
        {
            properties.Add($"\"Use\":\"{use}\"");
        }
        if (skill is not null)
        {
            properties.Add($"\"Skill\":\"{skill}\"");
        }

        return new(
            id,
            "consume item",
            $"Item{id}",
            $"Item {id}",
            0,
            [],
            null,
            null,
            null,
            null,
            string.Empty,
            string.Empty,
            $"{{{string.Join(',', properties)}}}");
    }

    private static ItemTemplateDefinition Equipment(uint id) =>
        new(
            id,
            "weapon",
            $"Item{id}",
            $"Item {id}",
            10,
            [0],
            1,
            200,
            2,
            0,
            string.Empty,
            string.Empty,
            $"{{\"ID\":\"{id}\",\"Type\":\"weapon\",\"Use\":\"1\",\"Skill\":\"4721\"}}");
}
