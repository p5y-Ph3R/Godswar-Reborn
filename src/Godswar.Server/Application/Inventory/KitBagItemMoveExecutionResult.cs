using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum KitBagItemMoveResultStatus : byte
{
    Moved = 1,
    Swapped = 2,
    EmptySource = 3,
    StaleSource = 4,
    StaleDestination = 5
}

internal sealed record KitBagItemMoveExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public KitBagItemMoveExecutionReceipt(
        int characterId,
        int sourceKitBagSlot,
        int destinationKitBagSlot,
        KitBagItemMoveResultStatus status,
        string expectedSourceCompactItemState,
        string expectedDestinationCompactItemState,
        string authoritativeSourceCompactItemState,
        string authoritativeDestinationCompactItemState,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        ValidateSlots(sourceKitBagSlot, destinationKitBagSlot);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ValidateCompactState(
            expectedSourceCompactItemState,
            nameof(expectedSourceCompactItemState));
        ValidateCompactState(
            expectedDestinationCompactItemState,
            nameof(expectedDestinationCompactItemState));
        ValidateCompactState(
            authoritativeSourceCompactItemState,
            nameof(authoritativeSourceCompactItemState));
        ValidateCompactState(
            authoritativeDestinationCompactItemState,
            nameof(authoritativeDestinationCompactItemState));
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

        ValidateOutcome(
            status,
            expectedSourceCompactItemState,
            expectedDestinationCompactItemState,
            authoritativeSourceCompactItemState,
            authoritativeDestinationCompactItemState,
            inventoryRevision,
            outboxEventId);

        CharacterId = characterId;
        SourceKitBagSlot = sourceKitBagSlot;
        DestinationKitBagSlot = destinationKitBagSlot;
        Status = status;
        ExpectedSourceCompactItemState =
            expectedSourceCompactItemState;
        ExpectedDestinationCompactItemState =
            expectedDestinationCompactItemState;
        AuthoritativeSourceCompactItemState =
            authoritativeSourceCompactItemState;
        AuthoritativeDestinationCompactItemState =
            authoritativeDestinationCompactItemState;
        InventoryRevision = inventoryRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family => CommandFamily.KitBagItemMove;
    public int CharacterId { get; }
    public int SourceKitBagSlot { get; }
    public int DestinationKitBagSlot { get; }
    public KitBagItemMoveResultStatus Status { get; }
    public string ExpectedSourceCompactItemState { get; }
    public string ExpectedDestinationCompactItemState { get; }
    public string AuthoritativeSourceCompactItemState { get; }
    public string AuthoritativeDestinationCompactItemState { get; }
    public long InventoryRevision { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }

    private static void ValidateSlots(int source, int destination)
    {
        if (source is < KitBagItemMoveCommandEnvelope.MinimumKitBagSlot or
                > KitBagItemMoveCommandEnvelope.MaximumKitBagSlot ||
            destination is
                < KitBagItemMoveCommandEnvelope.MinimumKitBagSlot or
                > KitBagItemMoveCommandEnvelope.MaximumKitBagSlot ||
            source == destination)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    private static void ValidateOutcome(
        KitBagItemMoveResultStatus status,
        string expectedSource,
        string expectedDestination,
        string authoritativeSource,
        string authoritativeDestination,
        long revision,
        Guid? eventId)
    {
        var mutated = status is
            KitBagItemMoveResultStatus.Moved or
            KitBagItemMoveResultStatus.Swapped;
        if (mutated != eventId.HasValue ||
            (mutated && (eventId == Guid.Empty || revision <= 0)))
        {
            throw new ArgumentException(
                "Only committed movement has a revision and event.");
        }

        var sourceMatches = string.Equals(
            expectedSource,
            authoritativeSource,
            StringComparison.Ordinal);
        var destinationMatches = string.Equals(
            expectedDestination,
            authoritativeDestination,
            StringComparison.Ordinal);
        var valid = status switch
        {
            KitBagItemMoveResultStatus.Moved =>
                sourceMatches && destinationMatches &&
                authoritativeSource != "[]" &&
                authoritativeDestination == "[]",
            KitBagItemMoveResultStatus.Swapped =>
                sourceMatches && destinationMatches &&
                authoritativeSource != "[]" &&
                authoritativeDestination != "[]",
            KitBagItemMoveResultStatus.EmptySource =>
                sourceMatches && authoritativeSource == "[]",
            KitBagItemMoveResultStatus.StaleSource =>
                !sourceMatches,
            KitBagItemMoveResultStatus.StaleDestination =>
                sourceMatches && authoritativeSource != "[]" &&
                !destinationMatches,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The item-move receipt states do not match its status.");
        }
    }

    private static void ValidateCompactState(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) >
                KitBagItemMoveCommandEnvelope
                    .MaximumExpectedStateUtf8Bytes ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']')
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal enum KitBagItemMoveExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record KitBagItemMoveExecutionResult
{
    public KitBagItemMoveExecutionResult(
        KitBagItemMoveExecutionDisposition disposition,
        KitBagItemMoveExecutionReceipt? receipt = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }
        var requiresReceipt = disposition is
            KitBagItemMoveExecutionDisposition.Committed or
            KitBagItemMoveExecutionDisposition.Duplicate or
            KitBagItemMoveExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                "The result receipt does not match its disposition.",
                nameof(receipt));
        }
        var mutation = receipt?.Status is
            KitBagItemMoveResultStatus.Moved or
            KitBagItemMoveResultStatus.Swapped;
        if (disposition ==
                KitBagItemMoveExecutionDisposition.Committed &&
            !mutation)
        {
            throw new ArgumentException(
                "A committed result must describe movement.",
                nameof(receipt));
        }
        if (disposition ==
                KitBagItemMoveExecutionDisposition.TerminalRejected &&
            mutation)
        {
            throw new ArgumentException(
                "A terminal rejection cannot describe movement.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public KitBagItemMoveExecutionDisposition Disposition { get; }
    public KitBagItemMoveExecutionReceipt? Receipt { get; }
    public bool IsSuccess =>
        Receipt?.Status is (
            KitBagItemMoveResultStatus.Moved or
            KitBagItemMoveResultStatus.Swapped) &&
        Disposition is (
            KitBagItemMoveExecutionDisposition.Committed or
            KitBagItemMoveExecutionDisposition.Duplicate);
    public bool IsDurable => Receipt is not null;

    public static KitBagItemMoveExecutionResult Committed(
        KitBagItemMoveExecutionReceipt receipt) =>
        new(KitBagItemMoveExecutionDisposition.Committed, receipt);
    public static KitBagItemMoveExecutionResult Duplicate(
        KitBagItemMoveExecutionReceipt receipt) =>
        new(KitBagItemMoveExecutionDisposition.Duplicate, receipt);
    public static KitBagItemMoveExecutionResult TerminalRejected(
        KitBagItemMoveExecutionReceipt receipt) =>
        new(KitBagItemMoveExecutionDisposition.TerminalRejected, receipt);
    public static KitBagItemMoveExecutionResult ReplayNotFound() =>
        new(KitBagItemMoveExecutionDisposition.ReplayNotFound);
    public static KitBagItemMoveExecutionResult RequestHashConflict() =>
        new(KitBagItemMoveExecutionDisposition.RequestHashConflict);
    public static KitBagItemMoveExecutionResult InvalidIntent() =>
        new(KitBagItemMoveExecutionDisposition.InvalidIntent);
    public static KitBagItemMoveExecutionResult PreconditionFailed() =>
        new(KitBagItemMoveExecutionDisposition.PreconditionFailed);
}
