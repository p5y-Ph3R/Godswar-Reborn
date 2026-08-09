using Godswar.Server.Application.Items;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class DeveloperCostumeGrantChecks
{
    public const string CheckName =
        "Narrow permanent costume developer grant";

    public static Task RunAsync()
    {
        const uint costumeItemId = 8068;
        var content = TestItemContent.Content;
        Check.True(
            content.DeveloperItems.TryResolveDeveloper(
                costumeItemId,
                out var numeric) &&
            numeric.ItemId == costumeItemId &&
            numeric.DisplayName == "Christmas Suit(perpetual)" &&
            numeric.StackCap == 1 &&
            numeric.GrantedBound == 1,
            "reviewed permanent costume resolves numerically");

        foreach (var alias in new[]
                 {
                     "christmassuit",
                     "Christmas Suit(perpetual)",
                     "costume8068"
                 })
        {
            Check.True(
                content.DeveloperItems.TryResolveDeveloper(
                    alias,
                    out var resolved) &&
                resolved == numeric,
                $"permanent costume alias '{alias}' resolves exactly");
        }

        Check.True(
            !content.DeveloperItems.TryResolveDeveloper(8067, out _) &&
            !content.DeveloperItems.TryResolveDeveloper(8069, out _),
            "adjacent costumes remain outside the developer allowlist");

        var operationId = Guid.NewGuid();
        Check.True(
            DeveloperItemCommand.TryParse(
                $"/item add christmassuit 1 op={operationId:D}",
                out var request,
                out var error,
                content.DeveloperMounts,
                content.DeveloperItems) &&
            string.IsNullOrEmpty(error) &&
            request is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: costumeItemId,
                Material.StackCap: 1,
                Material.GrantedBound: 1,
                Quantity: 1,
                ClientOperationId: not null
            } &&
            request.ClientOperationId == operationId,
            "costume command retains its authoritative operation identity");
        return Task.CompletedTask;
    }
}
