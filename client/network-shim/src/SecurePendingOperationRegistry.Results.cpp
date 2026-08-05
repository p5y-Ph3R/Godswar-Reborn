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
SecurePendingOperationRegistry::Resolve(
    const SecureLegacyCommandResult& result) noexcept {
    std::uint64_t now = 0;
    if (!ReadNow(&now)) {
        return SecureOperationRegistryResult::ClockFailure;
    }
    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    Entry* entry = FindByOperationId(result.operationId);
    if (entry == nullptr) {
        Tombstone* tombstone =
            FindTombstone(result.operationId);
        if (tombstone != nullptr) {
            const bool familyMatches =
                tombstone->family == result.commandFamily;
            ReleaseSRWLockExclusive(&lock_);
            return familyMatches
                ? SecureOperationRegistryResult::Success
                : SecureOperationRegistryResult::
                    FamilyConflict;
        }
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::UnknownOperation;
    }
    if (entry->family != result.commandFamily) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::FamilyConflict;
    }

    if (!RememberResolved(*entry, now)) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::ClockFailure;
    }
    if (entry->family ==
        SecureLegacyCommandFamily::EquipmentForge) {
        if (hasPrincipal_ &&
            hasCharacter_ &&
            characterId_ == entry->characterId &&
            EqualBytes(
                principal_,
                entry->principal,
                sizeof(principal_)) &&
            ForgeStateMatches(*entry)) {
            ResetForgeState();
        }
        ClearEntry(entry);
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }
    int identityBagSlots[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1,
        -1};
    std::size_t identitySelectionCount = 0;
    if (entry->capturesSelectionState &&
        hasPrincipal_ &&
        hasCharacter_ &&
        characterId_ == entry->characterId &&
        selectionGeneration_ ==
            entry->selectionGeneration &&
        TryGetIdentitySelection(
            identityBagSlots,
            &identitySelectionCount) &&
        EqualSelection(
            identityBagSlots,
            identitySelectionCount,
            entry->capturedSelectionBagSlots,
            entry->capturedSelectionCount) &&
        EqualBytes(
            principal_,
            entry->principal,
            sizeof(principal_))) {
        ResetSelectionState();
    }
    if (entry->family ==
            SecureLegacyCommandFamily::CombineGemPieces &&
        combinePageArmed_ &&
        combineNpcId_ == entry->npcId &&
        combinePageGeneration_ ==
            entry->combinePageGeneration) {
        combinePageArmed_ = false;
        combineNpcId_ = 0;
    }
    if (entry->classSuitPageGeneration != 0 &&
        classSuitPageArmed_ &&
        classSuitPageNpcId_ == entry->npcId &&
        classSuitPageGeneration_ ==
            entry->classSuitPageGeneration) {
        ClearClassSuitPage();
    }
    if (entry->holyStoneUpgradePageGeneration != 0 &&
        holyStoneUpgradePageArmed_ &&
        holyStoneUpgradePageGeneration_ ==
            entry->holyStoneUpgradePageGeneration) {
        // The server's native [3100, result] response rebuilds the Upgrade
        // controls in place. The stock client does not send another action-401
        // navigation packet for that rebuilt page, so keep the page armed
        // after settling the one-shot operation. This result-bound flag is
        // also the sole authority for the rebuilt page's observed A3 order:
        // select, action 401, then clear. ResetSelectionState above has
        // already discarded the consumed slots; refreshing the deadline lets
        // the next exact selection receive its own operation UUID.
        holyStoneUpgradePostResultRearmed_ = true;
        if (now <=
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            holyStoneUpgradePageExpiresAt_ =
                now + SecurePendingOperationLifetimeMilliseconds;
        } else {
            ClearHolyStoneUpgradePage();
        }
    }
    if (entry->holyStoneImplementPageGeneration != 0 &&
        holyStoneImplementPageArmed_ &&
        holyStoneImplementPageGeneration_ ==
            entry->holyStoneImplementPageGeneration) {
        // The native [3200, result] page rebuilds the fixed implementation
        // slots. As with Upgrade, only a settled result may authorize the
        // page's next action-before-clear sequence.
        holyStoneImplementPostResultRearmed_ = true;
        if (now <=
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            holyStoneImplementPageExpiresAt_ =
                now + SecurePendingOperationLifetimeMilliseconds;
        } else {
            ClearHolyStoneImplementPage();
        }
    }
    if (entry->holyStoneCombinePageGeneration != 0 &&
        holyStoneCombinePageArmed_ &&
        holyStoneCombinePageGeneration_ ==
            entry->holyStoneCombinePageGeneration) {
        // Response page 3300 rebuilds the four stock slots in place. Its A3
        // handler sends action 601 before clearing the controls, so only a
        // settled result may enable that action-before-clear sequence.
        holyStoneCombinePostResultRearmed_ = true;
        if (now <=
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            holyStoneCombinePageExpiresAt_ =
                now + SecurePendingOperationLifetimeMilliseconds;
        } else {
            ClearHolyStoneCombinePage();
        }
    }
    ClearEntry(entry);
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

} // namespace godswar::network
