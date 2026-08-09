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
        case LegacyHolyStoneAction::AdvancedDrill:
            *family =
                SecureLegacyCommandFamily::HolyStoneAdvancedDrill;
            return true;
        case LegacyHolyStoneAction::MountGearDrill:
            *family = SecureLegacyCommandFamily::MountGearDrill;
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
        -1,
        -1};
    const bool hasTargetOnly =
        command.action == LegacyHolyStoneAction::Drill ||
        command.action == LegacyHolyStoneAction::MountGearDrill;
    const std::size_t identityCount = hasTargetOnly ? 1 : 2;

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

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneUpgradeNavigation(
    const LegacyHolyStoneCommand& navigation,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        navigation.action != LegacyHolyStoneAction::Upgrade) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    ClearHolyStoneImplementPage();
    ClearHolyStoneCombinePage();
    const bool samePage =
        holyStoneUpgradePageArmed_ &&
        holyStoneUpgradePageNpcId_ == navigation.npcId;
    if (!samePage) {
        if (holyStoneUpgradePageGeneration_ ==
                (std::numeric_limits<std::uint64_t>::max)() ||
            now >
                (std::numeric_limits<std::uint64_t>::max)() -
                    SecurePendingOperationLifetimeMilliseconds) {
            ClearHolyStoneUpgradePage();
            ResetSelectionState();
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        ++holyStoneUpgradePageGeneration_;
        holyStoneUpgradePageArmed_ = true;
        holyStoneUpgradePostResultRearmed_ = false;
        holyStoneUpgradePageNpcId_ = navigation.npcId;
        holyStoneUpgradePageExpiresAt_ =
            now + SecurePendingOperationLifetimeMilliseconds;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    // The initial 406 page clears its ItemBtn controls before action 401, so
    // live selections cannot distinguish its all-unset action packet from a
    // page reopen. The server-rebuilt 3100 result page uses the opposite A3
    // order: select, action 401, then clear. Resolve marks only that exact page
    // generation as post-result rearmed, allowing its current immutable
    // selection snapshot to become the next command identity.
    if (!hasPendingClearedSelection_) {
        if (holyStoneUpgradePostResultRearmed_) {
            const auto postResultCommit =
                DescribeHolyStoneUpgradeCommitLocked(
                    navigation, now, descriptor);
            if (postResultCommit !=
                SecureOperationRegistryResult::NoSelection) {
                ReleaseSRWLockExclusive(&lock_);
                return postResultCommit;
            }
        }

        // The server recreates its one-shot selection context whenever the
        // stock page-navigation packet is sent. Mirror that lifecycle here so
        // a reopened Upgrade page cannot retain bag slots that the server has
        // already discarded. Any unresolved durable entry remains keyed by
        // its UUID and exact slots, but new selections must be staged again.
        if (holyStoneUpgradePageGeneration_ ==
                (std::numeric_limits<std::uint64_t>::max)() ||
            now >
                (std::numeric_limits<std::uint64_t>::max)() -
                    SecurePendingOperationLifetimeMilliseconds) {
            ClearHolyStoneUpgradePage();
            ResetSelectionState();
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        ++holyStoneUpgradePageGeneration_;
        holyStoneUpgradePostResultRearmed_ = false;
        holyStoneUpgradePageExpiresAt_ =
            now + SecurePendingOperationLifetimeMilliseconds;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }
    const auto result = DescribeHolyStoneUpgradeCommitLocked(
        navigation, now, descriptor);
    ReleaseSRWLockExclusive(&lock_);
    return result == SecureOperationRegistryResult::NoSelection
        ? SecureOperationRegistryResult::Success
        : result;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneUpgradeCommit(
    const LegacyHolyStoneCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    const auto result = DescribeHolyStoneUpgradeCommitLocked(
        command, now, descriptor);
    ReleaseSRWLockExclusive(&lock_);
    return result;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyStoneUpgradeCommitLocked(
    const LegacyHolyStoneCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr ||
        command.action != LegacyHolyStoneAction::Upgrade) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    if (!hasPrincipal_) {
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        return SecureOperationRegistryResult::NoCharacter;
    }
    if (!holyStoneUpgradePageArmed_ ||
        holyStoneUpgradePageNpcId_ != command.npcId) {
        return SecureOperationRegistryResult::NoSelection;
    }
    if (!hasPendingClearedSelection_ &&
        !holyStoneUpgradePostResultRearmed_) {
        // Only a settled result can opt the rebuilt 3100 page into the
        // observed action-before-clear A3 sequence. The initial 406 page and
        // every explicit reopen retain their clear-before-action contract.
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

    Entry* entry = Find(
        SecureLegacyCommandFamily::HolyStoneUpgrade,
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
        entry->family = SecureLegacyCommandFamily::HolyStoneUpgrade;
        entry->characterId = characterId_;
        // The two city NPCs expose the same durable operation. The staged
        // ordered item roles, principal, and character are its identity.
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
    entry->holyStoneUpgradePageGeneration =
        holyStoneUpgradePageGeneration_;

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
ClearHolyStoneUpgradePage() noexcept {
    holyStoneUpgradePageArmed_ = false;
    holyStoneUpgradePostResultRearmed_ = false;
    holyStoneUpgradePageNpcId_ = 0;
    holyStoneUpgradePageExpiresAt_ = 0;
}

} // namespace godswar::network
