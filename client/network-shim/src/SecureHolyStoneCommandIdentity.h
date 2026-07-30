#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyHolyStoneActionPacketBytes = 92;
inline constexpr std::uint32_t LegacySpartaHolyStoneNpc = 5083;
inline constexpr std::uint32_t LegacyAthensHolyStoneNpc = 5225;
inline constexpr std::int32_t LegacyHolyStoneDialog = 30;
inline constexpr std::int32_t LegacyHolyStoneMountSubId = 101;
inline constexpr std::int32_t LegacyHolyStoneRemoveSubId = 201;
inline constexpr std::int32_t LegacyHolyStoneDrillSubId = 301;
inline constexpr std::int32_t
    LegacyHolyStoneAdvancedDrillSubId = 701;
inline constexpr std::int32_t LegacyHolyStoneBagReferenceMinimum = 100;
inline constexpr std::int32_t LegacyHolyStoneBagReferenceMaximum = 195;
inline constexpr std::int32_t
    LegacyCapturedEquippedHolyStoneReference = 205;
inline constexpr std::size_t LegacyHolyStoneArgumentCount = 18;

enum class LegacyHolyStoneAction : std::uint8_t {
    Mount = 1,
    Remove = 2,
    Drill = 3,
};

enum class LegacyHolyStonePacketKind : std::uint8_t {
    UnrelatedOrNavigation = 0,
    Commit,
    InvalidMutation,
};

struct LegacyHolyStoneCommand final {
    LegacyHolyStoneAction action = LegacyHolyStoneAction::Mount;
    int targetReference = -1;
    int secondaryValue = -1;
};

// Classifies the Holy Stone boundary without conflating a benign page
// transition with a malformed valuable command. InvalidMutation must fail
// closed; UnrelatedOrNavigation may continue without an operation identity.
LegacyHolyStonePacketKind ClassifyLegacyHolyStonePacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolyStoneCommand* command) noexcept;

// Compatibility helper for callers that only need exact commits.
bool TryReadLegacyHolyStoneCommand(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolyStoneCommand* command) noexcept;

} // namespace godswar::network
