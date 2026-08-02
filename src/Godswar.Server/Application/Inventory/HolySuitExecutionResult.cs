namespace Godswar.Server.Application.Inventory;

internal enum HolySuitExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record HolySuitExecutionResult
{
    private HolySuitExecutionResult(
        HolySuitExecutionDisposition disposition,
        HolySuitExecutionReceipt? receipt = null)
    {
        var requiresReceipt = disposition is
            HolySuitExecutionDisposition.Committed or
            HolySuitExecutionDisposition.Duplicate or
            HolySuitExecutionDisposition.TerminalRejected;
        if (!Enum.IsDefined(disposition) ||
            requiresReceipt != (receipt is not null) ||
            disposition == HolySuitExecutionDisposition.Committed &&
                !receipt!.Committed ||
            disposition ==
                HolySuitExecutionDisposition.TerminalRejected &&
                receipt!.Committed)
        {
            throw new ArgumentException(
                "The Holy Suit disposition and receipt are inconsistent.");
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public HolySuitExecutionDisposition Disposition { get; }
    public HolySuitExecutionReceipt? Receipt { get; }
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is
            HolySuitExecutionDisposition.Committed or
            HolySuitExecutionDisposition.Duplicate;

    public static HolySuitExecutionResult Committed(
        HolySuitExecutionReceipt receipt) =>
        new(HolySuitExecutionDisposition.Committed, receipt);

    public static HolySuitExecutionResult Duplicate(
        HolySuitExecutionReceipt receipt) =>
        new(HolySuitExecutionDisposition.Duplicate, receipt);

    public static HolySuitExecutionResult TerminalRejected(
        HolySuitExecutionReceipt receipt) =>
        new(HolySuitExecutionDisposition.TerminalRejected, receipt);

    public static HolySuitExecutionResult ReplayNotFound() =>
        new(HolySuitExecutionDisposition.ReplayNotFound);

    public static HolySuitExecutionResult RequestHashConflict() =>
        new(HolySuitExecutionDisposition.RequestHashConflict);

    public static HolySuitExecutionResult InvalidIntent() =>
        new(HolySuitExecutionDisposition.InvalidIntent);

    public static HolySuitExecutionResult PreconditionFailed() =>
        new(HolySuitExecutionDisposition.PreconditionFailed);
}
