#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

bool TryGetClassSuitFamily(
    LegacyClassSuitAction action,
    SecureLegacyCommandFamily* family) noexcept {
    if (family == nullptr) {
        return false;
    }

    switch (action) {
        case LegacyClassSuitAction::ExchangeTierI:
            *family = SecureLegacyCommandFamily::
                ClassSuitExchangeTierI;
            return true;
        case LegacyClassSuitAction::AddAttribute:
            *family = SecureLegacyCommandFamily::
                ClassSuitAddAttribute;
            return true;
        case LegacyClassSuitAction::DeleteAttribute:
            *family = SecureLegacyCommandFamily::
                ClassSuitDeleteAttribute;
            return true;
        case LegacyClassSuitAction::ConvertToCommon:
            *family = SecureLegacyCommandFamily::
                ClassSuitConvertToCommon;
            return true;
        case LegacyClassSuitAction::UpgradeTierII:
            *family = SecureLegacyCommandFamily::
                ClassSuitUpgradeTierII;
            return true;
        case LegacyClassSuitAction::UpgradeTierIII:
            *family = SecureLegacyCommandFamily::
                ClassSuitUpgradeTierIII;
            return true;
        case LegacyClassSuitAction::UpgradeTierIV:
            *family = SecureLegacyCommandFamily::
                ClassSuitUpgradeTierIV;
            return true;
        default:
            return false;
    }
}

} // namespace

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeClassSuitPacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor,
    bool* recognized) noexcept {
    if (recognized == nullptr) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    *recognized = false;

    LegacyClassSuitCommand command{};
    switch (ClassifyLegacyClassSuitPacket(
                packet, packetBytes, &command)) {
        case LegacyClassSuitPacketKind::Commit:
            *recognized = true;
            return DescribeClassSuitCommand(
                command, now, descriptor);
        case LegacyClassSuitPacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        default:
            return SecureOperationRegistryResult::Success;
    }
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeClassSuitCommand(
    const LegacyClassSuitCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::ClassSuitExchangeTierI;
    int identity[SecureGearSelectionCapacity]{
        command.gearBagSlot,
        command.secondaryBagSlot,
        command.tertiaryBagSlot};
    const std::size_t identityCount =
        command.tertiaryBagSlot >= 0
        ? 3
        : command.secondaryBagSlot >= 0 ? 2 : 1;
    if (descriptor == nullptr ||
        command.gearBagSlot < 0 ||
        !TryGetClassSuitFamily(command.action, &family)) {
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

    // The exact NPC endpoint is part of the durable replay intent. A retry at
    // the other city's mentor must not inherit this command's UUID.
    Entry* entry = Find(
        family, command.npcId, identity, identityCount);
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
        entry->npcId = command.npcId;
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
