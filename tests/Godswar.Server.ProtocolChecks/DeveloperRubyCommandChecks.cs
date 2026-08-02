using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class DeveloperRubyCommandChecks
{
    public const string CheckName =
        "Dedicated Ruby developer command parsing";

    private static readonly Guid OperationId =
        Guid.Parse("35b344af-7906-4e71-ac04-d2eaf501de63");

    public static Task RunAsync()
    {
        var expectedRubies = new (int Level, uint ItemId)[]
        {
            (1, 4200),
            (2, 4201),
            (3, 4202)
        };

        foreach (var expected in expectedRubies)
        {
            Check.True(
                TestDeveloperItemCommand.TryParse(
                    $"/ruby {expected.Level}",
                    out var request,
                    out var error) &&
                string.IsNullOrEmpty(error) &&
                request is
                {
                    Operation: DeveloperItemOperation.Add,
                    Quantity: 1,
                    ClientOperationId: null
                } &&
                request.Material?.ItemId == expected.ItemId,
                $"Ruby level {expected.Level} resolves item {expected.ItemId} " +
                "and defaults quantity to one");
        }

        Check.True(
            TestDeveloperItemCommand.TryParse(
                "/ruby 2 37",
                out var quantityRequest,
                out var quantityError) &&
            string.IsNullOrEmpty(quantityError) &&
            quantityRequest is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 4201,
                Quantity: 37
            },
            "Ruby command accepts an explicit bounded quantity");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                $"/ruby 3 {DeveloperItemCommand.MaximumQuantity}",
                out var maximumRequest,
                out var maximumError) &&
            string.IsNullOrEmpty(maximumError) &&
            maximumRequest is
            {
                Material.ItemId: 4202,
                Quantity: DeveloperItemCommand.MaximumQuantity
            },
            "Ruby command accepts the shared developer-item quantity maximum");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                $"test2:/ruby 1 99 op={OperationId:D}",
                out var identifiedRequest,
                out var identifiedError) &&
            string.IsNullOrEmpty(identifiedError) &&
            identifiedRequest is
            {
                Operation: DeveloperItemOperation.Add,
                Material.ItemId: 4200,
                Quantity: 99,
                ClientOperationId: not null
            } &&
            identifiedRequest.ClientOperationId == OperationId,
            "Ruby command accepts a sender prefix and final operation ID");

        foreach (var invalidLevel in new[] { "0", "4", "5", "bad" })
        {
            Check.True(
                TestDeveloperItemCommand.TryParse(
                    $"/ruby {invalidLevel}",
                    out var request,
                    out var error) &&
                request is null &&
                !string.IsNullOrWhiteSpace(error),
                $"Ruby level '{invalidLevel}' is consumed and rejected");
        }

        Check.True(
            TestDeveloperItemCommand.TryParse(
                $"/ruby 1 {DeveloperItemCommand.MaximumQuantity + 1}",
                out var oversizedRequest,
                out var oversizedError) &&
            oversizedRequest is null &&
            oversizedError.Contains(
                DeveloperItemCommand.MaximumQuantity.ToString(),
                StringComparison.Ordinal),
            "Ruby command rejects quantities above the shared maximum");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                "/ruby 1 0",
                out var zeroRequest,
                out var zeroError) &&
            zeroRequest is null &&
            !string.IsNullOrWhiteSpace(zeroError),
            "Ruby command rejects zero quantity");

        Check.True(
            TestDeveloperItemCommand.TryParse(
                "/ruby 1 1 op=",
                out var invalidOperationRequest,
                out var invalidOperationError) &&
            invalidOperationRequest is null &&
            invalidOperationError.Contains("D-format UUID", StringComparison.Ordinal),
            "Ruby command rejects an invalid operation ID");

        return Task.CompletedTask;
    }
}
