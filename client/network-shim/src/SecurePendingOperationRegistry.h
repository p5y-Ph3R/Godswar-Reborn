#pragma once

#include "SecureEquipmentBagTransferIdentity.h"
#include "SecureForgeCommandIdentity.h"
#include "SecureKitBagItemDeleteIdentity.h"
#include "SecureKitBagItemMoveIdentity.h"
#include "SecureLegacyCommandIdentity.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecurePendingOperationCapacity = 16;
inline constexpr std::size_t SecureResolvedOperationCapacity = 16;
inline constexpr std::size_t SecureGearSelectionCapacity = 3;
inline constexpr std::size_t SecureForgeOddsCapacity = 25;
inline constexpr std::uint64_t
    SecurePendingOperationLifetimeMilliseconds = 10 * 60 * 1000;
inline constexpr std::uint64_t
    SecureSelectionClearCorrelationLifetimeMilliseconds = 1000;

using SecureOperationRandomGenerator =
    bool (*)(
        void* context,
        void* destination,
        std::size_t destinationBytes) noexcept;
using SecureOperationClock =
    bool (*)(
        void* context,
        std::uint64_t* unixMilliseconds) noexcept;

enum class SecureOperationRegistryResult : std::uint8_t {
    Success = 0,
    InvalidPacket,
    NoPrincipal,
    NoSelection,
    Capacity,
    RandomFailure,
    ClockFailure,
    UnknownOperation,
    FamilyConflict,
    NoCharacter,
};

struct SecureForgeOddsSelection final {
    int bagSlot = -1;
    std::uint8_t quantity = 0;
    bool descriptorLinked = false;
};

struct SecurePendingOperationSnapshot final {
    std::size_t pending = 0;
    std::size_t resolved = 0;
    bool hasPrincipal = false;
    bool hasCharacter = false;
    int characterId = -1;
    bool hasSelection = false;
    int selectedBagSlot = -1;
    std::size_t selectionCount = 0;
    int selectedBagSlots[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1};
    bool combinePageArmed = false;
    std::uint32_t combineNpcId = 0;
    bool hasForgeEquipment = false;
    int forgeEquipmentBagSlot = -1;
    bool hasForgePrimaryMaterial = false;
    int forgePrimaryMaterialBagSlot = -1;
    std::size_t forgeOddsCount = 0;
    std::uint32_t forgeOddsTotal = 0;
    bool forgeOddsFullyLinked = true;
    SecureForgeOddsSelection
        forgeOdds[SecureForgeOddsCapacity]{};
};

class SecurePendingOperationRegistry final {
public:
    SecurePendingOperationRegistry() noexcept;
    SecurePendingOperationRegistry(
        void* randomContext,
        SecureOperationRandomGenerator randomGenerator,
        void* clockContext,
        SecureOperationClock clock) noexcept;
    ~SecurePendingOperationRegistry() noexcept;

    SecurePendingOperationRegistry(
        const SecurePendingOperationRegistry&) = delete;
    SecurePendingOperationRegistry& operator=(
        const SecurePendingOperationRegistry&) = delete;

    SecureOperationRegistryResult DescribePacket(
        const void* packet,
        std::size_t packetBytes,
        LegacyPacketDescriptor* descriptor) noexcept;
    SecureOperationRegistryResult Resolve(
        const SecureLegacyCommandResult& result) noexcept;
    SecureOperationRegistryResult SetCharacter(
        int characterId) noexcept;
    SecurePendingOperationSnapshot Snapshot() noexcept;
    void Clear() noexcept;

private:
    struct Entry final {
        bool occupied = false;
        std::uint8_t
            principal[SecurePrincipalFingerprintBytes]{};
        SecureLegacyCommandFamily family =
            SecureLegacyCommandFamily::MakeAttributeStone;
        int characterId = -1;
        std::uint32_t npcId = 0;
        std::size_t selectionCount = 0;
        int bagSlots[SecureGearSelectionCapacity]{
            -1,
            -1,
            -1};
        bool capturesSelectionState = false;
        std::uint64_t selectionGeneration = 0;
        std::uint64_t combinePageGeneration = 0;
        bool capturesForgeState = false;
        int forgeEquipmentBagSlot = -1;
        int forgePrimaryMaterialBagSlot = -1;
        std::size_t forgeOddsCount = 0;
        SecureForgeOddsSelection
            forgeOdds[SecureForgeOddsCapacity]{};
        std::uint64_t expiresAt = 0;
        std::uint8_t operationId[16]{};
    };

    struct Tombstone final {
        bool occupied = false;
        SecureLegacyCommandFamily family =
            SecureLegacyCommandFamily::MakeAttributeStone;
        std::uint64_t expiresAt = 0;
        std::uint8_t operationId[16]{};
    };

    bool ReadNow(std::uint64_t* now) noexcept;
    SecureOperationRegistryResult DescribeForgePacket(
        const void* packet,
        std::size_t packetBytes,
        std::uint16_t opcode,
        std::uint64_t now,
        LegacyPacketDescriptor* descriptor) noexcept;
    SecureOperationRegistryResult DescribeInventoryPacket(
        const void* packet,
        std::size_t packetBytes,
        std::uint64_t now,
        LegacyPacketDescriptor* descriptor,
        bool* recognized) noexcept;
    SecureOperationRegistryResult DescribeKitBagItemDelete(
        int bagSlot,
        std::uint64_t now,
        LegacyPacketDescriptor* descriptor) noexcept;
    SecureOperationRegistryResult DescribeKitBagItemMove(
        int sourceBagSlot,
        int destinationBagSlot,
        std::uint64_t now,
        LegacyPacketDescriptor* descriptor) noexcept;
    SecureOperationRegistryResult DescribeEquipmentBagTransfer(
        int equipmentSlot,
        int bagSlot,
        std::uint64_t now,
        LegacyPacketDescriptor* descriptor) noexcept;
    void Prune(std::uint64_t now) noexcept;
    Entry* Find(
        SecureLegacyCommandFamily family,
        std::uint32_t npcId,
        const int* bagSlots,
        std::size_t selectionCount) noexcept;
    Entry* FindByOperationId(
        const std::uint8_t* operationId) noexcept;
    Tombstone* FindTombstone(
        const std::uint8_t* operationId) noexcept;
    Entry* FindAvailable() noexcept;
    Entry* FindForge() noexcept;
    Tombstone* FindTombstoneSlot() noexcept;
    bool RememberResolved(
        const Entry& entry,
        std::uint64_t now) noexcept;
    bool CreateOperationId(std::uint8_t* operationId) noexcept;
    bool StageForgeSelection(
        const LegacyForgeSelection& selection) noexcept;
    bool TryCaptureForgeIdentity(
        SecureForgeOddsSelection* odds,
        std::size_t* oddsCount) const noexcept;
    bool ForgeStateMatches(const Entry& entry) const noexcept;
    void ResetForgeState() noexcept;
    void SetPrincipal(
        const std::uint8_t* principal) noexcept;
    bool AddSelection(int bagSlot) noexcept;
    void RemoveSelection(
        int bagSlot,
        std::uint64_t now) noexcept;
    void BeginSelectionEdit() noexcept;
    void TrackSelectionClear(
        int bagSlot,
        std::uint64_t now) noexcept;
    void ResetSelectionClearCandidate() noexcept;
    void InvalidateSelectionClear() noexcept;
    void ResetSelectionState() noexcept;
    bool TryGetIdentitySelection(
        int* bagSlots,
        std::size_t* selectionCount) const noexcept;
    static bool EqualSelection(
        const int* first,
        std::size_t firstCount,
        const int* second,
        std::size_t secondCount) noexcept;
    void ClearEntry(Entry* entry) noexcept;
    void ClearTombstone(Tombstone* tombstone) noexcept;

    SRWLOCK lock_{};
    void* randomContext_ = nullptr;
    SecureOperationRandomGenerator randomGenerator_ = nullptr;
    void* clockContext_ = nullptr;
    SecureOperationClock clock_ = nullptr;
    bool hasPrincipal_ = false;
    bool hasCharacter_ = false;
    int characterId_ = -1;
    int selectedBagSlots_[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1};
    std::size_t selectionCount_ = 0;
    std::uint64_t selectionGeneration_ = 0;
    bool selectionClearCandidateActive_ = false;
    int selectionClearCandidate_[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1};
    std::size_t selectionClearCandidateCount_ = 0;
    std::size_t selectionClearStep_ = 0;
    std::uint64_t selectionClearCandidateExpiresAt_ = 0;
    bool hasPendingClearedSelection_ = false;
    int pendingClearedSelection_[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1};
    std::size_t pendingClearedSelectionCount_ = 0;
    std::uint64_t pendingClearedSelectionExpiresAt_ = 0;
    bool selectionClearInvalidated_ = false;
    bool combinePageArmed_ = false;
    std::uint32_t combineNpcId_ = 0;
    std::uint64_t combinePageGeneration_ = 0;
    int forgeEquipmentBagSlot_ = -1;
    int forgePrimaryMaterialBagSlot_ = -1;
    std::size_t forgeOddsCount_ = 0;
    SecureForgeOddsSelection
        forgeOdds_[SecureForgeOddsCapacity]{};
    std::uint8_t
        principal_[SecurePrincipalFingerprintBytes]{};
    Entry entries_[SecurePendingOperationCapacity]{};
    Tombstone tombstones_[SecureResolvedOperationCapacity]{};
};

} // namespace godswar::network
