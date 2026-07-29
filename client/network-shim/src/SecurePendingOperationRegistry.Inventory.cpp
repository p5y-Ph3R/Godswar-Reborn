#include "SecurePendingOperationRegistry.h"

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeInventoryPacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor,
    bool* recognized) noexcept {
    if (recognized == nullptr) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    *recognized = false;

    int kitBagDeleteSlot = -1;
    if (TryReadLegacyKitBagItemDelete(
            packet,
            packetBytes,
            &kitBagDeleteSlot)) {
        *recognized = true;
        return DescribeKitBagItemDelete(
            kitBagDeleteSlot,
            now,
            descriptor);
    }

    int equipmentSlot = -1;
    int equipmentBagSlot = -1;
    if (TryReadLegacyEquipmentBagTransfer(
            packet,
            packetBytes,
            &equipmentSlot,
            &equipmentBagSlot)) {
        *recognized = true;
        return DescribeEquipmentBagTransfer(
            equipmentSlot,
            equipmentBagSlot,
            now,
            descriptor);
    }

    int kitBagMoveSourceSlot = -1;
    int kitBagMoveDestinationSlot = -1;
    if (TryReadLegacyKitBagItemMove(
            packet,
            packetBytes,
            &kitBagMoveSourceSlot,
            &kitBagMoveDestinationSlot)) {
        *recognized = true;
        return DescribeKitBagItemMove(
            kitBagMoveSourceSlot,
            kitBagMoveDestinationSlot,
            now,
            descriptor);
    }

    return SecureOperationRegistryResult::Success;
}

} // namespace godswar::network
