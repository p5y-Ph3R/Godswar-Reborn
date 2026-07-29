#include "SecureEquipmentBagTransferIdentity.h"

#include "SecureKitBagItemDeleteIdentity.h"
#include "SecureLegacyCommandIdentity.h"

namespace godswar::network {
namespace {

std::uint16_t ReadUInt16Little(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

} // namespace

bool TryReadLegacyEquipmentBagTransfer(
    const void* packet,
    std::size_t packetBytes,
    int* equipmentSlot,
    int* bagSlot) noexcept {
    std::uint16_t opcode = 0;
    if (equipmentSlot == nullptr ||
        bagSlot == nullptr ||
        packetBytes != LegacyEquipmentBagTransferPacketBytes ||
        !TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode) ||
        opcode != LegacyStorageItemOpcode) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const std::uint16_t equipment =
        ReadUInt16Little(bytes + 8);
    const std::uint16_t marker =
        ReadUInt16Little(bytes + 10);
    const std::uint16_t page =
        ReadUInt16Little(bytes + 12);
    const std::uint16_t index =
        ReadUInt16Little(bytes + 14);
    if (equipment > LegacyEquipmentSlotMaximum ||
        marker != UINT16_MAX ||
        page >= LegacyKitBagPageCount ||
        index >= LegacyKitBagSlotsPerPage) {
        return false;
    }

    *equipmentSlot = static_cast<int>(equipment);
    *bagSlot = static_cast<int>(
        page * LegacyKitBagSlotsPerPage + index);
    return true;
}

} // namespace godswar::network
