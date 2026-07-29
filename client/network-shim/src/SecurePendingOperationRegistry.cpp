#include "SecurePendingOperationRegistry.h"

#include "SecureClientRuntimeInternal.h"

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

bool DefaultRandom(
    void*,
    void* destination,
    std::size_t destinationBytes) noexcept {
    return GenerateSystemSecureRandom(
        destination,
        destinationBytes);
}

bool DefaultClock(
    void*,
    std::uint64_t* unixMilliseconds) noexcept {
    return ReadSystemUnixMilliseconds(unixMilliseconds);
}

bool EqualBytes(
    const std::uint8_t* first,
    const std::uint8_t* second,
    std::size_t bytes) noexcept {
    return std::memcmp(first, second, bytes) == 0;
}

bool TryResolveCommandFamily(
    LegacyGearMentorAction action,
    SecureLegacyCommandFamily* family) noexcept {
    if (family == nullptr) {
        return false;
    }

    switch (action) {
        case LegacyGearMentorAction::MakeAttributeStone:
            *family =
                SecureLegacyCommandFamily::MakeAttributeStone;
            return true;
        case LegacyGearMentorAction::TransformCrystal:
            *family =
                SecureLegacyCommandFamily::TransformCrystal;
            return true;
        case LegacyGearMentorAction::CombineGemPieces:
            *family =
                SecureLegacyCommandFamily::CombineGemPieces;
            return true;
        default:
            return false;
    }
}

} // namespace

SecurePendingOperationRegistry::
SecurePendingOperationRegistry() noexcept
    : SecurePendingOperationRegistry(
          nullptr,
          DefaultRandom,
          nullptr,
          DefaultClock) {
}

SecurePendingOperationRegistry::
SecurePendingOperationRegistry(
    void* randomContext,
    SecureOperationRandomGenerator randomGenerator,
    void* clockContext,
    SecureOperationClock clock) noexcept
    : randomContext_(randomContext),
      randomGenerator_(randomGenerator),
      clockContext_(clockContext),
      clock_(clock) {
    InitializeSRWLock(&lock_);
}

SecurePendingOperationRegistry::
~SecurePendingOperationRegistry() noexcept {
    Clear();
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribePacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    *descriptor = LegacyPacketDescriptor{};

    std::uint16_t opcode = 0;
    if (!TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    descriptor->packetBytes =
        static_cast<std::uint16_t>(packetBytes);
    descriptor->opcode = opcode;

    std::uint64_t now = 0;
    if (!ReadNow(&now)) {
        return SecureOperationRegistryResult::ClockFailure;
    }

    std::uint8_t
        loginPrincipal[SecurePrincipalFingerprintBytes]{};
    int selectionSlot = -1;
    bool selected = false;
    std::uint32_t npcId = 0;
    LegacyGearMentorAction gearMentorAction =
        LegacyGearMentorAction::InitialMenu;
    const bool isLogin = TryHashLegacyLoginPrincipal(
        packet,
        packetBytes,
        loginPrincipal,
        sizeof(loginPrincipal));
    const bool isSelection = TryReadLegacyGearSelection(
        packet,
        packetBytes,
            &selectionSlot,
            &selected);
    const bool isGearMentorAction =
        TryReadLegacyGearMentorAction(
            packet,
            packetBytes,
            &gearMentorAction,
            &npcId);

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    if (isLogin) {
        SetPrincipal(loginPrincipal);
    }
    SecureZeroMemory(
        loginPrincipal,
        sizeof(loginPrincipal));

    if (isSelection) {
        if (selected) {
            if (selectionGeneration_ ==
                (std::numeric_limits<std::uint64_t>::max)()) {
                hasSelection_ = false;
                selectedBagSlot_ = -1;
                ReleaseSRWLockExclusive(&lock_);
                return SecureOperationRegistryResult::Capacity;
            }
            ++selectionGeneration_;
            hasSelection_ = true;
            selectedBagSlot_ = selectionSlot;
        } else if (hasSelection_ &&
                   selectedBagSlot_ == selectionSlot) {
            // The stock client clears the selected control immediately
            // before its final action. Preserve the semantic selection.
        }
    }

    if (!isGearMentorAction) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    if (gearMentorAction ==
        LegacyGearMentorAction::InitialMenu) {
        combinePageArmed_ = false;
        combineNpcId_ = 0;
        hasSelection_ = false;
        selectedBagSlot_ = -1;
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    if (gearMentorAction ==
            LegacyGearMentorAction::CombineGemPieces &&
        (!combinePageArmed_ || combineNpcId_ != npcId)) {
        // Wire action 9 first asks the server to open action page 201. The
        // stock client later reuses wire action 9 for confirmation. Navigation
        // is not a valuable command and must also discard a stale selection.
        if (combinePageGeneration_ ==
            (std::numeric_limits<std::uint64_t>::max)()) {
            combinePageArmed_ = false;
            combineNpcId_ = 0;
            hasSelection_ = false;
            selectedBagSlot_ = -1;
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        ++combinePageGeneration_;
        combinePageArmed_ = true;
        combineNpcId_ = npcId;
        hasSelection_ = false;
        selectedBagSlot_ = -1;
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::MakeAttributeStone;
    if (!TryResolveCommandFamily(
            gearMentorAction,
            &family)) {
        // A different stock Gear Mentor operation leaves page 201 and consumes
        // its own client-side controls. It is not assigned a secure identity
        // here, but it must not leak Combine or selection state into a later
        // action.
        combinePageArmed_ = false;
        combineNpcId_ = 0;
        hasSelection_ = false;
        selectedBagSlot_ = -1;
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }
    if (family !=
        SecureLegacyCommandFamily::CombineGemPieces) {
        combinePageArmed_ = false;
        combineNpcId_ = 0;
    }

    if (!hasPrincipal_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoCharacter;
    }
    if (!hasSelection_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoSelection;
    }

    // An unresolved semantic action owns this principal/NPC/bag-slot key.
    // Reuse its UUID even if untrusted scratch/tail bytes change; the server
    // binds that UUID to its first canonical request hash and rejects a
    // different request instead of letting the shim guess that it is fresh.
    Entry* entry = Find(
        family,
        npcId,
        selectedBagSlot_);
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
        entry->occupied = true;
        std::memcpy(
            entry->principal,
            principal_,
            sizeof(entry->principal));
        entry->family = family;
        entry->characterId = characterId_;
        entry->npcId = npcId;
        entry->bagSlot = selectedBagSlot_;
        entry->selectionGeneration = selectionGeneration_;
        entry->combinePageGeneration =
            family ==
                SecureLegacyCommandFamily::CombineGemPieces
            ? combinePageGeneration_
            : 0;
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            ClearEntry(entry);
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::ClockFailure;
        }
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
    if (hasPrincipal_ &&
        hasCharacter_ &&
        characterId_ == entry->characterId &&
        selectedBagSlot_ == entry->bagSlot &&
        selectionGeneration_ ==
            entry->selectionGeneration &&
        EqualBytes(
            principal_,
            entry->principal,
            sizeof(principal_))) {
        hasSelection_ = false;
        selectedBagSlot_ = -1;
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
    ClearEntry(entry);
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::SetCharacter(
    int characterId) noexcept {
    if (characterId <= 0) {
        return SecureOperationRegistryResult::InvalidPacket;
    }

    AcquireSRWLockExclusive(&lock_);
    const bool changed =
        !hasCharacter_ || characterId_ != characterId;
    hasCharacter_ = true;
    characterId_ = characterId;
    if (changed) {
        hasSelection_ = false;
        selectedBagSlot_ = -1;
        combinePageArmed_ = false;
        combineNpcId_ = 0;
    }
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

SecurePendingOperationSnapshot
SecurePendingOperationRegistry::Snapshot() noexcept {
    SecurePendingOperationSnapshot snapshot{};
    std::uint64_t now = 0;
    if (!ReadNow(&now)) {
        return snapshot;
    }

    AcquireSRWLockExclusive(&lock_);
    Prune(now);
    for (const auto& entry : entries_) {
        if (entry.occupied) {
            ++snapshot.pending;
        }
    }
    for (const auto& tombstone : tombstones_) {
        if (tombstone.occupied) {
            ++snapshot.resolved;
        }
    }
    snapshot.hasPrincipal = hasPrincipal_;
    snapshot.hasCharacter = hasCharacter_;
    snapshot.characterId = characterId_;
    snapshot.hasSelection = hasSelection_;
    snapshot.selectedBagSlot = selectedBagSlot_;
    snapshot.combinePageArmed = combinePageArmed_;
    snapshot.combineNpcId = combineNpcId_;
    ReleaseSRWLockExclusive(&lock_);
    return snapshot;
}

void SecurePendingOperationRegistry::Clear() noexcept {
    AcquireSRWLockExclusive(&lock_);
    for (auto& entry : entries_) {
        ClearEntry(&entry);
    }
    for (auto& tombstone : tombstones_) {
        ClearTombstone(&tombstone);
    }
    SecureZeroMemory(principal_, sizeof(principal_));
    hasPrincipal_ = false;
    hasCharacter_ = false;
    characterId_ = -1;
    hasSelection_ = false;
    selectedBagSlot_ = -1;
    selectionGeneration_ = 0;
    combinePageArmed_ = false;
    combineNpcId_ = 0;
    combinePageGeneration_ = 0;
    ReleaseSRWLockExclusive(&lock_);
}

bool SecurePendingOperationRegistry::ReadNow(
    std::uint64_t* now) noexcept {
    return now != nullptr &&
        clock_ != nullptr &&
        clock_(clockContext_, now);
}

void SecurePendingOperationRegistry::Prune(
    std::uint64_t now) noexcept {
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
    int bagSlot) noexcept {
    for (auto& entry : entries_) {
        if (entry.occupied &&
            entry.characterId == characterId_ &&
            entry.npcId == npcId &&
            entry.bagSlot == bagSlot &&
            entry.family == family &&
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
        hasSelection_ = false;
        selectedBagSlot_ = -1;
        combinePageArmed_ = false;
        combineNpcId_ = 0;
    }
}

void SecurePendingOperationRegistry::ClearEntry(
    Entry* entry) noexcept {
    if (entry != nullptr) {
        SecureZeroMemory(entry, sizeof(*entry));
        entry->bagSlot = -1;
    }
}

void SecurePendingOperationRegistry::ClearTombstone(
    Tombstone* tombstone) noexcept {
    if (tombstone != nullptr) {
        SecureZeroMemory(tombstone, sizeof(*tombstone));
    }
}

} // namespace godswar::network
