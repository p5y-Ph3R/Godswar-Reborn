#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyStorageItemOpcode = 10052;
inline constexpr std::size_t LegacyKitBagItemDeletePacketBytes = 28;
inline constexpr std::uint16_t LegacyKitBagPageCount = 4;
inline constexpr std::uint16_t LegacyKitBagSlotsPerPage = 24;

bool TryReadLegacyKitBagItemDelete(
    const void* packet,
    std::size_t packetBytes,
    int* bagSlot) noexcept;

} // namespace godswar::network
