namespace Godswar.Server.Application.Inventory;

internal enum HolyStoneExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record HolyStoneExecutionResult
{
    private HolyStoneExecutionResult(
        HolyStoneExecutionDisposition disposition,
        HolyStoneExecutionReceipt? receipt = null)
    {
        var needsReceipt = disposition is
            HolyStoneExecutionDisposition.Committed or
            HolyStoneExecutionDisposition.Duplicate or
            HolyStoneExecutionDisposition.TerminalRejected;
        if (!Enum.IsDefined(disposition) ||
            needsReceipt != (receipt is not null) ||
            disposition == HolyStoneExecutionDisposition.Committed &&
            !HolyStoneNativeResults.IsSuccess(receipt!.Status) ||
            disposition == HolyStoneExecutionDisposition.TerminalRejected &&
            HolyStoneNativeResults.IsSuccess(receipt!.Status))
        {
            throw new ArgumentException(
                "The execution disposition and receipt are inconsistent.");
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public HolyStoneExecutionDisposition Disposition { get; }
    public HolyStoneExecutionReceipt? Receipt { get; }
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt is not null &&
        HolyStoneNativeResults.IsSuccess(Receipt.Status) &&
        Disposition is
            HolyStoneExecutionDisposition.Committed or
            HolyStoneExecutionDisposition.Duplicate;

    public static HolyStoneExecutionResult Committed(
        HolyStoneExecutionReceipt receipt) =>
        new(HolyStoneExecutionDisposition.Committed, receipt);
    public static HolyStoneExecutionResult Duplicate(
        HolyStoneExecutionReceipt receipt) =>
        new(HolyStoneExecutionDisposition.Duplicate, receipt);
    public static HolyStoneExecutionResult TerminalRejected(
        HolyStoneExecutionReceipt receipt) =>
        new(HolyStoneExecutionDisposition.TerminalRejected, receipt);
    public static HolyStoneExecutionResult ReplayNotFound() =>
        new(HolyStoneExecutionDisposition.ReplayNotFound);
    public static HolyStoneExecutionResult RequestHashConflict() =>
        new(HolyStoneExecutionDisposition.RequestHashConflict);
    public static HolyStoneExecutionResult InvalidIntent() =>
        new(HolyStoneExecutionDisposition.InvalidIntent);
    public static HolyStoneExecutionResult PreconditionFailed() =>
        new(HolyStoneExecutionDisposition.PreconditionFailed);
}
