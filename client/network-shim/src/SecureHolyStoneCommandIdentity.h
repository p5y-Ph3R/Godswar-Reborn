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
inline constexpr std::int32_t LegacyHolyStoneUpgradeSubId = 401;
inline constexpr std::int32_t
    LegacyHolyStoneImplementSpiritSubId = 501;
inline constexpr std::int32_t LegacyHolyStoneCombineSubId = 601;
inline constexpr std::int32_t
    LegacyHolyStoneAdvancedDrillSubId = 701;
inline constexpr std::int32_t LegacyHolyStoneBagPageCount = 4;
inline constexpr std::int32_t LegacyHolyStoneBagSlotsPerPage = 24;
inline constexpr std::int32_t LegacyHolyStoneBagPageStride = 100;
inline constexpr std::int32_t LegacyHolyStoneBagReferenceMinimum = 0;
inline constexpr std::int32_t LegacyHolyStoneBagReferenceMaximum =
    ((LegacyHolyStoneBagPageCount - 1) *
     LegacyHolyStoneBagPageStride) +
    (LegacyHolyStoneBagSlotsPerPage - 1);
inline constexpr std::size_t LegacyHolyStoneArgumentCount = 18;

enum class LegacyHolyStoneAction : std::uint8_t {
    Mount = 1,
    Remove = 2,
    Drill = 3,
    AdvancedDrill = 4,
    Upgrade = 5,
    Combine = 6,
    ImplementSpirit = 7,
};

enum class LegacyHolyStonePacketKind : std::uint8_t {
    UnrelatedOrNavigation = 0,
    Navigation,
    Commit,
    StagedCommit,
    InvalidMutation,
};

struct LegacyHolyStoneCommand final {
    LegacyHolyStoneAction action = LegacyHolyStoneAction::Mount;
    std::uint32_t npcId = 0;
    // The parser normalizes wire reference page*100+pageSlot into the
    // authoritative linear bag slot page*24+pageSlot (0..95).
    int targetReference = -1;
    // Mount/AdvancedDrill: normalized material bag slot. Remove: one-based
    // socket ordinal. Drill: -1.
    int secondaryValue = -1;
    // Combine preserves the stock dialog's fixed ItemBtn1..ItemBtn4 role
    // order. These are normalized authoritative bag slots, not raw wire
    // references.
    std::size_t combinationCount = 0;
    int combinationBagSlots[4]{-1, -1, -1, -1};
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
