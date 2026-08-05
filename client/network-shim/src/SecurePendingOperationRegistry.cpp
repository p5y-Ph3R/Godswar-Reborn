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

bool TryResolveCommandFamily(
    LegacyGearMentorAction action,
    SecureLegacyCommandFamily* family) noexcept {
    if (family == nullptr) {
        return false;
    }

    switch (action) {
        case LegacyGearMentorAction::DecomposeGear:
            *family =
                SecureLegacyCommandFamily::DecomposeGear;
            return true;
        case LegacyGearMentorAction::EnhanceAttribute:
            *family =
                SecureLegacyCommandFamily::
                    GearMentorEnhanceAttribute;
            return true;
        case LegacyGearMentorAction::AddAttribute:
            *family =
                SecureLegacyCommandFamily::
                    GearMentorAddAttribute;
            return true;
        case LegacyGearMentorAction::MakeAttributeStone:
            *family =
                SecureLegacyCommandFamily::MakeAttributeStone;
            return true;
        case LegacyGearMentorAction::DeleteAttribute:
            *family =
                SecureLegacyCommandFamily::
                    GearMentorDeleteAttribute;
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

bool HasValidSelectionCount(
    SecureLegacyCommandFamily family,
    std::size_t selectionCount) noexcept {
    switch (family) {
        case SecureLegacyCommandFamily::DecomposeGear:
            return selectionCount >= 1 &&
                selectionCount <= SecureThreeSlotSelectionCount;
        case SecureLegacyCommandFamily::
                GearMentorEnhanceAttribute:
        case SecureLegacyCommandFamily::
                GearMentorAddAttribute:
        case SecureLegacyCommandFamily::
                GearMentorDeleteAttribute:
            return selectionCount == SecureThreeSlotSelectionCount;
        case SecureLegacyCommandFamily::MakeAttributeStone:
        case SecureLegacyCommandFamily::TransformCrystal:
        case SecureLegacyCommandFamily::CombineGemPieces:
            return selectionCount == 1;
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
    const auto lifecycleResult = DescribeCharacterLifecyclePacket(
        packet, packetBytes, now, descriptor);
    if (lifecycleResult != SecureOperationRegistryResult::Success ||
        descriptor->hasOperation)
        return lifecycleResult;
    const auto petResult = DescribePetPacket(packet, packetBytes, now, descriptor);
    if (petResult != SecureOperationRegistryResult::Success ||
        descriptor->hasOperation) return petResult;
    if (IsLegacyForgeOpcode(opcode)) {
        return DescribeForgePacket(
            packet,
            packetBytes,
            opcode,
            now,
            descriptor);
    }
    bool inventoryPacket = false;
    const auto inventoryResult = DescribeInventoryPacket(
        packet,
        packetBytes,
        now,
        descriptor,
        &inventoryPacket);
    if (inventoryPacket) {
        return inventoryResult;
    }

    bool zodiacPacket = false;
    const auto zodiacResult = DescribeZodiacPacket(
        packet,
        packetBytes,
        now,
        descriptor,
        &zodiacPacket);
    if (zodiacPacket) {
        return zodiacResult;
    }

    bool classSuitPacket = false;
    const auto classSuitResult = DescribeClassSuitPacket(
        packet, packetBytes, now, descriptor, &classSuitPacket);
    if (classSuitPacket) {
        return classSuitResult;
    }

    bool holyEquipmentPacket = false;
    const auto holyEquipmentResult = DescribeHolyEquipmentPacket(
        packet, packetBytes, now, descriptor, &holyEquipmentPacket);
    if (holyEquipmentPacket) {
        return holyEquipmentResult;
    }

    std::uint8_t
        loginPrincipal[SecurePrincipalFingerprintBytes]{};
    int selectionSlot = -1;
    bool selected = false;
    std::uint32_t npcId = 0;
    LegacyGearMentorAction gearMentorAction =
        LegacyGearMentorAction::InitialMenu;
    LegacyGearMentorAction originEnhancerAction =
        LegacyGearMentorAction::InitialMenu;
    LegacyGearMentorAction originEnhancerNavigation =
        LegacyGearMentorAction::InitialMenu;
    std::uint32_t originEnhancerNpcId = 0;
    std::uint32_t originEnhancerNavigationNpcId = 0;
    int originEnhancerBagSlots[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1,
        -1};
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
    const bool isOriginEnhancerCommit =
        TryReadLegacyOriginEnhancerCommit(
            packet,
            packetBytes,
            &originEnhancerAction,
            &originEnhancerNpcId,
            &originEnhancerBagSlots[0],
            &originEnhancerBagSlots[1],
            &originEnhancerBagSlots[2]);
    const bool isOriginEnhancerNavigation =
        TryReadLegacyOriginEnhancerNavigation(
            packet,
            packetBytes,
            &originEnhancerNavigation,
            &originEnhancerNavigationNpcId);

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
                ResetSelectionState();
                ReleaseSRWLockExclusive(&lock_);
                return SecureOperationRegistryResult::Capacity;
            }
            if (AddSelection(selectionSlot)) {
                ++selectionGeneration_;
            }
        } else {
            RemoveSelection(selectionSlot, now);
        }
    }

    if (isOriginEnhancerNavigation) {
        combinePageArmed_ = false;
        combineNpcId_ = 0;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    if (!isGearMentorAction &&
        !isOriginEnhancerCommit) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    if (isGearMentorAction &&
        gearMentorAction ==
        LegacyGearMentorAction::InitialMenu) {
        combinePageArmed_ = false;
        combineNpcId_ = 0;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    if (isGearMentorAction &&
        gearMentorAction ==
            LegacyGearMentorAction::CombineGemPieces &&
        (!combinePageArmed_ || combineNpcId_ != npcId)) {
        // Wire action 9 first asks the server to open action page 201. The
        // stock client later reuses wire action 9 for confirmation. Navigation
        // is not a valuable command and must also discard a stale selection.
        if (combinePageGeneration_ ==
            (std::numeric_limits<std::uint64_t>::max)()) {
            combinePageArmed_ = false;
            combineNpcId_ = 0;
            ResetSelectionState();
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::Capacity;
        }
        ++combinePageGeneration_;
        combinePageArmed_ = true;
        combineNpcId_ = npcId;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }

    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::MakeAttributeStone;
    if (!TryResolveCommandFamily(
            isOriginEnhancerCommit
                ? originEnhancerAction
                : gearMentorAction,
            &family)) {
        // A different stock Gear Mentor operation leaves page 201 and consumes
        // its own client-side controls. It is not assigned a secure identity
        // here, but it must not leak Combine or selection state into a later
        // action.
        combinePageArmed_ = false;
        combineNpcId_ = 0;
        ResetSelectionState();
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::Success;
    }
    if (isGearMentorAction &&
        family !=
        SecureLegacyCommandFamily::CombineGemPieces) {
        combinePageArmed_ = false;
        combineNpcId_ = 0;
    }
    if (isOriginEnhancerCommit) {
        npcId = originEnhancerNpcId;
        combinePageArmed_ = false;
        combineNpcId_ = 0;
        ResetSelectionState();
    }

    if (!hasPrincipal_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoPrincipal;
    }
    if (!hasCharacter_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoCharacter;
    }
    int identityBagSlots[SecureGearSelectionCapacity]{
        -1,
        -1,
        -1,
        -1};
    std::size_t identitySelectionCount = 0;
    if (isOriginEnhancerCommit) {
        std::memcpy(
            identityBagSlots,
            originEnhancerBagSlots,
            sizeof(identityBagSlots));
        identitySelectionCount = SecureThreeSlotSelectionCount;
    } else if (!TryGetIdentitySelection(
                   identityBagSlots,
                   &identitySelectionCount)) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureOperationRegistryResult::NoSelection;
    }
    if (!HasValidSelectionCount(
            family,
            identitySelectionCount)) {
        if (isGearMentorAction &&
            (family ==
                    SecureLegacyCommandFamily::
                        GearMentorEnhanceAttribute ||
                family ==
                    SecureLegacyCommandFamily::
                        GearMentorAddAttribute ||
                family ==
                    SecureLegacyCommandFamily::
                        GearMentorDeleteAttribute)) {
            ResetSelectionState();
        }
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
        identityBagSlots,
        identitySelectionCount);
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
        entry->selectionCount = identitySelectionCount;
        std::memcpy(
            entry->bagSlots,
            identityBagSlots,
            sizeof(entry->bagSlots));
        entry->capturesSelectionState =
            !isOriginEnhancerCommit;
        if (entry->capturesSelectionState) {
            entry->capturedSelectionCount =
                identitySelectionCount;
            std::memcpy(
                entry->capturedSelectionBagSlots,
                identityBagSlots,
                sizeof(entry->capturedSelectionBagSlots));
        }
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
        ResetSelectionState();
        ResetForgeState();
        combinePageArmed_ = false;
        combineNpcId_ = 0;
        ClearClassSuitPage();
        ClearHolyStoneUpgradePage();
        ClearHolyStoneImplementPage();
        ClearHolyStoneCombinePage();
    }
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
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
    ResetSelectionState();
    ResetForgeState();
    selectionGeneration_ = 0;
    combinePageArmed_ = false;
    combineNpcId_ = 0;
    combinePageGeneration_ = 0;
    ClearClassSuitPage();
    classSuitPageGeneration_ = 0;
    ClearHolyStoneUpgradePage();
    holyStoneUpgradePageGeneration_ = 0;
    ClearHolyStoneImplementPage();
    holyStoneImplementPageGeneration_ = 0;
    ClearHolyStoneCombinePage();
    holyStoneCombinePageGeneration_ = 0;
    ReleaseSRWLockExclusive(&lock_);
}

} // namespace godswar::network
