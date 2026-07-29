#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t
    LegacyEquipmentBagTransferPacketBytes = 80;
inline constexpr std::uint16_t
    LegacyEquipmentSlotMaximum = 20;

bool TryReadLegacyEquipmentBagTransfer(
    const void* packet,
    std::size_t packetBytes,
    int* equipmentSlot,
    int* bagSlot) noexcept;

} // namespace godswar::network
