using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum KitBagItemDeleteResultStatus : byte
{
    Deleted = 1,
    EmptySlot = 2,
    StaleSelection = 3
}

internal sealed record KitBagItemDeleteExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public KitBagItemDeleteExecutionReceipt(
        int characterId,
        int kitBagSlot,
        KitBagItemDeleteResultStatus status,
        string expectedCompactItemState,
        string authoritativeCompactItemState,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        if (kitBagSlot is
            < KitBagItemDeleteCommandEnvelope.MinimumKitBagSlot or
            > KitBagItemDeleteCommandEnvelope.MaximumKitBagSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(kitBagSlot));
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ValidateCompactState(
            expectedCompactItemState,
            nameof(expectedCompactItemState));
        ValidateCompactState(
            authoritativeCompactItemState,
            nameof(authoritativeCompactItemState));
        if (inventoryRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (Encoding.UTF8.GetByteCount(auditReference) >
                MaximumAuditReferenceUtf8Bytes ||
            auditReference.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference));
        }

        var deleted = status == KitBagItemDeleteResultStatus.Deleted;
        if (deleted)
        {
            if (expectedCompactItemState == "[]" ||
                !string.Equals(
                    expectedCompactItemState,
                    authoritativeCompactItemState,
                    StringComparison.Ordinal) ||
                inventoryRevision <= 0 ||
                !outboxEventId.HasValue ||
                outboxEventId.Value == Guid.Empty)
            {
                throw new ArgumentException(
                    "A deleted receipt requires one exact non-empty item, " +
                    "an advanced revision, and an outbox event.");
            }
        }
        else if (outboxEventId is not null)
        {
            throw new ArgumentException(
                "A terminal rejection cannot publish an inventory event.",
                nameof(outboxEventId));
        }

        if (status == KitBagItemDeleteResultStatus.EmptySlot &&
            (expectedCompactItemState != "[]" ||
             authoritativeCompactItemState != "[]"))
        {
            throw new ArgumentException(
                "An empty-slot receipt requires an explicitly empty " +
                "selection and authoritative slot.");
        }
        if (status == KitBagItemDeleteResultStatus.StaleSelection &&
            string.Equals(
                expectedCompactItemState,
                authoritativeCompactItemState,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A stale receipt requires different expected and " +
                "authoritative states.");
        }

        CharacterId = characterId;
        KitBagSlot = kitBagSlot;
        Status = status;
        ExpectedCompactItemState = expectedCompactItemState;
        AuthoritativeCompactItemState =
            authoritativeCompactItemState;
        InventoryRevision = inventoryRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family => CommandFamily.KitBagItemDelete;

    public int CharacterId { get; }

    public int KitBagSlot { get; }

    public KitBagItemDeleteResultStatus Status { get; }

    public string ExpectedCompactItemState { get; }

    public string AuthoritativeCompactItemState { get; }

    public long InventoryRevision { get; }

    public string AuditReference { get; }

    public Guid? OutboxEventId { get; }

    private static void ValidateCompactState(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) >
                KitBagItemDeleteCommandEnvelope
                    .MaximumExpectedStateUtf8Bytes ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']')
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal enum KitBagItemDeleteExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record KitBagItemDeleteExecutionResult
{
    public KitBagItemDeleteExecutionResult(
        KitBagItemDeleteExecutionDisposition disposition,
        KitBagItemDeleteExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt =
            disposition is
                KitBagItemDeleteExecutionDisposition.Committed or
                KitBagItemDeleteExecutionDisposition.Duplicate or
                KitBagItemDeleteExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                requiresReceipt
                    ? "Durable outcomes require their canonical receipt."
                    : "Non-durable outcomes cannot carry a receipt.",
                nameof(receipt));
        }
        if (disposition ==
                KitBagItemDeleteExecutionDisposition.Committed &&
            receipt!.Status != KitBagItemDeleteResultStatus.Deleted)
        {
            throw new ArgumentException(
                "A committed result must describe a deletion.",
                nameof(receipt));
        }
        if (disposition ==
                KitBagItemDeleteExecutionDisposition.TerminalRejected &&
            receipt!.Status == KitBagItemDeleteResultStatus.Deleted)
        {
            throw new ArgumentException(
                "A terminal rejection cannot describe a deletion.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public KitBagItemDeleteExecutionDisposition Disposition { get; }

    public KitBagItemDeleteExecutionReceipt? Receipt { get; }

    public bool IsSuccess =>
        Receipt?.Status == KitBagItemDeleteResultStatus.Deleted &&
        Disposition is
            KitBagItemDeleteExecutionDisposition.Committed or
            KitBagItemDeleteExecutionDisposition.Duplicate;

    public bool IsDurable => Receipt is not null;

    public static KitBagItemDeleteExecutionResult Committed(
        KitBagItemDeleteExecutionReceipt receipt) =>
        new(
            KitBagItemDeleteExecutionDisposition.Committed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static KitBagItemDeleteExecutionResult Duplicate(
        KitBagItemDeleteExecutionReceipt receipt) =>
        new(
            KitBagItemDeleteExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static KitBagItemDeleteExecutionResult TerminalRejected(
        KitBagItemDeleteExecutionReceipt receipt) =>
        new(
            KitBagItemDeleteExecutionDisposition.TerminalRejected,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static KitBagItemDeleteExecutionResult ReplayNotFound() =>
        new(KitBagItemDeleteExecutionDisposition.ReplayNotFound);

    public static KitBagItemDeleteExecutionResult RequestHashConflict() =>
        new(
            KitBagItemDeleteExecutionDisposition.RequestHashConflict);

    public static KitBagItemDeleteExecutionResult InvalidIntent() =>
        new(KitBagItemDeleteExecutionDisposition.InvalidIntent);

    public static KitBagItemDeleteExecutionResult PreconditionFailed() =>
        new(KitBagItemDeleteExecutionDisposition.PreconditionFailed);
}
