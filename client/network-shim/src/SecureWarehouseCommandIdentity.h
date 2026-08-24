#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyWarehouseTransferOpcode = 10059;
inline constexpr std::size_t LegacyWarehouseTransferPacketBytes = 20;
inline constexpr int LegacyWarehouseSlots = 360;
inline constexpr int LegacyWarehouseKitBagPages = 4;
inline constexpr int LegacyWarehouseKitBagSlotsPerPage = 24;

inline constexpr std::size_t LegacyWarehouseManagerPacketBytes = 92;
inline constexpr std::uint32_t LegacyAthensWarehouseManagerNpc = 5273;
inline constexpr std::uint32_t LegacySpartaWarehouseManagerNpc = 5131;
inline constexpr std::int32_t LegacyWarehouseManagerDialog = 106;
inline constexpr std::int32_t LegacyWarehouseManagerInitialSubId = -1;
inline constexpr std::int32_t LegacyWarehouseManagerExpandSubId = 100;

inline constexpr std::uint32_t LegacyWarehouseDepositedResult = 1;
inline constexpr std::uint32_t LegacyWarehouseWithdrawnResult = 2;
inline constexpr std::uint32_t LegacyWarehouseInternalMovedResult = 3;
inline constexpr std::uint32_t LegacyWarehouseStackedResult = 4;
inline constexpr std::uint32_t LegacyWarehouseSwappedResult = 5;
inline constexpr std::uint32_t LegacyWarehouseEmptySourceResult = 10;
inline constexpr std::uint32_t LegacyWarehouseDestinationOccupiedResult = 11;
inline constexpr std::uint32_t LegacyWarehouseBagFullResult = 12;
inline constexpr std::uint32_t LegacyWarehouseCapacityExceededResult = 13;
inline constexpr std::uint32_t LegacyWarehouseStackIncompatibleResult = 14;
inline constexpr std::uint32_t LegacyWarehouseConcurrentConflictResult = 15;
inline constexpr std::uint32_t LegacyWarehouseRestrictedItemResult = 16;

inline constexpr std::uint32_t
    LegacyWarehouseFirstExpansionSuccessResult = 201;
inline constexpr std::uint32_t
    LegacyWarehouseLastExpansionSuccessResult = 208;
inline constexpr std::uint32_t LegacyWarehouseMissingKeysResultBase =
    900000;
inline constexpr std::uint32_t LegacyWarehouseAlreadyMaximumResult = 998;
inline constexpr std::uint32_t LegacyWarehouseExpansionFailedResult = 999;

inline constexpr bool IsLegacyWarehouseExpansionSuccessResult(
    std::uint32_t result) noexcept {
    return result >= LegacyWarehouseFirstExpansionSuccessResult &&
        result <= LegacyWarehouseLastExpansionSuccessResult;
}

inline constexpr bool IsLegacyWarehouseMissingKeysResult(
    std::uint32_t result) noexcept {
    if (result <= LegacyWarehouseMissingKeysResultBase) {
        return false;
    }
    const auto encoded = result - LegacyWarehouseMissingKeysResultBase;
    const auto targetBox = encoded / 100;
    const auto keyCount = encoded % 100;
    return targetBox >= 2 && targetBox <= 9 &&
        keyCount >= 1 && keyCount <= 99;
}

enum class LegacyWarehousePacketKind : std::uint8_t {
    Unrelated = 0,
    Navigation,
    Transfer,
    Expansion,
    InvalidMutation,
};

enum class LegacyWarehouseTransferOperation : std::uint8_t {
    Deposit = 1,
    Withdraw = 2,
    InternalMove = 3,
};

struct LegacyWarehouseTransferCommand final {
    LegacyWarehouseTransferOperation operation =
        LegacyWarehouseTransferOperation::Deposit;
    int warehouseSlot = -1;
    int kitBagSlot = -1;
    int destinationWarehouseSlot = -1;
};

struct LegacyWarehouseExpansionCommand final {
    std::uint32_t npcId = 0;
};

// MSG_STORAGE_ITEM is a fixed 20-byte whole-stack operation. Padding and the
// normal-deposit tail and the stock internal-move tail are normalized and are
// excluded from identity. Money and award-storage withdrawals are rejected.
LegacyWarehousePacketKind ClassifyLegacyWarehouseTransferPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyWarehouseTransferCommand* command) noexcept;

// Dialog 106 action 100 is the only mutating Warehouse Manager request.
// Initial sub-id -1 is navigation and receives no operation identity.
LegacyWarehousePacketKind ClassifyLegacyWarehouseExpansionPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyWarehouseExpansionCommand* command) noexcept;

} // namespace godswar::network
