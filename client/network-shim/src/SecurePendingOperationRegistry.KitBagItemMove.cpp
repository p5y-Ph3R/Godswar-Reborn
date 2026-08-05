#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeKitBagItemMove(
    int sourceBagSlot,
    int destinationBagSlot,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    const int maximumBagSlot = static_cast<int>(
        LegacyKitBagPageCount * LegacyKitBagSlotsPerPage);
    if (descriptor == nullptr ||
        sourceBagSlot < 0 ||
        sourceBagSlot >= maximumBagSlot ||
        destinationBagSlot < 0 ||
        destinationBagSlot >= maximumBagSlot ||
        sourceBagSlot == destinationBagSlot) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    const int identityBagSlots[SecureGearSelectionCapacity]{
        sourceBagSlot,
        destinationBagSlot,
        -1,
        -1};

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    if (!hasPrincipal_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoCharacter;
    }

    Entry* entry = Find(
        SecureLegacyCommandFamily::KitBagItemMove,
        0,
        identityBagSlots,
        2);
    if (entry == nullptr) {
        entry = FindAvailable();
        if (entry == nullptr) {
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        if (!CreateOperationId(entry->operationId)) {
            ClearEntry(entry);
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::RandomFailure;
        }
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            ClearEntry(entry);
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::ClockFailure;
        }

        entry->occupied = true;
        std::memcpy(
            entry->principal,
            principal_,
            sizeof(entry->principal));
        entry->family =
            SecureLegacyCommandFamily::KitBagItemMove;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = 2;
        std::memcpy(
            entry->bagSlots,
            identityBagSlots,
            sizeof(entry->bagSlots));
        entry->capturesSelectionState = false;
        entry->expiresAt =
            now + SecurePendingOperationLifetimeMilliseconds;
    }

    descriptor->hasOperation = true;
    descriptor->operation.packetBytes =
        descriptor->packetBytes;
    descriptor->operation.opcode = descriptor->opcode;
    std::memcpy(
        descriptor->operation.operationId,
        entry->operationId,
        sizeof(entry->operationId));
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

} // namespace godswar::network
