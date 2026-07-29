using System.Text;

namespace Godswar.Server.Application.Inventory;

internal enum DeveloperItemGrantExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    RequestHashConflict = 3,
    InvalidIntent = 4,
    PreconditionFailed = 5
}

/// <summary>
/// Canonical durable receipt returned for both a new commit and its exact
/// duplicate. It intentionally contains no provider row or game-state type.
/// </summary>
internal sealed record DeveloperItemGrantExecutionReceipt
{
    public const int MaximumAuditReferenceBytes = 256;

    public DeveloperItemGrantExecutionReceipt(
        int characterId,
        uint itemId,
        int grantedQuantity,
        long inventoryRevision,
        string auditReference,
        Guid outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        if (itemId is <
                DeveloperItemGrantCommandEnvelope.MinimumItemId or
            > DeveloperItemGrantCommandEnvelope.MaximumItemId)
        {
            throw new ArgumentOutOfRangeException(nameof(itemId));
        }

        if (grantedQuantity is <
                DeveloperItemGrantCommandEnvelope.MinimumQuantity or
            > DeveloperItemGrantCommandEnvelope.MaximumQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grantedQuantity));
        }

        if (inventoryRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }

        AuditReference = RequireAuditReference(auditReference);
        if (outboxEventId == Guid.Empty)
        {
            throw new ArgumentException(
                "An outbox event ID is required.",
                nameof(outboxEventId));
        }

        CharacterId = characterId;
        ItemId = itemId;
        GrantedQuantity = grantedQuantity;
        InventoryRevision = inventoryRevision;
        OutboxEventId = outboxEventId;
    }

    public int CharacterId { get; }

    public uint ItemId { get; }

    public int GrantedQuantity { get; }

    public long InventoryRevision { get; }

    public string AuditReference { get; }

    public Guid OutboxEventId { get; }

    private static string RequireAuditReference(string auditReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (Encoding.UTF8.GetByteCount(auditReference) >
            MaximumAuditReferenceBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference),
                $"Audit references are limited to " +
                $"{MaximumAuditReferenceBytes} UTF-8 bytes.");
        }

        if (auditReference.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Audit references cannot contain control characters.",
                nameof(auditReference));
        }

        return auditReference;
    }
}

/// <summary>
/// Bounded grant outcome. Committed and duplicate outcomes carry the same
/// durable receipt; rejection outcomes never fabricate authoritative state.
/// </summary>
internal sealed record DeveloperItemGrantExecutionResult
{
    public DeveloperItemGrantExecutionResult(
        DeveloperItemGrantExecutionDisposition disposition,
        DeveloperItemGrantExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt =
            disposition is DeveloperItemGrantExecutionDisposition.Committed or
                DeveloperItemGrantExecutionDisposition.Duplicate;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                requiresReceipt
                    ? "Successful grant outcomes require a durable receipt."
                    : "Rejected grant outcomes cannot carry a receipt.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public DeveloperItemGrantExecutionDisposition Disposition { get; }

    public DeveloperItemGrantExecutionReceipt? Receipt { get; }

    public bool IsSuccess =>
        Disposition is DeveloperItemGrantExecutionDisposition.Committed or
            DeveloperItemGrantExecutionDisposition.Duplicate;

    public static DeveloperItemGrantExecutionResult Committed(
        DeveloperItemGrantExecutionReceipt receipt) =>
        new(
            DeveloperItemGrantExecutionDisposition.Committed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static DeveloperItemGrantExecutionResult Duplicate(
        DeveloperItemGrantExecutionReceipt receipt) =>
        new(
            DeveloperItemGrantExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static DeveloperItemGrantExecutionResult RequestHashConflict() =>
        new(DeveloperItemGrantExecutionDisposition.RequestHashConflict);

    public static DeveloperItemGrantExecutionResult InvalidIntent() =>
        new(DeveloperItemGrantExecutionDisposition.InvalidIntent);

    public static DeveloperItemGrantExecutionResult PreconditionFailed() =>
        new(DeveloperItemGrantExecutionDisposition.PreconditionFailed);
}
