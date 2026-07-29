using Godswar.Server.Application.Inventory;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MakeAttributeStoneCommandContractChecks
{
    private static void CheckNativeResultMapping()
    {
        Check.Equal(
            1017,
            MakeAttributeStoneNativeResults.GetResultSubId(
                MakeAttributeStoneResultStatus.Succeeded),
            "success uses the captured client result");
        Check.Equal(
            1016,
            MakeAttributeStoneNativeResults.GetResultSubId(
                MakeAttributeStoneResultStatus.InsufficientDust),
            "insufficient dust uses the captured client result");
        Check.Equal(
            1022,
            MakeAttributeStoneNativeResults.GetResultSubId(
                MakeAttributeStoneResultStatus.InvalidDust),
            "invalid dust uses the captured client result");
        Check.Equal(
            1020,
            MakeAttributeStoneNativeResults.GetResultSubId(
                MakeAttributeStoneResultStatus.InsufficientCapacity),
            "bag-full uses the captured client result");
        Check.Equal(
            1002,
            MakeAttributeStoneNativeResults.GetResultSubId(
                MakeAttributeStoneResultStatus.StaleSelection),
            "stale selection uses the captured client result");
        Check.Equal(
            1002,
            MakeAttributeStoneNativeResults.GetResultSubId(
                MakeAttributeStoneResultStatus.InvalidKitBagSlot),
            "invalid slot uses the captured client result");
    }

    private static void CheckReceiptAndResultInvariants()
    {
        var success = CreateSuccessReceipt();
        Check.True(
            MakeAttributeStoneExecutionResult
                .Committed(success).IsSuccess,
            "committed success carries its durable receipt");
        Check.True(
            MakeAttributeStoneExecutionResult
                .Duplicate(success).IsSuccess,
            "duplicate success replays its durable receipt");

        var rejectedReceipt = new MakeAttributeStoneExecutionReceipt(
            7,
            MakeAttributeStoneResultStatus.InvalidDust,
            MakeAttributeStoneNativeResults.InvalidDustSubId,
            12,
            9999,
            0,
            true,
            0,
            "command_audit:43",
            null);
        var rejected =
            MakeAttributeStoneExecutionResult.TerminalRejected(
                rejectedReceipt);
        Check.True(
            rejected.IsDurable && !rejected.IsSuccess,
            "terminal rejection carries a durable non-success receipt");
        var duplicateRejected =
            MakeAttributeStoneExecutionResult.Duplicate(
                rejectedReceipt);
        Check.True(
            duplicateRejected.IsDurable &&
            !duplicateRejected.IsSuccess,
            "duplicate can replay a durable terminal rejection");

        var replayMissing =
            MakeAttributeStoneExecutionResult.ReplayNotFound();
        Check.True(
            replayMissing.Disposition ==
                MakeAttributeStoneExecutionDisposition.ReplayNotFound &&
            replayMissing.Receipt is null &&
            !replayMissing.IsDurable,
            "replay-not-found cannot fabricate a receipt");

        Check.Throws<ArgumentException>(
            () => new MakeAttributeStoneExecutionReceipt(
                7,
                MakeAttributeStoneResultStatus.Succeeded,
                MakeAttributeStoneNativeResults.SucceededSubId,
                12,
                9900,
                9930,
                true,
                1,
                "command_audit:42",
                null),
            "success requires a non-empty outbox event");
        Check.Throws<ArgumentException>(
            () => new MakeAttributeStoneExecutionReceipt(
                7,
                MakeAttributeStoneResultStatus.StaleSelection,
                MakeAttributeStoneNativeResults.StaleSelectionSubId,
                12,
                9900,
                0,
                true,
                1,
                "command_audit:42",
                null),
            "stale selection cannot invent item identity");
        Check.Throws<ArgumentException>(
            () => new MakeAttributeStoneExecutionReceipt(
                7,
                MakeAttributeStoneResultStatus.InvalidDust,
                MakeAttributeStoneNativeResults.InvalidDustSubId,
                12,
                9999,
                9930,
                true,
                1,
                "command_audit:42",
                null),
            "invalid dust cannot invent an output stone");
        Check.Throws<ArgumentException>(
            () => new MakeAttributeStoneExecutionReceipt(
                7,
                MakeAttributeStoneResultStatus.InsufficientDust,
                MakeAttributeStoneNativeResults.InvalidDustSubId,
                12,
                9900,
                9930,
                true,
                1,
                "command_audit:42",
                null),
            "receipt rejects a mismatched native result");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new MakeAttributeStoneExecutionReceipt(
                7,
                MakeAttributeStoneResultStatus.StaleSelection,
                MakeAttributeStoneNativeResults.StaleSelectionSubId,
                12,
                0,
                0,
                null,
                -1,
                "command_audit:42",
                null),
            "receipt requires a non-negative inventory revision");
        Check.Throws<ArgumentException>(
            () => MakeAttributeStoneExecutionResult.Committed(
                rejectedReceipt),
            "committed result cannot describe a rejection");
        Check.Throws<ArgumentException>(
            () => MakeAttributeStoneExecutionResult.TerminalRejected(
                success),
            "terminal rejection cannot describe success");
        Check.Throws<ArgumentException>(
            () => new MakeAttributeStoneExecutionResult(
                MakeAttributeStoneExecutionDisposition.ReplayNotFound,
                success),
            "non-durable result cannot carry a receipt");
    }

    private static MakeAttributeStoneExecutionReceipt
        CreateSuccessReceipt() =>
        new(
            7,
            MakeAttributeStoneResultStatus.Succeeded,
            MakeAttributeStoneNativeResults.SucceededSubId,
            12,
            9900,
            9930,
            true,
            1,
            "command_audit:42",
            Guid.Parse("93e0536e-1dca-4327-a513-aea8c948a15e"));
}
