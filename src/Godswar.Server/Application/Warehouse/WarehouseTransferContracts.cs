using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Warehouse;

internal enum WarehouseStorageType : ushort
{
    Normal = 0,
    Award = 1
}

internal enum WarehouseTransferOperation : byte
{
    Deposit = 1,
    Withdraw = 2,
    InternalMove = 3
}

internal readonly record struct WarehouseTransferCommand(
    WarehouseOperationIdentity Identity,
    int RealmId,
    WarehouseTransferOperation Operation,
    int WarehouseSlot,
    int KitBagSlot,
    int DestinationWarehouseSlot,
    int Money,
    WarehouseStorageType StorageType,
    long ExpectedWarehouseRevision,
    long ExpectedInventoryRevision,
    string ExpectedSourceCompactItemState,
    string ExpectedDestinationCompactItemState);

internal readonly record struct WarehouseTransferReplayIntent(
    int RealmId,
    WarehouseTransferOperation Operation,
    int WarehouseSlot,
    int KitBagSlot,
    int DestinationWarehouseSlot,
    int Money,
    WarehouseStorageType StorageType);

internal enum WarehouseTransferResultStatus : byte
{
    Deposited = 1,
    Withdrawn = 2,
    InternalMoved = 3,
    Stacked = 4,
    Swapped = 5,
    EmptySource = 10,
    DestinationOccupied = 11,
    BagFull = 12,
    CapacityExceeded = 13,
    StackIncompatible = 14,
    ConcurrentConflict = 15,
    RestrictedItem = 16
}

internal enum WarehouseInventoryLocation : byte
{
    KitBag = 1,
    Warehouse = 3
}

internal readonly record struct WarehouseItemMutation(
    long ItemInstanceId,
    int ItemId,
    WarehouseInventoryLocation BeforeLocation,
    int BeforeSlot,
    int BeforeStack,
    WarehouseInventoryLocation? AfterLocation,
    int? AfterSlot,
    int? AfterStack)
{
    public bool IsValid =>
        ItemInstanceId > 0 &&
        ItemId > 0 &&
        IsValidSlot(BeforeLocation, BeforeSlot) &&
        BeforeStack > 0 &&
        (AfterLocation is null
            ? AfterSlot is null && AfterStack is null
            : AfterSlot is { } afterSlot &&
              AfterStack is > 0 &&
              IsValidSlot(AfterLocation.Value, afterSlot));

    private static bool IsValidSlot(
        WarehouseInventoryLocation location,
        int slot) =>
        location switch
        {
            WarehouseInventoryLocation.KitBag =>
                WarehouseCapacityPolicy.IsValidKitBagSlot(slot),
            WarehouseInventoryLocation.Warehouse =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(slot),
            _ => false
        };
}

internal sealed record WarehouseTransferExecutionReceipt(
    int CharacterId,
    WarehouseTransferOperation Operation,
    int WarehouseSlot,
    int KitBagSlot,
    int DestinationWarehouseSlot,
    int ActualWarehouseSlot,
    int ActualKitBagSlot,
    WarehouseTransferResultStatus Status,
    int MovedQuantity,
    int Capacity,
    long WarehouseRevision,
    long InventoryRevision,
    IReadOnlyList<WarehouseItemMutation> Mutations,
    string AuditReference,
    Guid? OutboxEventId)
{
    public bool Succeeded => Status is
        WarehouseTransferResultStatus.Deposited or
        WarehouseTransferResultStatus.Withdrawn or
        WarehouseTransferResultStatus.InternalMoved or
        WarehouseTransferResultStatus.Stacked or
        WarehouseTransferResultStatus.Swapped;

    public void Validate()
    {
        var slotsValid = Operation switch
        {
            WarehouseTransferOperation.Deposit =>
                (WarehouseCapacityPolicy.IsValidWarehouseSlot(
                     WarehouseSlot) ||
                 WarehouseSlot ==
                    WarehouseCapacityPolicy.AutomaticWarehouseSlot) &&
                WarehouseCapacityPolicy.IsValidKitBagSlot(KitBagSlot) &&
                DestinationWarehouseSlot == -1,
            WarehouseTransferOperation.Withdraw =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    WarehouseSlot) &&
                (WarehouseCapacityPolicy.IsValidKitBagSlot(KitBagSlot) ||
                 KitBagSlot == WarehouseCapacityPolicy.AutomaticKitBagSlot) &&
                DestinationWarehouseSlot == -1,
            WarehouseTransferOperation.InternalMove =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    WarehouseSlot) &&
                KitBagSlot == WarehouseCapacityPolicy.AutomaticKitBagSlot &&
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    DestinationWarehouseSlot) &&
                WarehouseSlot != DestinationWarehouseSlot,
            _ => false
        };
        var actualKitBagSlotValid = Operation ==
                WarehouseTransferOperation.InternalMove
            ? ActualKitBagSlot == WarehouseCapacityPolicy.AutomaticKitBagSlot
            : WarehouseCapacityPolicy.IsValidKitBagSlot(ActualKitBagSlot) ||
              !Succeeded &&
              ActualKitBagSlot == WarehouseCapacityPolicy.AutomaticKitBagSlot;
        var actualWarehouseSlotValid =
            WarehouseCapacityPolicy.IsValidWarehouseSlot(
                ActualWarehouseSlot) ||
            !Succeeded &&
            ActualWarehouseSlot ==
                WarehouseCapacityPolicy.AutomaticWarehouseSlot;
        if (CharacterId <= 0 ||
            !slotsValid ||
            !actualKitBagSlotValid ||
            !actualWarehouseSlotValid ||
            !Enum.IsDefined(Status) ||
            !WarehouseCapacityPolicy.IsValidCapacity(Capacity) ||
            WarehouseRevision < 0 ||
            InventoryRevision < 0 ||
            Mutations is null ||
            Mutations.Count >
                WarehouseCapacityPolicy.MaximumTransferMutations ||
            Mutations.Any(static mutation => !mutation.IsValid) ||
            Mutations.Select(static mutation => mutation.ItemInstanceId)
                .Distinct().Count() != Mutations.Count ||
            string.IsNullOrWhiteSpace(AuditReference) ||
            AuditReference.Length > 256 ||
            AuditReference.Any(char.IsControl) ||
            Succeeded != (OutboxEventId is { } id && id != Guid.Empty) ||
            Succeeded != (MovedQuantity > 0 && Mutations.Count > 0) ||
            Succeeded && InventoryRevision <= 0 ||
            !Succeeded && MovedQuantity != 0)
        {
            throw new InvalidDataException(
                "The warehouse transfer receipt is inconsistent.");
        }
    }
}

internal enum WarehouseTransferExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record WarehouseTransferExecutionResult(
    WarehouseTransferExecutionDisposition Disposition,
    WarehouseTransferExecutionReceipt? Receipt)
{
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is WarehouseTransferExecutionDisposition.Committed or
            WarehouseTransferExecutionDisposition.Duplicate;

    public bool IsDurable => Receipt is not null;

    public static WarehouseTransferExecutionResult Terminal(
        WarehouseTransferExecutionDisposition disposition,
        WarehouseTransferExecutionReceipt? receipt = null)
    {
        receipt?.Validate();
        var requiresReceipt = disposition is
            WarehouseTransferExecutionDisposition.Committed or
            WarehouseTransferExecutionDisposition.Duplicate or
            WarehouseTransferExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null) ||
            disposition == WarehouseTransferExecutionDisposition.Committed &&
            receipt?.Succeeded != true ||
            disposition ==
                WarehouseTransferExecutionDisposition.TerminalRejected &&
            receipt?.Succeeded != false)
        {
            throw new ArgumentException(
                "The warehouse transfer result is inconsistent.");
        }

        return new(disposition, receipt);
    }
}

internal interface IWarehouseTransferCommandExecutor
{
    Task<WarehouseTransferExecutionResult> ExecuteAsync(
        CommandEnvelope<WarehouseTransferCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<WarehouseTransferExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        WarehouseTransferReplayIntent intent,
        WarehouseOperationIdentity identity,
        CancellationToken cancellationToken = default);
}
