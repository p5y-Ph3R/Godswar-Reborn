using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum GearMentorMaterialConversionResultStatus : byte
{
    Succeeded = 1,
    InvalidCrystal = 2,
    InvalidGemPieces = 3,
    InsufficientGemPieces = 4,
    InsufficientCapacity = 5,
    StaleSelection = 6,
    InvalidKitBagSlot = 7
}

internal static class GearMentorMaterialConversionNativeResults
{
    public const int TransformSucceededSubId = 1823;
    public const int TransformInvalidCrystalSubId = 1822;
    public const int TransformInsufficientCapacitySubId = 1020;
    public const int CombineSucceededSubId = 304;
    public const int CombineInvalidGemPiecesSubId = 301;
    public const int CombineInsufficientGemPiecesSubId = 302;
    public const int CombineInsufficientCapacitySubId = 303;

    public static int GetResultSubId(
        CommandFamily family,
        GearMentorMaterialConversionResultStatus status) =>
        (family, status) switch
        {
            (
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.Succeeded) =>
                TransformSucceededSubId,
            (
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.InvalidCrystal) =>
                TransformInvalidCrystalSubId,
            (
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus
                    .InsufficientCapacity) =>
                TransformInsufficientCapacitySubId,
            (
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.StaleSelection) =>
                TransformInvalidCrystalSubId,
            (
                CommandFamily.GearMentorTransformCrystal,
                GearMentorMaterialConversionResultStatus.InvalidKitBagSlot) =>
                TransformInvalidCrystalSubId,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.Succeeded) =>
                CombineSucceededSubId,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.InvalidGemPieces) =>
                CombineInvalidGemPiecesSubId,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus
                    .InsufficientGemPieces) =>
                CombineInsufficientGemPiecesSubId,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus
                    .InsufficientCapacity) =>
                CombineInsufficientCapacitySubId,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.StaleSelection) =>
                CombineInvalidGemPiecesSubId,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorMaterialConversionResultStatus.InvalidKitBagSlot) =>
                CombineInvalidGemPiecesSubId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                "The status is not valid for this command family.")
        };

    public static bool IsValidStatus(
        CommandFamily family,
        GearMentorMaterialConversionResultStatus status) =>
        family switch
        {
            CommandFamily.GearMentorTransformCrystal =>
                status is
                    GearMentorMaterialConversionResultStatus.Succeeded or
                    GearMentorMaterialConversionResultStatus
                        .InvalidCrystal or
                    GearMentorMaterialConversionResultStatus
                        .InsufficientCapacity or
                    GearMentorMaterialConversionResultStatus
                        .StaleSelection or
                    GearMentorMaterialConversionResultStatus
                        .InvalidKitBagSlot,
            CommandFamily.GearMentorCombineGemPieces =>
                status is
                    GearMentorMaterialConversionResultStatus.Succeeded or
                    GearMentorMaterialConversionResultStatus
                        .InvalidGemPieces or
                    GearMentorMaterialConversionResultStatus
                        .InsufficientGemPieces or
                    GearMentorMaterialConversionResultStatus
                        .InsufficientCapacity or
                    GearMentorMaterialConversionResultStatus
                        .StaleSelection or
                    GearMentorMaterialConversionResultStatus
                        .InvalidKitBagSlot,
            _ => false
        };
}

internal enum GearMentorMaterialConversionExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record GearMentorMaterialConversionExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public GearMentorMaterialConversionExecutionReceipt(
        CommandFamily family,
        int characterId,
        GearMentorMaterialConversionResultStatus status,
        int nativeResultSubId,
        int selectedKitBagSlot,
        uint sourceItemId,
        uint outputItemId,
        int outputQuantity,
        bool? isBound,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (!GearMentorMaterialConversionNativeResults.IsValidStatus(
                family,
                status))
        {
            throw new ArgumentException(
                "The durable status is invalid for this command family.",
                nameof(status));
        }
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        if (nativeResultSubId !=
            GearMentorMaterialConversionNativeResults.GetResultSubId(
                family,
                status))
        {
            throw new ArgumentException(
                "The native result does not match the family and status.",
                nameof(nativeResultSubId));
        }
        if (selectedKitBagSlot is
            < GearMentorSingleMaterialCommandContract.MinimumKitBagSlot or
            > GearMentorSingleMaterialCommandContract.MaximumKitBagSlot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedKitBagSlot));
        }

        ValidateItemOutcome(
            status,
            sourceItemId,
            outputItemId,
            outputQuantity,
            isBound);
        if (inventoryRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }

        AuditReference = RequireAuditReference(auditReference);
        var succeeded =
            status ==
            GearMentorMaterialConversionResultStatus.Succeeded;
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

        Family = family;
        CharacterId = characterId;
        Status = status;
        NativeResultSubId = nativeResultSubId;
        SelectedKitBagSlot = selectedKitBagSlot;
        SourceItemId = sourceItemId;
        OutputItemId = outputItemId;
        OutputQuantity = outputQuantity;
        IsBound = isBound;
        InventoryRevision = inventoryRevision;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family { get; }

    public int CharacterId { get; }

    public GearMentorMaterialConversionResultStatus Status { get; }

    public int NativeResultSubId { get; }

    public int SelectedKitBagSlot { get; }

    public uint SourceItemId { get; }

    public uint OutputItemId { get; }

    public int OutputQuantity { get; }

    public bool? IsBound { get; }

    public long InventoryRevision { get; }

    public string AuditReference { get; }

    public Guid? OutboxEventId { get; }

    private static void ValidateItemOutcome(
        GearMentorMaterialConversionResultStatus status,
        uint sourceItemId,
        uint outputItemId,
        int outputQuantity,
        bool? isBound)
    {
        var sourceKnown =
            status is
                GearMentorMaterialConversionResultStatus.Succeeded or
                GearMentorMaterialConversionResultStatus.InvalidCrystal or
                GearMentorMaterialConversionResultStatus.InvalidGemPieces or
                GearMentorMaterialConversionResultStatus
                    .InsufficientGemPieces or
                GearMentorMaterialConversionResultStatus
                    .InsufficientCapacity;
        var outputKnown =
            status is
                GearMentorMaterialConversionResultStatus.Succeeded or
                GearMentorMaterialConversionResultStatus
                    .InsufficientGemPieces or
                GearMentorMaterialConversionResultStatus
                    .InsufficientCapacity;

        if (sourceKnown != (sourceItemId != 0) ||
            outputKnown != (outputItemId != 0) ||
            sourceKnown != isBound.HasValue ||
            outputKnown != (outputQuantity > 0) ||
            outputQuantity > 99)
        {
            throw new ArgumentException(
                "Item identity, quantity, and binding must match status " +
                "availability.");
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

internal sealed record GearMentorMaterialConversionExecutionResult
{
    public GearMentorMaterialConversionExecutionResult(
        GearMentorMaterialConversionExecutionDisposition disposition,
        GearMentorMaterialConversionExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt =
            disposition is
                    GearMentorMaterialConversionExecutionDisposition
                        .Committed or
                GearMentorMaterialConversionExecutionDisposition
                    .Duplicate or
                GearMentorMaterialConversionExecutionDisposition
                    .TerminalRejected;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                requiresReceipt
                    ? "Durable outcomes require their canonical receipt."
                    : "Non-durable outcomes cannot carry a receipt.",
                nameof(receipt));
        }
        if (disposition ==
                GearMentorMaterialConversionExecutionDisposition.Committed &&
            receipt!.Status !=
                GearMentorMaterialConversionResultStatus.Succeeded)
        {
            throw new ArgumentException(
                "A committed result must describe a success.",
                nameof(receipt));
        }
        if (disposition ==
                GearMentorMaterialConversionExecutionDisposition
                    .TerminalRejected &&
            receipt!.Status ==
                GearMentorMaterialConversionResultStatus.Succeeded)
        {
            throw new ArgumentException(
                "A terminal rejection cannot describe a success.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public GearMentorMaterialConversionExecutionDisposition Disposition
    {
        get;
    }

    public GearMentorMaterialConversionExecutionReceipt? Receipt { get; }

    public bool IsSuccess =>
        Receipt?.Status ==
            GearMentorMaterialConversionResultStatus.Succeeded &&
        Disposition is
            GearMentorMaterialConversionExecutionDisposition.Committed or
            GearMentorMaterialConversionExecutionDisposition.Duplicate;

    public bool IsDurable => Receipt is not null;

    public static GearMentorMaterialConversionExecutionResult Committed(
        GearMentorMaterialConversionExecutionReceipt receipt) =>
        new(
            GearMentorMaterialConversionExecutionDisposition.Committed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static GearMentorMaterialConversionExecutionResult Duplicate(
        GearMentorMaterialConversionExecutionReceipt receipt) =>
        new(
            GearMentorMaterialConversionExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static GearMentorMaterialConversionExecutionResult
        TerminalRejected(
            GearMentorMaterialConversionExecutionReceipt receipt) =>
        new(
            GearMentorMaterialConversionExecutionDisposition
                .TerminalRejected,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static GearMentorMaterialConversionExecutionResult
        ReplayNotFound() =>
        new(
            GearMentorMaterialConversionExecutionDisposition
                .ReplayNotFound);

    public static GearMentorMaterialConversionExecutionResult
        RequestHashConflict() =>
        new(
            GearMentorMaterialConversionExecutionDisposition
                .RequestHashConflict);

    public static GearMentorMaterialConversionExecutionResult
        InvalidIntent() =>
        new(
            GearMentorMaterialConversionExecutionDisposition.InvalidIntent);

    public static GearMentorMaterialConversionExecutionResult
        PreconditionFailed() =>
        new(
            GearMentorMaterialConversionExecutionDisposition
                .PreconditionFailed);
}
