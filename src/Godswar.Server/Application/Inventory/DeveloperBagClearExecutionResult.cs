using System.Text;

namespace Godswar.Server.Application.Inventory;

internal enum DeveloperBagClearExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    RequestHashConflict = 3,
    InvalidIntent = 4,
    PreconditionFailed = 5
}

internal sealed record DeveloperBagClearExecutionReceipt
{
    public const int MaximumRemovedSlots = 96;
    public const int MaximumAuditReferenceBytes = 256;

    public DeveloperBagClearExecutionReceipt(
        int characterId,
        IReadOnlyList<short> removedSlots,
        long inventoryRevision,
        string auditReference,
        Guid outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        ArgumentNullException.ThrowIfNull(removedSlots);
        if (removedSlots.Count is <= 0 or > MaximumRemovedSlots ||
            removedSlots.Any(static slot => slot is < 0 or >= 96) ||
            removedSlots.Distinct().Count() != removedSlots.Count ||
            !removedSlots.SequenceEqual(removedSlots.Order()))
        {
            throw new ArgumentException(
                "Removed bag slots must be unique, ordered, and bounded.",
                nameof(removedSlots));
        }

        if (inventoryRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (Encoding.UTF8.GetByteCount(auditReference) >
                MaximumAuditReferenceBytes ||
            auditReference.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference));
        }

        if (outboxEventId == Guid.Empty)
        {
            throw new ArgumentException(
                "An outbox event ID is required.",
                nameof(outboxEventId));
        }

        CharacterId = characterId;
        RemovedSlots = removedSlots.ToArray();
        InventoryRevision = inventoryRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public int CharacterId { get; }

    public IReadOnlyList<short> RemovedSlots { get; }

    public long InventoryRevision { get; }

    public string AuditReference { get; }

    public Guid OutboxEventId { get; }
}

internal sealed record DeveloperBagClearExecutionResult
{
    public DeveloperBagClearExecutionResult(
        DeveloperBagClearExecutionDisposition disposition,
        DeveloperBagClearExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt =
            disposition is DeveloperBagClearExecutionDisposition.Committed or
                DeveloperBagClearExecutionDisposition.Duplicate;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                requiresReceipt
                    ? "Successful bag clears require a durable receipt."
                    : "Rejected bag clears cannot carry a receipt.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public DeveloperBagClearExecutionDisposition Disposition { get; }

    public DeveloperBagClearExecutionReceipt? Receipt { get; }

    public bool IsSuccess =>
        Disposition is DeveloperBagClearExecutionDisposition.Committed or
            DeveloperBagClearExecutionDisposition.Duplicate;

    public static DeveloperBagClearExecutionResult Committed(
        DeveloperBagClearExecutionReceipt receipt) =>
        new(DeveloperBagClearExecutionDisposition.Committed, receipt);

    public static DeveloperBagClearExecutionResult Duplicate(
        DeveloperBagClearExecutionReceipt receipt) =>
        new(DeveloperBagClearExecutionDisposition.Duplicate, receipt);

    public static DeveloperBagClearExecutionResult RequestHashConflict() =>
        new(DeveloperBagClearExecutionDisposition.RequestHashConflict);

    public static DeveloperBagClearExecutionResult InvalidIntent() =>
        new(DeveloperBagClearExecutionDisposition.InvalidIntent);

    public static DeveloperBagClearExecutionResult PreconditionFailed() =>
        new(DeveloperBagClearExecutionDisposition.PreconditionFailed);
}
