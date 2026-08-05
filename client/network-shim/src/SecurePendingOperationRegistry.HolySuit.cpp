#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

bool TryGetHolySuitFamily(
    LegacyHolySuitAction action,
    SecureLegacyCommandFamily* family) noexcept {
    if (family == nullptr) {
        return false;
    }
    switch (action) {
        case LegacyHolySuitAction::StoreExperience:
            *family = SecureLegacyCommandFamily::
                HolySuitStoreExperience;
            return true;
        case LegacyHolySuitAction::TransferExperience:
            *family = SecureLegacyCommandFamily::
                HolySuitTransferExperience;
            return true;
        case LegacyHolySuitAction::ConsumeWare:
            *family = SecureLegacyCommandFamily::
                HolySuitConsumeWare;
            return true;
        case LegacyHolySuitAction::TransformExperience:
            *family = SecureLegacyCommandFamily::
                HolySuitTransformExperience;
            return true;
        default:
            return false;
    }
}

std::size_t CreateHolySuitIdentity(
    const LegacyHolySuitCommand& command,
    int* identity) noexcept {
    if (identity == nullptr) {
        return 0;
    }
    switch (command.action) {
        case LegacyHolySuitAction::StoreExperience:
            identity[0] = command.primaryReference;
            static_assert(
                sizeof(identity[1]) == sizeof(command.amount));
            std::memcpy(
                &identity[1],
                &command.amount,
                sizeof(command.amount));
            return 2;
        case LegacyHolySuitAction::TransferExperience:
        case LegacyHolySuitAction::ConsumeWare:
            identity[0] = command.primaryReference;
            identity[1] = command.secondaryReference;
            return 2;
        case LegacyHolySuitAction::TransformExperience:
            static_assert(
                sizeof(identity[0]) == sizeof(command.amount));
            std::memcpy(
                &identity[0],
                &command.amount,
                sizeof(command.amount));
            return 1;
        default:
            return 0;
    }
}

} // namespace

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolySuitCommand(
    const LegacyHolySuitCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::HolySuitStoreExperience;
    int identity[SecureGearSelectionCapacity]{-1, -1, -1, -1};
    const auto identityCount =
        CreateHolySuitIdentity(command, identity);
    if (descriptor == nullptr || identityCount == 0 ||
        !TryGetHolySuitFamily(command.action, &family)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

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

    // Sparta and Athens are equivalent façades for the same authoritative
    // forger operation, so a city transfer must not create a second UUID.
    Entry* entry = Find(family, 0, identity, identityCount);
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
            identity,
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
