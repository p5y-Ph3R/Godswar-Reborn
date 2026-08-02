#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyHolySuitActionPacketBytes = 92;
inline constexpr std::uint32_t LegacySpartaHolySuitNpc = 5082;
inline constexpr std::uint32_t LegacyAthensHolySuitNpc = 5224;
inline constexpr std::int32_t LegacyHolySuitDialog = 29;
inline constexpr std::int32_t LegacyHolySuitStoreSubId = 101;
inline constexpr std::int32_t LegacyHolySuitTransferSubId = 201;
inline constexpr std::int32_t LegacyHolySuitConsumeWareSubId = 301;
inline constexpr std::int32_t LegacyHolySuitTransformSubId = 401;
inline constexpr std::int32_t LegacyHolySuitBagPageCount = 4;
inline constexpr std::int32_t LegacyHolySuitBagSlotsPerPage = 24;
inline constexpr std::int32_t LegacyHolySuitBagReferencePageStride = 100;
inline constexpr std::int32_t LegacyHolySuitBagReferenceMinimum = 0;
inline constexpr std::int32_t LegacyHolySuitBagReferenceMaximum =
    ((LegacyHolySuitBagPageCount - 1) *
     LegacyHolySuitBagReferencePageStride) +
    (LegacyHolySuitBagSlotsPerPage - 1);
inline constexpr std::size_t LegacyHolySuitArgumentCount = 18;
inline constexpr std::size_t LegacyHolySuitScratchArgument = 0;
inline constexpr std::size_t LegacyHolySuitFirstItemArgument = 6;
inline constexpr std::size_t LegacyHolySuitSecondItemArgument = 7;
inline constexpr std::size_t LegacyHolySuitAmountArgument = 10;
inline constexpr std::uint32_t LegacyHolySuitBlankAmount = 0xFFFFFFFFU;
inline constexpr std::uint32_t LegacyHolySuitMouseOnlyTransformPrisms = 20;

enum class LegacyHolySuitAction : std::uint8_t {
    StoreExperience = 1,
    TransferExperience = 2,
    ConsumeWare = 3,
    TransformExperience = 4,
};

enum class LegacyHolySuitPacketKind : std::uint8_t {
    UnrelatedOrNavigation = 0,
    Commit,
    InvalidMutation,
};

struct LegacyHolySuitCommand final {
    LegacyHolySuitAction action =
        LegacyHolySuitAction::StoreExperience;
    int primaryReference = -1;
    int secondaryReference = -1;
    // The stock client writes this four-byte field as an unsigned value,
    // except that all-one-bits is its blank visual-prefill sentinel.
    // Preserve the remaining high unsigned domain instead of treating
    // values above INT32_MAX as negative/missing amounts. Store Experience
    // Store Experience normalizes that sentinel to zero as its explicit
    // auto/max intent. Transform Experience normalizes it to the matching
    // 20-prism mouse-only default displayed by the compatibility Lua.
    std::uint32_t amount = 0;
};

// The original client reuses the four action sub-IDs for both page opening
// and confirmation. An all--1 argument vector is navigation. A value-bearing
// packet for this exact NPC/dialog boundary must match one canonical commit
// shape or fail closed.
LegacyHolySuitPacketKind ClassifyLegacyHolySuitPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolySuitCommand* command) noexcept;

bool TryReadLegacyHolySuitCommand(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolySuitCommand* command) noexcept;

} // namespace godswar::network
