#include "SecurePendingOperationRegistry.h"
#include "SecurePetManagerUtilityIdentity.h"

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

bool HasSettlingResultCode(
    const SecureLegacyCommandResult& result) noexcept {
    if (result.commandFamily ==
        SecureLegacyCommandFamily::PetBind) {
        return result.resultCode ==
                LegacyPetBindAlreadyBoundResultSubId ||
            result.resultCode ==
                LegacyPetBindSucceededResultSubId ||
            result.resultCode ==
                LegacyPetBindNoPetResultSubId;
    }
    if (result.commandFamily ==
        SecureLegacyCommandFamily::PetAppearanceChange) {
        return result.resultCode ==
                LegacyPetAppearanceSucceededResultSubId ||
            result.resultCode ==
                LegacyPetAppearanceMissingJadeResultSubId ||
            result.resultCode ==
                LegacyPetAppearanceIncompatibleJadeResultSubId ||
            result.resultCode ==
                LegacyPetAppearanceNoPetResultSubId ||
            result.resultCode ==
                LegacyPetAppearanceUnboundPetResultSubId;
    }
    if (result.commandFamily ==
        SecureLegacyCommandFamily::PetGrowthReset) {
        return result.resultCode ==
                LegacyPetGrowthResetNoPetResultSubId ||
            result.resultCode ==
                LegacyPetGrowthResetNoFeatherResultSubId ||
            result.resultCode ==
                LegacyPetGrowthResetNoPreviewResultSubId ||
            result.resultCode ==
                LegacyPetGrowthResetSucceededResultSubId;
    }
    if (result.commandFamily ==
            SecureLegacyCommandFamily::PetBasicSavvyReset) {
        return result.resultCode ==
                LegacyPetBasicSavvyResetLegacyNoPetResultSubId ||
            result.resultCode ==
                LegacyPetBasicSavvyResetLegacyNoFeatherResultSubId ||
            result.resultCode ==
                LegacyPetBasicSavvyResetNoFeatherResultSubId ||
            result.resultCode ==
                LegacyPetBasicSavvyResetNoPetResultSubId ||
            result.resultCode ==
                LegacyPetBasicSavvyResetNoPreviewResultSubId ||
            result.resultCode ==
                LegacyPetBasicSavvyResetSucceededResultSubId;
    }
    if (result.commandFamily ==
            SecureLegacyCommandFamily::PetManagerUtility) {
        return result.resultCode == LegacyPetGrowthCheckedResult ||
            result.resultCode == LegacyPetGrowthNoPetResult ||
            result.resultCode == LegacyPetGrowthNoTearResult ||
            result.resultCode == LegacyPetSealSucceededResult ||
            result.resultCode == LegacyPetSealNoJadeResult ||
            result.resultCode == LegacyPetSealBagFullResult ||
            result.resultCode == LegacyPetSealBoundResult ||
            result.resultCode == LegacyPetCharmBagFullResult ||
            result.resultCode == LegacyPetCharmHeldResult ||
            result.resultCode == LegacyPetCallClaimedResult ||
            result.resultCode == LegacyPetMergeClaimedResult ||
            result.resultCode == LegacyPetGenderUnboundResult ||
            result.resultCode == LegacyPetGenderNoPetResult ||
            result.resultCode == LegacyPetGenderUnavailableResult ||
            result.resultCode == LegacyPetGenderNoItemResult ||
            result.resultCode == LegacyPetGenderMaleResult ||
            result.resultCode == LegacyPetGenderFemaleResult ||
            result.resultCode == LegacyPetUnsealedResult ||
            result.resultCode == LegacyPetUnsealUnavailableResult ||
            result.resultCode == LegacyPetUnsealLinkInvalidResult ||
            result.resultCode == LegacyPetUnsealMalformedResult ||
            result.resultCode == LegacyPetUnsealConflictResult;
    }
    if (result.commandFamily !=
        SecureLegacyCommandFamily::PetSkillUnlearn) {
        return true;
    }
    return result.resultCode ==
            LegacyPetSkillUnlearnNoPetResultSubId ||
        result.resultCode ==
            LegacyPetSkillUnlearnNoPotionResultSubId ||
        result.resultCode ==
            LegacyPetSkillUnlearnEmptySlotResultSubId ||
        result.resultCode ==
            LegacyPetSkillUnlearnSucceededResultSubId;
}

bool IsUnsealResultCode(std::uint32_t resultCode) noexcept {
    return resultCode == LegacyPetUnsealedResult ||
        resultCode == LegacyPetUnsealUnavailableResult ||
        resultCode == LegacyPetUnsealLinkInvalidResult ||
        resultCode == LegacyPetUnsealMalformedResult ||
        resultCode == LegacyPetUnsealConflictResult;
}

} // namespace

SecureOperationRegistryResult
SecurePendingOperationRegistry::Resolve(
    const SecureLegacyCommandResult& result) noexcept {
    // Pet Manager families are settled only by their stock terminal
    // responses. An unknown code must leave the UUID pending so a valid
    // response or retry can still complete the operation.
    if (!HasSettlingResultCode(result)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
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
    const bool bagActivationResolvedAsUnseal =
        entry->family ==
            SecureLegacyCommandFamily::BagItemActivation &&
        result.commandFamily ==
            SecureLegacyCommandFamily::PetManagerUtility &&
        entry->capturesPetIntent &&
        IsUnsealResultCode(result.resultCode);
    if (entry->family != result.commandFamily &&
        !bagActivationResolvedAsUnseal) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::FamilyConflict;
    }
    if (bagActivationResolvedAsUnseal) {
        // Opcode 10051 cannot expose the authoritative item template to the
        // shim. The server locks the selected slot and is the only authority
        // that may promote its family-26 intent to family 55 Unseal. Store
        // that final family in the tombstone so duplicate server results are
        // idempotent while every other cross-family settlement still fails.
        entry->family = result.commandFamily;
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
