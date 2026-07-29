#include "SecurePendingOperationRegistry.h"

#include <Windows.h>

#include <algorithm>
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

bool SecurePendingOperationRegistry::ReadNow(
    std::uint64_t* now) noexcept {
    return now != nullptr &&
        clock_ != nullptr &&
        clock_(clockContext_, now);
}

void SecurePendingOperationRegistry::Prune(
    std::uint64_t now) noexcept {
    if (hasPendingClearedSelection_ &&
        now >= pendingClearedSelectionExpiresAt_) {
        hasPendingClearedSelection_ = false;
        pendingClearedSelectionCount_ = 0;
        pendingClearedSelectionExpiresAt_ = 0;
        std::fill_n(
            pendingClearedSelection_,
            SecureGearSelectionCapacity,
            -1);
    }
    if (selectionClearCandidateActive_ &&
        now >= selectionClearCandidateExpiresAt_) {
        InvalidateSelectionClear();
    }
    for (auto& entry : entries_) {
        if (entry.occupied && now >= entry.expiresAt) {
            ClearEntry(&entry);
        }
    }
    for (auto& tombstone : tombstones_) {
        if (tombstone.occupied &&
            now >= tombstone.expiresAt) {
            ClearTombstone(&tombstone);
        }
    }
}

SecurePendingOperationRegistry::Entry*
SecurePendingOperationRegistry::Find(
    SecureLegacyCommandFamily family,
    std::uint32_t npcId,
    const int* bagSlots,
    std::size_t selectionCount) noexcept {
    for (auto& entry : entries_) {
        if (entry.occupied &&
            entry.characterId == characterId_ &&
            entry.npcId == npcId &&
            entry.family == family &&
            EqualSelection(
                entry.bagSlots,
                entry.selectionCount,
                bagSlots,
                selectionCount) &&
            EqualBytes(
                entry.principal,
                principal_,
                sizeof(principal_))) {
            return &entry;
        }
    }
    return nullptr;
}

SecurePendingOperationRegistry::Entry*
SecurePendingOperationRegistry::FindByOperationId(
    const std::uint8_t* operationId) noexcept {
    for (auto& entry : entries_) {
        if (entry.occupied &&
            EqualBytes(
                entry.operationId,
                operationId,
                sizeof(entry.operationId))) {
            return &entry;
        }
    }
    return nullptr;
}

SecurePendingOperationRegistry::Tombstone*
SecurePendingOperationRegistry::FindTombstone(
    const std::uint8_t* operationId) noexcept {
    for (auto& tombstone : tombstones_) {
        if (tombstone.occupied &&
            EqualBytes(
                tombstone.operationId,
                operationId,
                sizeof(tombstone.operationId))) {
            return &tombstone;
        }
    }
    return nullptr;
}

SecurePendingOperationRegistry::Entry*
SecurePendingOperationRegistry::FindAvailable() noexcept {
    for (auto& entry : entries_) {
        if (!entry.occupied) {
            return &entry;
        }
    }
    return nullptr;
}

SecurePendingOperationRegistry::Tombstone*
SecurePendingOperationRegistry::FindTombstoneSlot() noexcept {
    Tombstone* oldest = nullptr;
    for (auto& tombstone : tombstones_) {
        if (!tombstone.occupied) {
            return &tombstone;
        }
        if (oldest == nullptr ||
            tombstone.expiresAt < oldest->expiresAt) {
            oldest = &tombstone;
        }
    }
    return oldest;
}

bool SecurePendingOperationRegistry::RememberResolved(
    const Entry& entry,
    std::uint64_t now) noexcept {
    if (now >
        (std::numeric_limits<std::uint64_t>::max)() -
            SecurePendingOperationLifetimeMilliseconds) {
        return false;
    }
    Tombstone* tombstone = FindTombstoneSlot();
    if (tombstone == nullptr) {
        return false;
    }

    ClearTombstone(tombstone);
    tombstone->occupied = true;
    tombstone->family = entry.family;
    tombstone->expiresAt =
        now + SecurePendingOperationLifetimeMilliseconds;
    std::memcpy(
        tombstone->operationId,
        entry.operationId,
        sizeof(tombstone->operationId));
    return true;
}

bool SecurePendingOperationRegistry::CreateOperationId(
    std::uint8_t* operationId) noexcept {
    if (operationId == nullptr ||
        randomGenerator_ == nullptr) {
        return false;
    }

    for (unsigned attempt = 0; attempt < 4; ++attempt) {
        if (!randomGenerator_(
                randomContext_,
                operationId,
                16)) {
            return false;
        }
        operationId[6] = static_cast<std::uint8_t>(
            (operationId[6] & 0x0FU) | 0x40U);
        operationId[8] = static_cast<std::uint8_t>(
            (operationId[8] & 0x3FU) | 0x80U);
        if (FindByOperationId(operationId) == nullptr &&
            FindTombstone(operationId) == nullptr) {
            return true;
        }
    }
    SecureZeroMemory(operationId, 16);
    return false;
}

void SecurePendingOperationRegistry::SetPrincipal(
    const std::uint8_t* principal) noexcept {
    const bool changed =
        !hasPrincipal_ ||
        !EqualBytes(
            principal_,
            principal,
            sizeof(principal_));
    std::memcpy(
        principal_,
        principal,
        sizeof(principal_));
    hasPrincipal_ = true;
    if (changed) {
        hasCharacter_ = false;
        characterId_ = -1;
        ResetSelectionState();
        combinePageArmed_ = false;
        combineNpcId_ = 0;
    }
}

bool SecurePendingOperationRegistry::AddSelection(
    int bagSlot) noexcept {
    for (const int selected : selectedBagSlots_) {
        if (selected == bagSlot) {
            return false;
        }
    }
    if (selectionCount_ >= SecureGearSelectionCapacity) {
        return false;
    }

    BeginSelectionEdit();
    for (auto& selected : selectedBagSlots_) {
        if (selected < 0) {
            selected = bagSlot;
            ++selectionCount_;
            return true;
        }
    }
    return false;
}

void SecurePendingOperationRegistry::RemoveSelection(
    int bagSlot,
    std::uint64_t now) noexcept {
    TrackSelectionClear(bagSlot, now);
    for (auto& selected : selectedBagSlots_) {
        if (selected == bagSlot) {
            selected = -1;
            if (selectionCount_ > 0) {
                --selectionCount_;
            }
            return;
        }
    }
}

void SecurePendingOperationRegistry::BeginSelectionEdit() noexcept {
    ResetSelectionClearCandidate();
    hasPendingClearedSelection_ = false;
    pendingClearedSelectionCount_ = 0;
    pendingClearedSelectionExpiresAt_ = 0;
    std::fill_n(
        pendingClearedSelection_,
        SecureGearSelectionCapacity,
        -1);
    selectionClearInvalidated_ = false;
}

void SecurePendingOperationRegistry::TrackSelectionClear(
    int bagSlot,
    std::uint64_t now) noexcept {
    if (hasPendingClearedSelection_) {
        hasPendingClearedSelection_ = false;
        pendingClearedSelectionCount_ = 0;
        pendingClearedSelectionExpiresAt_ = 0;
        std::fill_n(
            pendingClearedSelection_,
            SecureGearSelectionCapacity,
            -1);
    }
    if (selectionClearInvalidated_) {
        return;
    }

    if (!selectionClearCandidateActive_) {
        int current[SecureGearSelectionCapacity]{
            -1,
            -1,
            -1};
        std::size_t count = 0;
        for (const int selected : selectedBagSlots_) {
            if (selected >= 0) {
                current[count++] = selected;
            }
        }
        if (count == 0) {
            ResetSelectionClearCandidate();
            return;
        }
        if (current[0] != bagSlot) {
            InvalidateSelectionClear();
            return;
        }
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecureSelectionClearCorrelationLifetimeMilliseconds) {
            InvalidateSelectionClear();
            return;
        }

        std::memcpy(
            selectionClearCandidate_,
            current,
            sizeof(selectionClearCandidate_));
        selectionClearCandidateCount_ = count;
        selectionClearStep_ = 1;
        selectionClearCandidateExpiresAt_ =
            now +
            SecureSelectionClearCorrelationLifetimeMilliseconds;
        selectionClearCandidateActive_ = true;
    } else {
        if (selectionClearStep_ >=
                selectionClearCandidateCount_ ||
            selectionClearCandidate_[selectionClearStep_] !=
                bagSlot) {
            InvalidateSelectionClear();
            return;
        }
        ++selectionClearStep_;
    }

    if (selectionClearStep_ ==
        selectionClearCandidateCount_) {
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecureSelectionClearCorrelationLifetimeMilliseconds) {
            InvalidateSelectionClear();
            return;
        }
        std::memcpy(
            pendingClearedSelection_,
            selectionClearCandidate_,
            sizeof(pendingClearedSelection_));
        pendingClearedSelectionCount_ =
            selectionClearCandidateCount_;
        pendingClearedSelectionExpiresAt_ =
            now +
            SecureSelectionClearCorrelationLifetimeMilliseconds;
        hasPendingClearedSelection_ = true;
        ResetSelectionClearCandidate();
    }
}

void SecurePendingOperationRegistry::
ResetSelectionClearCandidate() noexcept {
    selectionClearCandidateActive_ = false;
    selectionClearCandidateCount_ = 0;
    selectionClearStep_ = 0;
    selectionClearCandidateExpiresAt_ = 0;
    std::fill_n(
        selectionClearCandidate_,
        SecureGearSelectionCapacity,
        -1);
}

void SecurePendingOperationRegistry::
InvalidateSelectionClear() noexcept {
    ResetSelectionClearCandidate();
    selectionClearInvalidated_ = true;
}

void SecurePendingOperationRegistry::ResetSelectionState() noexcept {
    std::fill_n(
        selectedBagSlots_,
        SecureGearSelectionCapacity,
        -1);
    selectionCount_ = 0;
    ResetSelectionClearCandidate();
    hasPendingClearedSelection_ = false;
    pendingClearedSelectionCount_ = 0;
    pendingClearedSelectionExpiresAt_ = 0;
    std::fill_n(
        pendingClearedSelection_,
        SecureGearSelectionCapacity,
        -1);
    selectionClearInvalidated_ = false;
}

bool SecurePendingOperationRegistry::TryGetIdentitySelection(
    int* bagSlots,
    std::size_t* selectionCount) const noexcept {
    if (bagSlots == nullptr || selectionCount == nullptr) {
        return false;
    }
    std::fill_n(
        bagSlots,
        SecureGearSelectionCapacity,
        -1);
    *selectionCount = 0;

    if (hasPendingClearedSelection_) {
        std::memcpy(
            bagSlots,
            pendingClearedSelection_,
            sizeof(pendingClearedSelection_));
        *selectionCount = pendingClearedSelectionCount_;
        return *selectionCount > 0;
    }
    if (selectionClearInvalidated_ ||
        selectionClearCandidateActive_) {
        return false;
    }

    for (const int selected : selectedBagSlots_) {
        if (selected >= 0) {
            bagSlots[(*selectionCount)++] = selected;
        }
    }
    return *selectionCount > 0;
}

bool SecurePendingOperationRegistry::EqualSelection(
    const int* first,
    std::size_t firstCount,
    const int* second,
    std::size_t secondCount) noexcept {
    return first != nullptr &&
        second != nullptr &&
        firstCount == secondCount &&
        firstCount <= SecureGearSelectionCapacity &&
        std::equal(
            first,
            first + firstCount,
            second);
}

void SecurePendingOperationRegistry::ClearEntry(
    Entry* entry) noexcept {
    if (entry != nullptr) {
        SecureZeroMemory(entry, sizeof(*entry));
        std::fill_n(
            entry->bagSlots,
            SecureGearSelectionCapacity,
            -1);
    }
}

void SecurePendingOperationRegistry::ClearTombstone(
    Tombstone* tombstone) noexcept {
    if (tombstone != nullptr) {
        SecureZeroMemory(tombstone, sizeof(*tombstone));
    }
}

} // namespace godswar::network
