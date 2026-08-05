#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeEquipmentBagTransfer(
    int equipmentSlot,
    int bagSlot,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    const int maximumBagSlot = static_cast<int>(
        LegacyKitBagPageCount * LegacyKitBagSlotsPerPage);
    if (descriptor == nullptr ||
        equipmentSlot < 0 ||
        equipmentSlot >
            static_cast<int>(LegacyEquipmentSlotMaximum) ||
        bagSlot < 0 ||
        bagSlot >= maximumBagSlot) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    const int identitySlots[SecureGearSelectionCapacity]{
        equipmentSlot,
        bagSlot,
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
        SecureLegacyCommandFamily::EquipmentBagTransfer,
        0,
        identitySlots,
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
            SecureLegacyCommandFamily::EquipmentBagTransfer;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = 2;
        std::memcpy(
            entry->bagSlots,
            identitySlots,
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
