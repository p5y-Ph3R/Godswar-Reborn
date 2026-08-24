using Godswar.Server.Application.Warehouse;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseTransferCommandExecutor
{
    private static TransferEndpoints ResolveEndpoints(
        WarehouseTransferCommand command) =>
        command.Operation switch
        {
            WarehouseTransferOperation.Deposit => new(
                1,
                3,
                command.KitBagSlot,
                command.WarehouseSlot,
                command.WarehouseSlot < 0),
            WarehouseTransferOperation.Withdraw => new(
                3,
                1,
                command.WarehouseSlot,
                command.KitBagSlot,
                command.KitBagSlot < 0),
            WarehouseTransferOperation.InternalMove => new(
                3,
                3,
                command.WarehouseSlot,
                command.DestinationWarehouseSlot,
                false),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

    private static bool WarehouseEndpointExceedsCapacity(
        short location,
        int slot,
        int capacity) => location == 3 && slot >= capacity;

    private static int DestinationLimit(
        short location,
        LockedCharacter character) =>
        location == 3
            ? character.Capacity
            : WarehouseCapacityPolicy.MaximumKitBagSlot + 1;

    private static bool StackCompatible(
        CompactItemEntry first,
        CompactItemEntry second) =>
        first.Id == second.Id &&
        first with { Stack = 1, LinkedSealedPetId = 0 } ==
        second with { Stack = 1, LinkedSealedPetId = 0 };

    private static TransferPlan Rejected(
        WarehouseTransferCommand command,
        WarehouseTransferResultStatus status,
        TransferEndpoints endpoints,
        LockedItem? source,
        LockedItem? destination) =>
        CreatePlan(
            command,
            status,
            endpoints,
            source,
            destination,
            [],
            0,
            source?.Item.Stack ?? 0,
            []);

    private static TransferPlan CreatePlan(
        WarehouseTransferCommand command,
        WarehouseTransferResultStatus status,
        TransferEndpoints endpoints,
        LockedItem? source,
        LockedItem? destination,
        IReadOnlyList<LockedItem> stackDestinations,
        int moved,
        int sourceAfter,
        IReadOnlyList<WarehouseItemMutation> mutations)
    {
        var actualWarehouse = command.Operation switch
        {
            WarehouseTransferOperation.Deposit => endpoints.DestinationSlot,
            WarehouseTransferOperation.Withdraw => endpoints.SourceSlot,
            WarehouseTransferOperation.InternalMove =>
                endpoints.DestinationSlot,
            _ => -1
        };
        var actualBag = command.Operation switch
        {
            WarehouseTransferOperation.Deposit => endpoints.SourceSlot,
            WarehouseTransferOperation.Withdraw => endpoints.DestinationSlot,
            _ => -1
        };
        return new TransferPlan(
            status,
            source,
            destination,
            stackDestinations,
            endpoints.SourceLocation,
            endpoints.DestinationLocation,
            endpoints.SourceSlot,
            endpoints.DestinationSlot,
            actualWarehouse,
            actualBag,
            moved,
            sourceAfter,
            mutations);
    }

    private static WarehouseItemMutation CreateMutation(
        LockedItem item,
        short? afterLocation,
        int? afterSlot,
        int? afterStack) =>
        new(
            item.ItemInstanceId,
            checked((int)item.Item.Id),
            (WarehouseInventoryLocation)item.Location,
            item.Slot,
            item.Item.Stack,
            afterLocation.HasValue
                ? (WarehouseInventoryLocation)afterLocation.Value
                : null,
            afterSlot,
            afterStack);

    private sealed record TransferEndpoints(
        short SourceLocation,
        short DestinationLocation,
        int SourceSlot,
        int DestinationSlot,
        bool DestinationWasAutomatic);
}
