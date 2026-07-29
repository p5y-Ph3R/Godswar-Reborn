#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

bool TryGetHolyStoneFamily(
    LegacyHolyStoneAction action,
    SecureLegacyCommandFamily* family) noexcept {
    if (family == nullptr) {
        return false;
    }
    switch (action) {
        case LegacyHolyStoneAction::Mount:
            *family = SecureLegacyCommandFamily::HolyStoneMount;
            return true;
        case LegacyHolyStoneAction::Remove:
            *family = SecureLegacyCommandFamily::HolyStoneRemove;
            return true;
        case LegacyHolyStoneAction::Drill:
            *family = SecureLegacyCommandFamily::HolyStoneDrill;
            return true;
        default:
            return false;
    }
}

} // namespace

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneCommand(
    const LegacyHolyStoneCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::HolyStoneMount;
    if (descriptor == nullptr ||
        !TryGetHolyStoneFamily(command.action, &family)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    int identitySlots[SecureGearSelectionCapacity]{
        command.targetReference,
        command.secondaryValue,
        -1};
    const std::size_t identityCount =
        command.action == LegacyHolyStoneAction::Drill ? 1 : 2;

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

    // The NPC identity is deliberately canonicalized to zero. Sparta and
    // Athens expose the same authoritative operation, so retrying after a
    // city transfer must retain the original operation UUID.
    Entry* entry = Find(family, 0, identitySlots, identityCount);
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
        entry->family = family;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = identityCount;
        std::memcpy(
            entry->bagSlots,
            identitySlots,
            sizeof(entry->bagSlots));
        entry->capturesSelectionState = false;
        entry->expiresAt =
            now + SecurePendingOperationLifetimeMilliseconds;
    }

    descriptor->hasOperation = true;
    descriptor->operation.packetBytes = descriptor->packetBytes;
    descriptor->operation.opcode = descriptor->opcode;
    std::memcpy(
        descriptor->operation.operationId,
        entry->operationId,
        sizeof(entry->operationId));
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

} // namespace godswar::network
