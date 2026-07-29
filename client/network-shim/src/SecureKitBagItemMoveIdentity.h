#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t
    LegacyKitBagItemMoveCompactPacketBytes = 20;
inline constexpr std::size_t
    LegacyKitBagItemMoveDetailedPacketBytes = 80;

bool TryReadLegacyKitBagItemMove(
    const void* packet,
    std::size_t packetBytes,
    int* sourceBagSlot,
    int* destinationBagSlot) noexcept;

} // namespace godswar::network
