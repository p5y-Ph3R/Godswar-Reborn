#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyForgeStartOpcode = 10109;
inline constexpr std::uint16_t LegacyForgeSelectionOpcode = 10110;
inline constexpr std::uint16_t LegacyForgeReplacementSelectionOpcode = 10111;
inline constexpr std::uint16_t LegacyForgeReplacementActionOpcode = 10112;
inline constexpr std::uint16_t LegacyForgeCancelOpcode = 10117;
inline constexpr std::size_t LegacyForgeStartPacketBytes = 40;
inline constexpr std::size_t LegacyForgeSelectionPacketBytes = 60;
inline constexpr std::size_t LegacyForgeCancelPacketBytes = 4;
inline constexpr std::uint32_t LegacyOrdinaryForgeMode = 0;
inline constexpr std::uint32_t LegacyForgeEquipmentDestination = 0;
inline constexpr std::uint32_t LegacyForgePrimaryMaterialDestination = 1;
inline constexpr std::uint32_t LegacyForgeOddsDescriptorDestination = 5;
inline constexpr std::uint32_t LegacyForgeOddsIncrementAction = 88;
inline constexpr int LegacyForgeSlotsPerPage = 24;
inline constexpr int LegacyForgePageCount = 4;

struct LegacyForgeSelection final {
    int bagSlot = -1;
    std::uint32_t destination = 0;
    std::uint32_t mode = 0;
};

bool IsLegacyForgeOpcode(std::uint16_t opcode) noexcept;

bool TryReadLegacyForgeSelection(
    const void* packet,
    std::size_t packetBytes,
    LegacyForgeSelection* selection) noexcept;

bool TryReadLegacyForgeStart(
    const void* packet,
    std::size_t packetBytes,
    std::uint32_t* mode) noexcept;

bool TryReadLegacyForgeCancel(
    const void* packet,
    std::size_t packetBytes) noexcept;

bool TryReadLegacyForgeReplacement(
    const void* packet,
    std::size_t packetBytes,
    std::uint16_t expectedOpcode) noexcept;

} // namespace godswar::network
