#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneCombineNavigation(
    const LegacyHolyStoneCommand& navigation,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        navigation.action != LegacyHolyStoneAction::Combine) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    if (holyStoneCombinePageGeneration_ ==
            (std::numeric_limits<std::uint64_t>::max)() ||
        now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
        ClearHolyStoneCombinePage();
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Capacity;
    }

    // Action 601 with no item references is the stock page-open request.
    // Every explicit open creates a fresh page generation and discards any
    // selections left by another NPC page.
    ++holyStoneCombinePageGeneration_;
    holyStoneCombinePageArmed_ = true;
    holyStoneCombinePostResultRearmed_ = false;
    holyStoneCombinePageNpcId_ = navigation.npcId;
    holyStoneCombinePageExpiresAt_ =
        now + SecurePendingOperationLifetimeMilliseconds;
    ClearHolyStoneUpgradePage();
    ClearHolyStoneImplementPage();
    ResetSelectionState();
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneCombineCommit(
    const LegacyHolyStoneCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    const auto result = DescribeHolyStoneCombineCommitLocked(
        command, now, descriptor);
    ReleaseSRWLockExclusive(&lock_);
    return result;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneCombineCommitLocked(
    const LegacyHolyStoneCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        command.action != LegacyHolyStoneAction::Combine ||
        command.combinationCount != SecureGearSelectionCapacity) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    if (!hasPrincipal_) {
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        return SecureOperationRegistryResult::NoCharacter;
    }
    if (!holyStoneCombinePageArmed_ ||
        holyStoneCombinePageNpcId_ != command.npcId ||
        (!hasPendingClearedSelection_ &&
         !holyStoneCombinePostResultRearmed_)) {
        return SecureOperationRegistryResult::NoSelection;
    }

    int stagedSelection[SecureGearSelectionCapacity]{
        -1, -1, -1, -1};
    std::size_t stagedSelectionCount = 0;
    if (!TryGetIdentitySelection(
            stagedSelection, &stagedSelectionCount) ||
        stagedSelectionCount != SecureGearSelectionCapacity ||
        !EqualSelection(
            stagedSelection,
            stagedSelectionCount,
            command.combinationBagSlots,
            command.combinationCount)) {
        return SecureOperationRegistryResult::NoSelection;
    }

    // Sparta and Athens expose the same durable operation. The fixed stock
    // ItemBtn1..ItemBtn4 order is the semantic identity across either city.
    Entry* entry = Find(
        SecureLegacyCommandFamily::HolyStoneCombine,
        0,
        command.combinationBagSlots,
        command.combinationCount);
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
        entry->family = SecureLegacyCommandFamily::HolyStoneCombine;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = command.combinationCount;
        std::memcpy(
            entry->bagSlots,
            command.combinationBagSlots,
            sizeof(entry->bagSlots));
        entry->expiresAt =
            now + SecurePendingOperationLifetimeMilliseconds;
    }

    entry->capturesSelectionState = true;
    entry->capturedSelectionCount = command.combinationCount;
    std::memcpy(
        entry->capturedSelectionBagSlots,
        command.combinationBagSlots,
        sizeof(entry->capturedSelectionBagSlots));
    entry->selectionGeneration = selectionGeneration_;
    entry->holyStoneCombinePageGeneration =
        holyStoneCombinePageGeneration_;

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
ClearHolyStoneCombinePage() noexcept {
    holyStoneCombinePageArmed_ = false;
    holyStoneCombinePostResultRearmed_ = false;
    holyStoneCombinePageNpcId_ = 0;
    holyStoneCombinePageExpiresAt_ = 0;
}

} // namespace godswar::network
