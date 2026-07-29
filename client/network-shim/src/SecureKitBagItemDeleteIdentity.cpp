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

bool TryReadLegacyKitBagItemDelete(
    const void* packet,
    std::size_t packetBytes,
    int* bagSlot) noexcept {
    std::uint16_t opcode = 0;
    if (bagSlot == nullptr ||
        packetBytes != LegacyKitBagItemDeletePacketBytes ||
        !TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode) ||
        opcode != LegacyStorageItemOpcode) {
        return false;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const std::uint16_t sourcePage =
        ReadUInt16Little(bytes + 8);
    const std::uint16_t sourceIndex =
        ReadUInt16Little(bytes + 10);
    const std::uint16_t destinationPage =
        ReadUInt16Little(bytes + 12);
    const std::uint16_t destinationIndex =
        ReadUInt16Little(bytes + 14);
    if (sourcePage >= LegacyKitBagPageCount ||
        sourceIndex >= LegacyKitBagSlotsPerPage ||
        destinationPage != UINT16_MAX ||
        destinationIndex != UINT16_MAX) {
        return false;
    }

    *bagSlot = static_cast<int>(
        sourcePage * LegacyKitBagSlotsPerPage +
        sourceIndex);
    return true;
}

} // namespace godswar::network
