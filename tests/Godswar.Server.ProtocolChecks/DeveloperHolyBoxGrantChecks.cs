using Godswar.Server.Application.Items;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class DeveloperHolyBoxGrantChecks
{
    public const string CheckName =
        "Database-derived empty Holy Box developer grants";

    public static Task RunAsync()
    {
        var content = TestItemContent.HolySuitContent;
        var expectedIds = new uint[] { 9020, 9021, 9022, 9023, 9024 };
        for (var index = 0; index < expectedIds.Length; index++)
        {
            var ordinal = index + 1;
            var expectedId = expectedIds[index];
            Check.True(
                content.DeveloperItems.TryResolveDeveloper(
                    $"holybox{ordinal}",
                    out var shortAlias) &&
                shortAlias.ItemId == expectedId &&
                shortAlias.StackCap == 1 &&
                shortAlias.GrantedBound == 1,
                $"holybox{ordinal} resolves from the pinned Holy Suit revision");
            Check.True(
                content.DeveloperItems.TryResolveDeveloper(
                    $"emptyholybox{ordinal}",
                    out var explicitAlias) &&
                explicitAlias == shortAlias,
                $"emptyholybox{ordinal} resolves to the same empty box");
            Check.True(
                content.DeveloperItems.TryResolveDeveloper(
                    expectedId,
                    out var numeric) &&
                numeric == shortAlias,
                $"Holy Box item {expectedId} resolves numerically");
        }

        Check.True(
            !content.DeveloperItems.TryResolveDeveloper(9010, out _) &&
            !content.DeveloperItems.TryResolveDeveloper(9025, out _),
            "wares and Experience Prisms are not implicitly developer-grantable");

        Check.True(
            DeveloperItemCommand.TryParse(
                "/gmitem add holybox1 1",
                out var request,
                out var error,
                content.DeveloperMounts,
                content.DeveloperItems) &&
            string.IsNullOrEmpty(error) &&
            request is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 9020,
                Quantity: 1
            },
            "the stock-client gmitem spelling parses an empty Holy Box grant");
        return Task.CompletedTask;
    }
}
