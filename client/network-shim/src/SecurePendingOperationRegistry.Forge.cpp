#include "SecurePendingOperationRegistry.h"

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

bool EqualForgeOdds(
    const SecureForgeOddsSelection* first,
    const SecureForgeOddsSelection* second,
    std::size_t count) noexcept {
    if (first == nullptr || second == nullptr) {
        return false;
    }
    for (std::size_t index = 0; index < count; ++index) {
        if (first[index].bagSlot != second[index].bagSlot ||
            first[index].quantity != second[index].quantity) {
            return false;
        }
    }
    return true;
}

} // namespace

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeForgePacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint16_t opcode,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr || !IsLegacyForgeOpcode(opcode)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    if (opcode == LegacyForgeSelectionOpcode) {
        LegacyForgeSelection selection{};
        if (!TryReadLegacyForgeSelection(
                packet,
                packetBytes,
                &selection)) {
            return SecureOperationRegistryResult::InvalidPacket;
        }

        AcquireSRWLockExclusive(&lock_);
        Prune(now);
        if (selection.mode != LegacyOrdinaryForgeMode) {
            ResetForgeState();
        } else {
            static_cast<void>(StageForgeSelection(selection));
        }
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    if (opcode == LegacyForgeCancelOpcode) {
        if (!TryReadLegacyForgeCancel(packet, packetBytes)) {
            return SecureOperationRegistryResult::InvalidPacket;
        }
        AcquireSRWLockExclusive(&lock_);
        Prune(now);
        ResetForgeState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    if (opcode == LegacyForgeReplacementSelectionOpcode ||
        opcode == LegacyForgeReplacementActionOpcode) {
        if (!TryReadLegacyForgeReplacement(
                packet,
                packetBytes,
                opcode)) {
            return SecureOperationRegistryResult::InvalidPacket;
        }
        AcquireSRWLockExclusive(&lock_);
        Prune(now);
        ResetForgeState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    std::uint32_t mode = 0;
    if (opcode != LegacyForgeStartOpcode ||
        !TryReadLegacyForgeStart(
            packet,
            packetBytes,
            &mode)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    if (mode != LegacyOrdinaryForgeMode) {
        ResetForgeState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }
    if (!hasPrincipal_) {
        ResetForgeState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        ResetForgeState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoCharacter;
    }

    SecureForgeOddsSelection odds[SecureForgeOddsCapacity]{};
    std::size_t oddsCount = 0;
    if (!TryCaptureForgeIdentity(odds, &oddsCount)) {
        ResetForgeState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoSelection;
    }

    Entry* entry = FindForge();
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
            SecureLegacyCommandFamily::EquipmentForge;
        entry->characterId = characterId_;
        entry->capturesForgeState = true;
        entry->forgeEquipmentBagSlot =
            forgeEquipmentBagSlot_;
        entry->forgePrimaryMaterialBagSlot =
            forgePrimaryMaterialBagSlot_;
        entry->forgeOddsCount = oddsCount;
        std::memcpy(
            entry->forgeOdds,
            odds,
            sizeof(entry->forgeOdds));
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

bool SecurePendingOperationRegistry::StageForgeSelection(
    const LegacyForgeSelection& selection) noexcept {
    if (selection.bagSlot < 0 ||
        selection.bagSlot >=
            LegacyForgePageCount * LegacyForgeSlotsPerPage) {
        return false;
    }

    std::size_t oddsIndex = 0;
    while (oddsIndex < forgeOddsCount_ &&
           forgeOdds_[oddsIndex].bagSlot <
               selection.bagSlot) {
        ++oddsIndex;
    }
    const bool hasOdds =
        oddsIndex < forgeOddsCount_ &&
        forgeOdds_[oddsIndex].bagSlot ==
            selection.bagSlot;

    if (selection.destination ==
        LegacyForgeEquipmentDestination) {
        if (selection.bagSlot ==
                forgePrimaryMaterialBagSlot_ ||
            (hasOdds &&
                forgeOdds_[oddsIndex].quantity != 0)) {
            return false;
        }
        if (hasOdds) {
            std::move(
                forgeOdds_ + oddsIndex + 1,
                forgeOdds_ + forgeOddsCount_,
                forgeOdds_ + oddsIndex);
            --forgeOddsCount_;
            forgeOdds_[forgeOddsCount_] =
                SecureForgeOddsSelection{};
        }
        forgeEquipmentBagSlot_ = selection.bagSlot;
        return true;
    }

    if (selection.destination ==
        LegacyForgePrimaryMaterialDestination) {
        if (selection.bagSlot ==
                forgeEquipmentBagSlot_ ||
            (hasOdds &&
                forgeOdds_[oddsIndex].quantity != 0)) {
            return false;
        }
        if (hasOdds) {
            std::move(
                forgeOdds_ + oddsIndex + 1,
                forgeOdds_ + forgeOddsCount_,
                forgeOdds_ + oddsIndex);
            --forgeOddsCount_;
            forgeOdds_[forgeOddsCount_] =
                SecureForgeOddsSelection{};
        }
        forgePrimaryMaterialBagSlot_ =
            selection.bagSlot;
        return true;
    }

    if (selection.destination ==
        LegacyForgeOddsDescriptorDestination) {
        if (selection.bagSlot ==
                forgeEquipmentBagSlot_ ||
            selection.bagSlot ==
                forgePrimaryMaterialBagSlot_) {
            return false;
        }
        if (hasOdds) {
            forgeOdds_[oddsIndex].descriptorLinked = true;
            return true;
        }
        if (forgeOddsCount_ == SecureForgeOddsCapacity) {
            return false;
        }
        std::move_backward(
            forgeOdds_ + oddsIndex,
            forgeOdds_ + forgeOddsCount_,
            forgeOdds_ + forgeOddsCount_ + 1);
        forgeOdds_[oddsIndex] =
            SecureForgeOddsSelection{};
        forgeOdds_[oddsIndex].bagSlot =
            selection.bagSlot;
        forgeOdds_[oddsIndex].descriptorLinked = true;
        ++forgeOddsCount_;
        return true;
    }

    if (selection.destination ==
        LegacyForgeOddsIncrementAction) {
        if (!hasOdds ||
            !forgeOdds_[oddsIndex].descriptorLinked) {
            return false;
        }
        std::uint32_t total = 0;
        for (std::size_t index = 0;
             index < forgeOddsCount_;
             ++index) {
            total += forgeOdds_[index].quantity;
        }
        if (total >= SecureForgeOddsCapacity) {
            return false;
        }
        ++forgeOdds_[oddsIndex].quantity;
        return true;
    }

    ResetForgeState();
    return false;
}

bool SecurePendingOperationRegistry::TryCaptureForgeIdentity(
    SecureForgeOddsSelection* odds,
    std::size_t* oddsCount) const noexcept {
    if (odds == nullptr ||
        oddsCount == nullptr ||
        forgeEquipmentBagSlot_ < 0 ||
        forgePrimaryMaterialBagSlot_ < 0 ||
        forgeEquipmentBagSlot_ ==
            forgePrimaryMaterialBagSlot_) {
        return false;
    }

    std::fill_n(
        odds,
        SecureForgeOddsCapacity,
        SecureForgeOddsSelection{});
    *oddsCount = 0;
    std::uint32_t total = 0;
    int previousBagSlot = -1;
    for (std::size_t index = 0;
         index < forgeOddsCount_;
         ++index) {
        const auto& selected = forgeOdds_[index];
        if (selected.bagSlot <= previousBagSlot ||
            selected.bagSlot ==
                forgeEquipmentBagSlot_ ||
            selected.bagSlot ==
                forgePrimaryMaterialBagSlot_) {
            return false;
        }
        previousBagSlot = selected.bagSlot;
        if (selected.quantity == 0) {
            continue;
        }
        if (!selected.descriptorLinked ||
            total >
                SecureForgeOddsCapacity -
                    selected.quantity) {
            return false;
        }
        total += selected.quantity;
        odds[(*oddsCount)++] = selected;
    }
    return total <= SecureForgeOddsCapacity;
}

bool SecurePendingOperationRegistry::ForgeStateMatches(
    const Entry& entry) const noexcept {
    if (!entry.capturesForgeState ||
        entry.forgeEquipmentBagSlot !=
            forgeEquipmentBagSlot_ ||
        entry.forgePrimaryMaterialBagSlot !=
            forgePrimaryMaterialBagSlot_) {
        return false;
    }
    SecureForgeOddsSelection odds[SecureForgeOddsCapacity]{};
    std::size_t oddsCount = 0;
    return TryCaptureForgeIdentity(odds, &oddsCount) &&
        oddsCount == entry.forgeOddsCount &&
        EqualForgeOdds(
            odds,
            entry.forgeOdds,
            oddsCount);
}

SecurePendingOperationRegistry::Entry*
SecurePendingOperationRegistry::FindForge() noexcept {
    for (auto& entry : entries_) {
        if (entry.occupied &&
            entry.family ==
                SecureLegacyCommandFamily::EquipmentForge &&
            entry.characterId == characterId_ &&
            EqualBytes(
                entry.principal,
                principal_,
                sizeof(principal_)) &&
            ForgeStateMatches(entry)) {
            return &entry;
        }
    }
    return nullptr;
}

void SecurePendingOperationRegistry::ResetForgeState() noexcept {
    forgeEquipmentBagSlot_ = -1;
    forgePrimaryMaterialBagSlot_ = -1;
    forgeOddsCount_ = 0;
    std::fill_n(
        forgeOdds_,
        SecureForgeOddsCapacity,
        SecureForgeOddsSelection{});
}

} // namespace godswar::network
