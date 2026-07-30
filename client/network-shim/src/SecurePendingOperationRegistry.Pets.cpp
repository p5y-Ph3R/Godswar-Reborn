#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribePetPacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    LegacyPetCommandIntent intent{};
    switch (ClassifyLegacyPetCommandPacket(
                packet,
                packetBytes,
                &intent)) {
        case LegacyPetCommandPacketKind::Command:
            return DescribePetCommand(intent, now, descriptor);
        case LegacyPetCommandPacketKind::InvalidMutation:
            return SecureOperationRegistryResult::InvalidPacket;
        case LegacyPetCommandPacketKind::Unrelated:
        default:
            return SecureOperationRegistryResult::Success;
    }
}

SecurePendingOperationRegistry::Entry*
SecurePendingOperationRegistry::FindPetCommand(
    const LegacyPetCommandIntent& intent) noexcept {
    for (auto& entry : entries_) {
        if (entry.occupied &&
            entry.capturesPetIntent &&
            entry.family == intent.family &&
            entry.characterId == characterId_ &&
            std::memcmp(
                entry.principal,
                principal_,
                sizeof(principal_)) == 0 &&
            EqualPetCommandIntent(
                intent,
                entry.petIntent)) {
            return &entry;
        }
    }
    return nullptr;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribePetCommand(
    const LegacyPetCommandIntent& intent,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr) {
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

    Entry* entry = FindPetCommand(intent);
    if (entry == nullptr) {
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::ClockFailure;
        }
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

        entry->occupied = true;
        std::memcpy(
            entry->principal,
            principal_,
            sizeof(entry->principal));
        entry->family = intent.family;
        entry->characterId = characterId_;
        entry->capturesPetIntent = true;
        entry->petIntent = intent;
        entry->expiresAt =
            now + SecurePendingOperationLifetimeMilliseconds;
    }

    descriptor->hasOperation = true;
    descriptor->operation.packetBytes = descriptor->packetBytes;
    descriptor->operation.opcode = descriptor->opcode;
    std::memcpy(
        descriptor->operation.operationId,
        entry->operationId,
        sizeof(descriptor->operation.operationId));
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

} // namespace godswar::network
