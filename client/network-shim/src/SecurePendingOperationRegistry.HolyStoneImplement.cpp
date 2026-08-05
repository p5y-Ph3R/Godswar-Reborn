#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneImplementNavigation(
    const LegacyHolyStoneCommand& navigation,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        navigation.action != LegacyHolyStoneAction::ImplementSpirit) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    ClearHolyStoneUpgradePage();
    ClearHolyStoneCombinePage();
    const bool samePage =
        holyStoneImplementPageArmed_ &&
        holyStoneImplementPageNpcId_ == navigation.npcId;
    if (!samePage) {
        if (holyStoneImplementPageGeneration_ ==
                (std::numeric_limits<std::uint64_t>::max)() ||
            now >
                (std::numeric_limits<std::uint64_t>::max)() -
                    SecurePendingOperationLifetimeMilliseconds) {
            ClearHolyStoneImplementPage();
            ResetSelectionState();
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        ++holyStoneImplementPageGeneration_;
        holyStoneImplementPageArmed_ = true;
        holyStoneImplementPostResultRearmed_ = false;
        holyStoneImplementPageNpcId_ = navigation.npcId;
        holyStoneImplementPageExpiresAt_ =
            now + SecurePendingOperationLifetimeMilliseconds;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    // Match the stock Upgrade lifecycle. The initial page clears its fixed
    // ItemBtn controls before action 501. A settled result page may send the
    // next action before clearing, but only Resolve can authorize that order.
    if (!hasPendingClearedSelection_) {
        if (holyStoneImplementPostResultRearmed_) {
            const auto postResultCommit =
                DescribeHolyStoneImplementCommitLocked(
                    navigation, now, descriptor);
            if (postResultCommit !=
                SecureOperationRegistryResult::NoSelection) {
                ReleaseSRWLockExclusive(&lock_);
                return postResultCommit;
            }
        }

        if (holyStoneImplementPageGeneration_ ==
                (std::numeric_limits<std::uint64_t>::max)() ||
            now >
                (std::numeric_limits<std::uint64_t>::max)() -
                    SecurePendingOperationLifetimeMilliseconds) {
            ClearHolyStoneImplementPage();
            ResetSelectionState();
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        ++holyStoneImplementPageGeneration_;
        holyStoneImplementPostResultRearmed_ = false;
        holyStoneImplementPageExpiresAt_ =
            now + SecurePendingOperationLifetimeMilliseconds;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    const auto result = DescribeHolyStoneImplementCommitLocked(
        navigation, now, descriptor);
    ReleaseSRWLockExclusive(&lock_);
    return result == SecureOperationRegistryResult::NoSelection
        ? SecureOperationRegistryResult::Success
        : result;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneImplementCommit(
    const LegacyHolyStoneCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    const auto result = DescribeHolyStoneImplementCommitLocked(
        command, now, descriptor);
    ReleaseSRWLockExclusive(&lock_);
    return result;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneImplementCommitLocked(
    const LegacyHolyStoneCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        command.action != LegacyHolyStoneAction::ImplementSpirit) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    if (!hasPrincipal_) {
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        return SecureOperationRegistryResult::NoCharacter;
    }
    if (!holyStoneImplementPageArmed_ ||
        holyStoneImplementPageNpcId_ != command.npcId) {
        return SecureOperationRegistryResult::NoSelection;
    }
    if (!hasPendingClearedSelection_ &&
        !holyStoneImplementPostResultRearmed_) {
        return SecureOperationRegistryResult::NoSelection;
    }

    int stagedSelection[SecureGearSelectionCapacity]{
        -1, -1, -1, -1};
    std::size_t stagedSelectionCount = 0;
    if (!TryGetIdentitySelection(
            stagedSelection, &stagedSelectionCount) ||
        (stagedSelectionCount != 2 &&
         stagedSelectionCount != 3)) {
        return SecureOperationRegistryResult::NoSelection;
    }

    // Fixed semantic order: target Holy Stone, Holy Spirit, then optional
    // Goddess Stone. Both city NPCs expose the same durable operation.
    Entry* entry = Find(
        SecureLegacyCommandFamily::HolyStoneImplementSpirit,
        0,
        stagedSelection,
        stagedSelectionCount);
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
        entry->family =
            SecureLegacyCommandFamily::HolyStoneImplementSpirit;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = stagedSelectionCount;
        std::memcpy(
            entry->bagSlots,
            stagedSelection,
            sizeof(entry->bagSlots));
        entry->expiresAt =
            now + SecurePendingOperationLifetimeMilliseconds;
    }

    entry->capturesSelectionState = true;
    entry->capturedSelectionCount = stagedSelectionCount;
    std::memcpy(
        entry->capturedSelectionBagSlots,
        stagedSelection,
        sizeof(entry->capturedSelectionBagSlots));
    entry->selectionGeneration = selectionGeneration_;
    entry->holyStoneImplementPageGeneration =
        holyStoneImplementPageGeneration_;

    descriptor->hasOperation = true;
    descriptor->operation.packetBytes = descriptor->packetBytes;
    descriptor->operation.opcode = descriptor->opcode;
    std::memcpy(
        descriptor->operation.operationId,
        entry->operationId,
        sizeof(entry->operationId));
    return SecureOperationRegistryResult::Success;
}

void SecurePendingOperationRegistry::
ClearHolyStoneImplementPage() noexcept {
    holyStoneImplementPageArmed_ = false;
    holyStoneImplementPostResultRearmed_ = false;
    holyStoneImplementPageNpcId_ = 0;
    holyStoneImplementPageExpiresAt_ = 0;
}

} // namespace godswar::network
