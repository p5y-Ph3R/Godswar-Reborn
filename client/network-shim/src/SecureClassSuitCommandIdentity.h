#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyClassSuitActionPacketBytes = 92;
inline constexpr std::uint32_t LegacySpartaClassSuitNpc = 5067;
inline constexpr std::uint32_t LegacyAthensClassSuitNpc = 5209;
inline constexpr std::int32_t LegacyClassSuitDialog = 37;
inline constexpr std::size_t LegacyClassSuitArgumentCount = 18;
inline constexpr std::size_t LegacyClassSuitScratchArgument = 0;
inline constexpr std::size_t LegacyClassSuitGearArgument = 6;
inline constexpr std::size_t LegacyClassSuitInsigniaArgument = 7;
inline constexpr std::size_t LegacyClassSuitThirdItemArgument = 8;
inline constexpr std::int32_t LegacyClassSuitBagReferenceMinimum = 100;
inline constexpr std::int32_t LegacyClassSuitBagReferenceMaximum = 195;

enum class LegacyClassSuitAction : std::int32_t {
    ExchangeTierI = 100,
    AddAttribute = 101,
    DeleteAttribute = 102,
    ConvertToCommon = 104,
    UpgradeTierII = 105,
    UpgradeTierIII = 106,
    UpgradeTierIV = 108,
};

enum class LegacyClassSuitPacketKind : std::uint8_t {
    UnrelatedOrNavigation = 0,
    Commit,
    InvalidMutation,
};

struct LegacyClassSuitCommand final {
    LegacyClassSuitAction action =
        LegacyClassSuitAction::ExchangeTierI;
    std::uint32_t npcId = 0;
    int gearBagSlot = -1;
    int secondaryBagSlot = -1;
    int tertiaryBagSlot = -1;
};

// The stock client reuses each transformation sub-ID to open its page and to
// confirm it. Only an exact value-bearing packet for the two physical Gear
// Mentor NPCs is a valuable command. Navigation and every unresolved Class
// Suit action deliberately remain ordinary legacy traffic.
LegacyClassSuitPacketKind ClassifyLegacyClassSuitPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyClassSuitCommand* command) noexcept;

bool TryReadLegacyClassSuitCommand(
    const void* packet,
    std::size_t packetBytes,
    LegacyClassSuitCommand* command) noexcept;

} // namespace godswar::network
