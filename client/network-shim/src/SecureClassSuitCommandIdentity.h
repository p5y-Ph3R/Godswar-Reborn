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
inline constexpr std::int32_t LegacyClassSuitBagSlotMinimum = 0;
inline constexpr std::int32_t LegacyClassSuitBagSlotMaximum = 95;
inline constexpr std::int32_t LegacyClassSuitBagReferenceMinimum = 100;
inline constexpr std::int32_t LegacyClassSuitBagReferenceMaximum = 195;
inline constexpr std::int32_t LegacyClassSuitEquippedWeaponReference = 205;

enum class LegacyClassSuitAction : std::int32_t {
    InitialMenu = -1,
    ExchangeTierI = 100,
    AddAttribute = 101,
    DeleteAttribute = 102,
    Instructions = 103,
    ConvertToCommon = 104,
    UpgradeTierII = 105,
    UpgradeTierIII = 106,
    AddFifthAttribute = 107,
    UpgradeTierIV = 108,
};

enum class LegacyClassSuitPacketKind : std::uint8_t {
    UnrelatedOrNavigation = 0,
    Navigation,
    Commit,
    InvalidMutation,
};

struct LegacyClassSuitCommand final {
    LegacyClassSuitAction action =
        LegacyClassSuitAction::ExchangeTierI;
    std::uint32_t npcId = 0;
    // Normalized bag slot 0..95, or the equipped-weapon sentinel 205.
    int gearReference = -1;
    int secondaryBagSlot = -1;
    int tertiaryBagSlot = -1;
};

// The stock client reuses each transformation sub-ID to open its page and to
// confirm it. Exact navigation is reported separately so the operation
// registry can correlate the stock select/clear/final-action sequence without
// assigning an identity to the first page-opening action. Every unresolved
// Class Suit action deliberately remains ordinary legacy traffic.
LegacyClassSuitPacketKind ClassifyLegacyClassSuitPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyClassSuitCommand* command) noexcept;

bool TryReadLegacyClassSuitCommand(
    const void* packet,
    std::size_t packetBytes,
    LegacyClassSuitCommand* command) noexcept;

} // namespace godswar::network
