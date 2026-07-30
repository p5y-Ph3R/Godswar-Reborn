#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

bool EqualBytes(
    const std::uint8_t* first,
    const std::uint8_t* second,
    std::size_t bytes) noexcept {
    return std::memcmp(first, second, bytes) == 0;
}

} // namespace

SecureOperationRegistryResult
SecurePendingOperationRegistry::
DescribeCharacterLifecyclePacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    LegacyCharacterLifecycleIntent intent{};
    switch (ClassifyLegacyCharacterLifecyclePacket(
                packet,
                packetBytes,
                &intent)) {
        case LegacyCharacterLifecyclePacketKind::Command:
            return DescribeCharacterLifecycle(
                intent,
                now,
                descriptor);
        case LegacyCharacterLifecyclePacketKind::InvalidMutation:
            return SecureOperationRegistryResult::InvalidPacket;
        case LegacyCharacterLifecyclePacketKind::Unrelated:
        default:
            return SecureOperationRegistryResult::Success;
    }
}

SecurePendingOperationRegistry::Entry*
SecurePendingOperationRegistry::FindCharacterLifecycle(
    const LegacyCharacterLifecycleIntent& intent) noexcept {
    for (auto& entry : entries_) {
        if (entry.occupied &&
            entry.capturesLifecycleIntent &&
            entry.family == intent.family &&
            EqualBytes(
                entry.principal,
                principal_,
                sizeof(principal_)) &&
            EqualBytes(
                entry.lifecycleIntent,
                intent.bytes,
                sizeof(entry.lifecycleIntent))) {
            return &entry;
        }
    }
    return nullptr;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeCharacterLifecycle(
    const LegacyCharacterLifecycleIntent& intent,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        (intent.family !=
                SecureLegacyCommandFamily::CharacterCreate &&
            intent.family !=
                SecureLegacyCommandFamily::CharacterDelete)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    if (!hasPrincipal_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoPrincipal;
    }

    Entry* entry = FindCharacterLifecycle(intent);
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
        entry->characterId =
            hasCharacter_ ? characterId_ : -1;
        entry->capturesLifecycleIntent = true;
        std::memcpy(
            entry->lifecycleIntent,
            intent.bytes,
            sizeof(entry->lifecycleIntent));
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
        sizeof(descriptor->operation.operationId));
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

} // namespace godswar::network
