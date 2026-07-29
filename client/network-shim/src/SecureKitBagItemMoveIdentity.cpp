#include "SecureKitBagItemMoveIdentity.h"

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

bool TryReadLegacyKitBagItemMove(
    const void* packet,
    std::size_t packetBytes,
    int* sourceBagSlot,
    int* destinationBagSlot) noexcept {
    std::uint16_t opcode = 0;
    const bool isCompact =
        packetBytes == LegacyKitBagItemMoveCompactPacketBytes;
    const bool isDetailed =
        packetBytes == LegacyKitBagItemMoveDetailedPacketBytes;
    if (sourceBagSlot == nullptr ||
        destinationBagSlot == nullptr ||
        (!isCompact && !isDetailed) ||
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
        destinationPage >= LegacyKitBagPageCount ||
        destinationIndex >= LegacyKitBagSlotsPerPage ||
        (isCompact &&
            (ReadUInt16Little(bytes + 16) != UINT16_MAX ||
                ReadUInt16Little(bytes + 18) != UINT16_MAX))) {
        return false;
    }

    const int source = static_cast<int>(
        sourcePage * LegacyKitBagSlotsPerPage + sourceIndex);
    const int destination = static_cast<int>(
        destinationPage * LegacyKitBagSlotsPerPage +
        destinationIndex);
    if (source == destination) {
        return false;
    }

    *sourceBagSlot = source;
    *destinationBagSlot = destination;
    return true;
}

} // namespace godswar::network
