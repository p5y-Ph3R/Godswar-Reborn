using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Application.Warehouse;

/// <summary>
/// Server-owned interpretation of one stock MSG_STORAGE_ITEM frame. Bag slots
/// are flattened to 0..95 so no client page/cell values escape this boundary.
/// </summary>
internal readonly record struct WarehouseTransferIntent(
    WarehouseTransferOperation Operation,
    int WarehouseSlot,
    int KitBagSlot,
    int DestinationWarehouseSlot,
    int Money,
    WarehouseStorageType StorageType);

internal static class WarehouseWireProtocol
{
    public const int TransferPacketBytes = 20;
    public const int KitBagSlotsPerPage = 24;
    public const int KitBagPageCount = 4;

    private const byte WithdrawOrInternalDirection = 0;
    private const byte DepositDirection = 1;

    public static bool TryReadTransfer(
        GamePacket packet,
        out WarehouseTransferIntent intent)
    {
        intent = default;
        if (packet is null ||
            packet.Length != TransferPacketBytes ||
            packet.Buffer.Length != TransferPacketBytes ||
            packet.Opcode != Opcodes.WarehouseTransfer)
        {
            return false;
        }

        var bytes = packet.Buffer.AsSpan();
        var warehouseSlot = BinaryPrimitives.ReadInt16LittleEndian(
            bytes.Slice(4, 2));
        var firstTarget = BinaryPrimitives.ReadInt16LittleEndian(
            bytes.Slice(6, 2));
        var secondTarget = BinaryPrimitives.ReadInt16LittleEndian(
            bytes.Slice(8, 2));
        var money = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(12, 4));
        var direction = bytes[16];
        var storageType = (WarehouseStorageType)
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(18, 2));

        // Stock warehouse-item movement never transfers stored money. Reject
        // this dormant field instead of turning an item drag into a wallet API.
        if (money != 0)
        {
            return false;
        }

        if (direction == DepositDirection)
        {
            if (!IsAutomaticOrWarehouseSlot(warehouseSlot) ||
                !TryFlattenKitBagSlot(
                    firstTarget,
                    secondTarget,
                    out var kitBagSlot))
            {
                return false;
            }

            // The native deposit sender leaves the two-byte storage-type tail
            // as scratch. A deposit is always normal storage; never authorize
            // award storage from those uninitialized bytes.
            intent = new(
                WarehouseTransferOperation.Deposit,
                warehouseSlot,
                kitBagSlot,
                WarehouseCapacityPolicy.AutomaticWarehouseSlot,
                0,
                WarehouseStorageType.Normal);
            return true;
        }

        if (direction != WithdrawOrInternalDirection ||
            !WarehouseCapacityPolicy.IsValidWarehouseSlot(warehouseSlot))
        {
            return false;
        }

        // The stock normal-warehouse drag sender writes 1 in the storage-type
        // tail for an internal move. The matching client receive path ignores
        // that field for this exact source/destination/-1 shape. Normalize the
        // native value without admitting award-storage withdrawals.
        if (secondTarget == WarehouseCapacityPolicy.AutomaticKitBagSlot &&
            WarehouseCapacityPolicy.IsValidWarehouseSlot(firstTarget) &&
            firstTarget != warehouseSlot &&
            storageType is WarehouseStorageType.Normal or
                WarehouseStorageType.Award)
        {
            intent = new(
                WarehouseTransferOperation.InternalMove,
                warehouseSlot,
                WarehouseCapacityPolicy.AutomaticKitBagSlot,
                firstTarget,
                0,
                WarehouseStorageType.Normal);
            return true;
        }

        if (storageType != WarehouseStorageType.Normal)
        {
            return false;
        }

        if (firstTarget == WarehouseCapacityPolicy.AutomaticKitBagSlot &&
            secondTarget == WarehouseCapacityPolicy.AutomaticKitBagSlot)
        {
            intent = new(
                WarehouseTransferOperation.Withdraw,
                warehouseSlot,
                WarehouseCapacityPolicy.AutomaticKitBagSlot,
                WarehouseCapacityPolicy.AutomaticWarehouseSlot,
                0,
                WarehouseStorageType.Normal);
            return true;
        }

        if (!TryFlattenKitBagSlot(
                firstTarget,
                secondTarget,
                out var destinationKitBagSlot))
        {
            return false;
        }

        intent = new(
            WarehouseTransferOperation.Withdraw,
            warehouseSlot,
            destinationKitBagSlot,
            WarehouseCapacityPolicy.AutomaticWarehouseSlot,
            0,
            WarehouseStorageType.Normal);
        return true;
    }

    public static bool IsValidIntent(in WarehouseTransferIntent intent) =>
        intent.Money == 0 &&
        intent.StorageType == WarehouseStorageType.Normal &&
        intent.Operation switch
        {
            WarehouseTransferOperation.Deposit =>
                IsAutomaticOrWarehouseSlot(intent.WarehouseSlot) &&
                WarehouseCapacityPolicy.IsValidKitBagSlot(
                    intent.KitBagSlot) &&
                intent.DestinationWarehouseSlot ==
                    WarehouseCapacityPolicy.AutomaticWarehouseSlot,
            WarehouseTransferOperation.Withdraw =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    intent.WarehouseSlot) &&
                (WarehouseCapacityPolicy.IsValidKitBagSlot(
                     intent.KitBagSlot) ||
                 intent.KitBagSlot ==
                    WarehouseCapacityPolicy.AutomaticKitBagSlot) &&
                intent.DestinationWarehouseSlot ==
                    WarehouseCapacityPolicy.AutomaticWarehouseSlot,
            WarehouseTransferOperation.InternalMove =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    intent.WarehouseSlot) &&
                intent.KitBagSlot ==
                    WarehouseCapacityPolicy.AutomaticKitBagSlot &&
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    intent.DestinationWarehouseSlot) &&
                intent.DestinationWarehouseSlot != intent.WarehouseSlot,
            _ => false
        };

    private static bool TryFlattenKitBagSlot(
        int page,
        int cell,
        out int slot)
    {
        slot = -1;
        if (page is < 0 or >= KitBagPageCount ||
            cell is < 0 or >= KitBagSlotsPerPage)
        {
            return false;
        }

        slot = (page * KitBagSlotsPerPage) + cell;
        return true;
    }

    private static bool IsAutomaticOrWarehouseSlot(int slot) =>
        slot == WarehouseCapacityPolicy.AutomaticWarehouseSlot ||
        WarehouseCapacityPolicy.IsValidWarehouseSlot(slot);
}
