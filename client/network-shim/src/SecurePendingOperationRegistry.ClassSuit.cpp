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
        case LegacyClassSuitPacketKind::Navigation:
            *recognized = true;
            return DescribeClassSuitNavigation(
                command, now, descriptor);
        case LegacyClassSuitPacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        default:
            return SecureOperationRegistryResult::Success;
    }
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeClassSuitNavigation(
    const LegacyClassSuitCommand& navigation,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);

    if (navigation.action == LegacyClassSuitAction::InitialMenu) {
        ClearClassSuitPage();
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    const bool samePage =
        classSuitPageArmed_ &&
        classSuitPageAction_ == navigation.action &&
        classSuitPageNpcId_ == navigation.npcId;
    if (!samePage) {
        if (classSuitPageGeneration_ ==
            (std::numeric_limits<std::uint64_t>::max)()) {
            ClearClassSuitPage();
            ResetSelectionState();
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        ++classSuitPageGeneration_;
        classSuitPageArmed_ = true;
        classSuitPageAction_ = navigation.action;
        classSuitPageNpcId_ = navigation.npcId;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    // Only Delete is known to use an empty-reference final packet in the
    // shipped client. Requiring the completed ordered clear snapshot prevents
    // the page-opening navigation, a partial clear, or live stale selections
    // from becoming an authoritative command.
    int stagedSelection[SecureGearSelectionCapacity]{
        -1, -1, -1, -1};
    std::size_t stagedSelectionCount = 0;
    if (navigation.action != LegacyClassSuitAction::DeleteAttribute ||
        !hasPendingClearedSelection_ ||
        !TryGetIdentitySelection(
            stagedSelection, &stagedSelectionCount) ||
        stagedSelectionCount != 3) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    const LegacyClassSuitCommand command{
        navigation.action,
        navigation.npcId,
        stagedSelection[0],
        stagedSelection[1],
        stagedSelection[2]};
    const auto result = DescribeClassSuitCommandLocked(
        command, now, descriptor);
    ReleaseSRWLockExclusive(&lock_);
    return result;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeClassSuitCommand(
    const LegacyClassSuitCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    const auto result = DescribeClassSuitCommandLocked(
        command, now, descriptor);
    ReleaseSRWLockExclusive(&lock_);
    return result;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeClassSuitCommandLocked(
    const LegacyClassSuitCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::ClassSuitExchangeTierI;
    int identity[SecureGearSelectionCapacity]{
        command.gearReference,
        command.secondaryBagSlot,
        command.tertiaryBagSlot,
        -1};
    const std::size_t identityCount =
        command.tertiaryBagSlot >= 0
        ? 3
        : command.secondaryBagSlot >= 0 ? 2 : 1;
    if (descriptor == nullptr ||
        command.gearReference < 0 ||
        !TryGetClassSuitFamily(command.action, &family)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    int expectedSelection[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1,
        -1};
    std::size_t expectedSelectionCount = 0;
    if (command.gearReference !=
        LegacyClassSuitEquippedWeaponReference) {
        expectedSelection[expectedSelectionCount++] =
            command.gearReference;
    }
    if (command.secondaryBagSlot >= 0) {
        expectedSelection[expectedSelectionCount++] =
            command.secondaryBagSlot;
    }
    if (command.tertiaryBagSlot >= 0) {
        expectedSelection[expectedSelectionCount++] =
            command.tertiaryBagSlot;
    }

    if (!hasPrincipal_) {
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        return SecureOperationRegistryResult::NoCharacter;
    }
    int stagedSelection[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1,
        -1};
    std::size_t stagedSelectionCount = 0;
    const bool capturesSelectionState =
        TryGetIdentitySelection(
            stagedSelection,
            &stagedSelectionCount) &&
        EqualSelection(
            stagedSelection,
            stagedSelectionCount,
            expectedSelection,
            expectedSelectionCount);

    // The exact NPC endpoint is part of the durable replay intent. A retry at
    // the other city's mentor must not inherit this command's UUID.
    Entry* entry = Find(
        family, command.npcId, identity, identityCount);
    if (entry == nullptr) {
        entry = FindAvailable();
        if (entry == nullptr) {
            return SecureOperationRegistryResult::Capacity;
        }
        if (!CreateOperationId(entry->operationId)) {
            ClearEntry(entry);
            return SecureOperationRegistryResult::RandomFailure;
        }
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            ClearEntry(entry);
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
        entry->expiresAt =
            now + SecurePendingOperationLifetimeMilliseconds;
    }
    if (capturesSelectionState) {
        entry->capturesSelectionState = true;
        entry->capturedSelectionCount =
            expectedSelectionCount;
        std::memcpy(
            entry->capturedSelectionBagSlots,
            expectedSelection,
            sizeof(entry->capturedSelectionBagSlots));
        entry->selectionGeneration = selectionGeneration_;
        if (classSuitPageArmed_ &&
            classSuitPageAction_ == command.action &&
            classSuitPageNpcId_ == command.npcId) {
            entry->classSuitPageGeneration =
                classSuitPageGeneration_;
        }
    }

    descriptor->hasOperation = true;
    descriptor->operation.packetBytes = descriptor->packetBytes;
    descriptor->operation.opcode = descriptor->opcode;
    std::memcpy(
        descriptor->operation.operationId,
        entry->operationId,
        sizeof(entry->operationId));
    return SecureOperationRegistryResult::Success;
}

void SecurePendingOperationRegistry::ClearClassSuitPage() noexcept {
    classSuitPageArmed_ = false;
    classSuitPageAction_ = LegacyClassSuitAction::InitialMenu;
    classSuitPageNpcId_ = 0;
}

} // namespace godswar::network
