#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeZodiacPacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor,
    bool* recognized) noexcept {
    if (descriptor == nullptr || recognized == nullptr) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    *recognized = false;

    LegacyZodiacSkillGridUpgradeCommand upgrade{};
    switch (ClassifyLegacyZodiacSkillGridUpgradePacket(
                packet,
                packetBytes,
                &upgrade)) {
        case LegacyZodiacSkillGridUpgradePacketKind::Commit:
            *recognized = true;
            return DescribeZodiacSkillGridUpgrade(
                upgrade,
                now,
                descriptor);
        case LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        case LegacyZodiacSkillGridUpgradePacketKind::Unrelated:
        default:
            break;
    }

    LegacyZodiacSkillGridSelectionCommand selection{};
    switch (ClassifyLegacyZodiacSkillGridSelectionPacket(
                packet,
                packetBytes,
                &selection)) {
        case LegacyZodiacSkillGridSelectionPacketKind::Commit:
            *recognized = true;
            return DescribeZodiacSkillGridSelection(
                selection,
                now,
                descriptor);
        case LegacyZodiacSkillGridSelectionPacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        case LegacyZodiacSkillGridSelectionPacketKind::Unrelated:
        default:
            return SecureOperationRegistryResult::Success;
    }
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeZodiacSkillGridUpgrade(
    const LegacyZodiacSkillGridUpgradeCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        command.gridIndex < LegacyZodiacSkillGridMinimum ||
        command.gridIndex > LegacyZodiacSkillGridMaximum) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    const int identity[SecureGearSelectionCapacity]{
        command.gridIndex,
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
        SecureLegacyCommandFamily::ZodiacSkillGridUpgrade,
        0,
        identity,
        1);
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
            SecureLegacyCommandFamily::ZodiacSkillGridUpgrade;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = 1;
        std::memcpy(
            entry->bagSlots,
            identity,
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

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeZodiacSkillGridSelection(
    const LegacyZodiacSkillGridSelectionCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        command.gridIndex < LegacyZodiacSkillGridMinimum ||
        command.gridIndex > LegacyZodiacSkillGridMaximum) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    const int identity[SecureGearSelectionCapacity]{
        command.gridIndex,
        command.selectedSkillKind,
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
        SecureLegacyCommandFamily::ZodiacSkillGridSelection,
        0,
        identity,
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
            SecureLegacyCommandFamily::ZodiacSkillGridSelection;
        entry->characterId = characterId_;
        entry->selectionCount = 2;
        std::memcpy(
            entry->bagSlots,
            identity,
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
