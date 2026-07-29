#pragma once

#include "SecureLegacyCommandIdentity.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecurePendingOperationCapacity = 16;
inline constexpr std::size_t SecureResolvedOperationCapacity = 16;
inline constexpr std::uint64_t
    SecurePendingOperationLifetimeMilliseconds = 10 * 60 * 1000;

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

struct SecurePendingOperationSnapshot final {
    std::size_t pending = 0;
    std::size_t resolved = 0;
    bool hasPrincipal = false;
    bool hasCharacter = false;
    int characterId = -1;
    bool hasSelection = false;
    int selectedBagSlot = -1;
    bool combinePageArmed = false;
    std::uint32_t combineNpcId = 0;
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
        int bagSlot = -1;
        std::uint64_t selectionGeneration = 0;
        std::uint64_t combinePageGeneration = 0;
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
    void Prune(std::uint64_t now) noexcept;
    Entry* Find(
        SecureLegacyCommandFamily family,
        std::uint32_t npcId,
        int bagSlot) noexcept;
    Entry* FindByOperationId(
        const std::uint8_t* operationId) noexcept;
    Tombstone* FindTombstone(
        const std::uint8_t* operationId) noexcept;
    Entry* FindAvailable() noexcept;
    Tombstone* FindTombstoneSlot() noexcept;
    bool RememberResolved(
        const Entry& entry,
        std::uint64_t now) noexcept;
    bool CreateOperationId(std::uint8_t* operationId) noexcept;
    void SetPrincipal(
        const std::uint8_t* principal) noexcept;
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
    bool hasSelection_ = false;
    int selectedBagSlot_ = -1;
    std::uint64_t selectionGeneration_ = 0;
    bool combinePageArmed_ = false;
    std::uint32_t combineNpcId_ = 0;
    std::uint64_t combinePageGeneration_ = 0;
    std::uint8_t
        principal_[SecurePrincipalFingerprintBytes]{};
    Entry entries_[SecurePendingOperationCapacity]{};
    Tombstone tombstones_[SecureResolvedOperationCapacity]{};
};

} // namespace godswar::network
