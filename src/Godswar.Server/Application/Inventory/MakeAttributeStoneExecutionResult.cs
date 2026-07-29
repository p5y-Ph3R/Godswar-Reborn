using System.Text;

namespace Godswar.Server.Application.Inventory;

internal enum MakeAttributeStoneResultStatus : byte
{
    Succeeded = 1,
    InvalidDust = 2,
    InsufficientDust = 3,
    InsufficientCapacity = 4,
    StaleSelection = 5,
    InvalidKitBagSlot = 6
}

internal static class MakeAttributeStoneNativeResults
{
    public const int SucceededSubId = 1017;
    public const int InsufficientDustSubId = 1016;
    public const int InvalidDustSubId = 1022;
    public const int InsufficientCapacitySubId = 1020;
    public const int StaleSelectionSubId = 1002;
    public const int InvalidKitBagSlotSubId = 1002;

    public static int GetResultSubId(
        MakeAttributeStoneResultStatus status) =>
        status switch
        {
            MakeAttributeStoneResultStatus.Succeeded =>
                SucceededSubId,
            MakeAttributeStoneResultStatus.InvalidDust =>
                InvalidDustSubId,
            MakeAttributeStoneResultStatus.InsufficientDust =>
                InsufficientDustSubId,
            MakeAttributeStoneResultStatus.InsufficientCapacity =>
                InsufficientCapacitySubId,
            MakeAttributeStoneResultStatus.StaleSelection =>
                StaleSelectionSubId,
            MakeAttributeStoneResultStatus.InvalidKitBagSlot =>
                InvalidKitBagSlotSubId,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}

internal enum MakeAttributeStoneExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record MakeAttributeStoneExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public MakeAttributeStoneExecutionReceipt(
        int characterId,
        MakeAttributeStoneResultStatus status,
        int nativeResultSubId,
        int selectedKitBagSlot,
        uint sourceDustItemId,
        uint outputStoneItemId,
        bool? isBound,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (nativeResultSubId !=
            MakeAttributeStoneNativeResults.GetResultSubId(status))
        {
            throw new ArgumentException(
                "The native result does not match the durable status.",
                nameof(nativeResultSubId));
        }

        if (selectedKitBagSlot is
            < GearMentorMakeAttributeStoneCommandEnvelope.MinimumKitBagSlot or
            > GearMentorMakeAttributeStoneCommandEnvelope.MaximumKitBagSlot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedKitBagSlot));
        }

        ValidateItemOutcome(
            status,
            sourceDustItemId,
            outputStoneItemId,
            isBound);
        if (inventoryRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }

        AuditReference = RequireAuditReference(auditReference);
        var succeeded =
            status == MakeAttributeStoneResultStatus.Succeeded;
        if (succeeded)
        {
            if (!outboxEventId.HasValue ||
                outboxEventId.Value == Guid.Empty)
            {
                throw new ArgumentException(
                    "A successful mutation requires an outbox event.",
                    nameof(outboxEventId));
            }
        }
        else if (outboxEventId is not null)
        {
            throw new ArgumentException(
                "A terminal rejection cannot publish an inventory event.",
                nameof(outboxEventId));
        }

        CharacterId = characterId;
        Status = status;
        NativeResultSubId = nativeResultSubId;
        SelectedKitBagSlot = selectedKitBagSlot;
        SourceDustItemId = sourceDustItemId;
        OutputStoneItemId = outputStoneItemId;
        IsBound = isBound;
        InventoryRevision = inventoryRevision;
        OutboxEventId = outboxEventId;
    }

    public int CharacterId { get; }

    public MakeAttributeStoneResultStatus Status { get; }

    public int NativeResultSubId { get; }

    public int SelectedKitBagSlot { get; }

    public uint SourceDustItemId { get; }

    public uint OutputStoneItemId { get; }

    public bool? IsBound { get; }

    public long InventoryRevision { get; }

    public string AuditReference { get; }

    public Guid? OutboxEventId { get; }

    private static void ValidateItemOutcome(
        MakeAttributeStoneResultStatus status,
        uint sourceDustItemId,
        uint outputStoneItemId,
        bool? isBound)
    {
        var sourceKnown =
            status is MakeAttributeStoneResultStatus.Succeeded or
                MakeAttributeStoneResultStatus.InvalidDust or
                MakeAttributeStoneResultStatus.InsufficientDust or
                MakeAttributeStoneResultStatus.InsufficientCapacity;
        var outputKnown =
            status is MakeAttributeStoneResultStatus.Succeeded or
                MakeAttributeStoneResultStatus.InsufficientDust or
                MakeAttributeStoneResultStatus.InsufficientCapacity;

        if (sourceKnown != (sourceDustItemId != 0) ||
            outputKnown != (outputStoneItemId != 0) ||
            sourceKnown != isBound.HasValue)
        {
            throw new ArgumentException(
                "Item identity and binding must match status availability.");
        }
    }

    private static string RequireAuditReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) >
                MaximumAuditReferenceUtf8Bytes ||
            value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }
}

internal sealed record MakeAttributeStoneExecutionResult
{
    public MakeAttributeStoneExecutionResult(
        MakeAttributeStoneExecutionDisposition disposition,
        MakeAttributeStoneExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt =
            disposition is MakeAttributeStoneExecutionDisposition.Committed or
                MakeAttributeStoneExecutionDisposition.Duplicate or
                MakeAttributeStoneExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                requiresReceipt
                    ? "Durable outcomes require their canonical receipt."
                    : "Non-durable outcomes cannot carry a receipt.",
                nameof(receipt));
        }

        if (disposition ==
                MakeAttributeStoneExecutionDisposition.Committed &&
            receipt!.Status != MakeAttributeStoneResultStatus.Succeeded)
        {
            throw new ArgumentException(
                "A committed result must describe a success.",
                nameof(receipt));
        }

        if (disposition ==
                MakeAttributeStoneExecutionDisposition.TerminalRejected &&
            receipt!.Status == MakeAttributeStoneResultStatus.Succeeded)
        {
            throw new ArgumentException(
                "A terminal rejection cannot describe a success.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public MakeAttributeStoneExecutionDisposition Disposition { get; }

    public MakeAttributeStoneExecutionReceipt? Receipt { get; }

    public bool IsSuccess =>
        Receipt?.Status == MakeAttributeStoneResultStatus.Succeeded &&
        Disposition is MakeAttributeStoneExecutionDisposition.Committed or
            MakeAttributeStoneExecutionDisposition.Duplicate;

    public bool IsDurable => Receipt is not null;

    public static MakeAttributeStoneExecutionResult Committed(
        MakeAttributeStoneExecutionReceipt receipt) =>
        new(
            MakeAttributeStoneExecutionDisposition.Committed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static MakeAttributeStoneExecutionResult Duplicate(
        MakeAttributeStoneExecutionReceipt receipt) =>
        new(
            MakeAttributeStoneExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static MakeAttributeStoneExecutionResult TerminalRejected(
        MakeAttributeStoneExecutionReceipt receipt) =>
        new(
            MakeAttributeStoneExecutionDisposition.TerminalRejected,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static MakeAttributeStoneExecutionResult ReplayNotFound() =>
        new(MakeAttributeStoneExecutionDisposition.ReplayNotFound);

    public static MakeAttributeStoneExecutionResult RequestHashConflict() =>
        new(MakeAttributeStoneExecutionDisposition.RequestHashConflict);

    public static MakeAttributeStoneExecutionResult InvalidIntent() =>
        new(MakeAttributeStoneExecutionDisposition.InvalidIntent);

    public static MakeAttributeStoneExecutionResult PreconditionFailed() =>
        new(MakeAttributeStoneExecutionDisposition.PreconditionFailed);
}
