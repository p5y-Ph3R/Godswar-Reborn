using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class KitBagItemMoveDurableHandlerChecks
{
    private static async Task
        CheckStaleDestinationRejectionAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemMoveResultStatus.StaleDestination);
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            KitBagItemMoveExecutionResult.TerminalRejected(receipt),
            persistedSource: SourceItem,
            persistedDestination: DestinationItem);

        await InvokeMoveAsync(fixture.Handler, OperationId);

        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Rejected,
            expectedMoveAcknowledgement: false,
            "stale-destination rejection");
    }

    private static async Task
        CheckExecutorFailureLeavesPendingAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            KitBagItemMoveExecutionResult.Committed(
                CreateReceipt(
                    KitBagItemMoveResultStatus.Moved)),
            executionFails: true);

        await InvokeMoveAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "uncertain move commit reaches executor once");
        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "uncertain move commit does not project");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "uncertain move commit emits no terminal result");
    }
}
