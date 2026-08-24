#include "SecurePendingOperationRegistry.h"

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

bool IsWarehouseManager(std::uint32_t npcId) noexcept {
    return npcId == LegacyAthensWarehouseManagerNpc ||
        npcId == LegacySpartaWarehouseManagerNpc;
}

} // namespace

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeWarehousePacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor,
    bool* recognized) noexcept {
    if (descriptor == nullptr || recognized == nullptr) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    *recognized = false;

    LegacyWarehouseTransferCommand transfer{};
    switch (ClassifyLegacyWarehouseTransferPacket(
                packet, packetBytes, &transfer)) {
        case LegacyWarehousePacketKind::Transfer:
            *recognized = true;
            return DescribeWarehouseTransfer(
                transfer, now, descriptor);
        case LegacyWarehousePacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        case LegacyWarehousePacketKind::Unrelated:
        default:
            break;
    }

    LegacyWarehouseExpansionCommand expansion{};
    switch (ClassifyLegacyWarehouseExpansionPacket(
                packet, packetBytes, &expansion)) {
        case LegacyWarehousePacketKind::Expansion:
            *recognized = true;
            return DescribeWarehouseExpansion(
                expansion, now, descriptor);
        case LegacyWarehousePacketKind::Navigation:
            *recognized = true;
            return SecureOperationRegistryResult::Success;
        case LegacyWarehousePacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        case LegacyWarehousePacketKind::Unrelated:
        default:
            return SecureOperationRegistryResult::Success;
    }
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeWarehouseTransfer(
    const LegacyWarehouseTransferCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    const auto operation = static_cast<int>(command.operation);
    if (descriptor == nullptr || operation < 1 || operation > 3) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    const int identity[SecureGearSelectionCapacity]{
        operation,
        command.warehouseSlot,
        command.kitBagSlot,
        command.destinationWarehouseSlot};

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

    Entry* entry = Find(
        SecureLegacyCommandFamily::WarehouseTransfer,
        0,
        identity,
        SecureGearSelectionCapacity);
    if (entry == nullptr) {
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::ClockFailure;
        }
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
        entry->family =
            SecureLegacyCommandFamily::WarehouseTransfer;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = SecureGearSelectionCapacity;
        std::memcpy(
            entry->bagSlots,
            identity,
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
        sizeof(descriptor->operation.operationId));
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeWarehouseExpansion(
    const LegacyWarehouseExpansionCommand& command,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor) noexcept {
    if (descriptor == nullptr || !IsWarehouseManager(command.npcId)) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    const int identity[SecureGearSelectionCapacity]{
        LegacyWarehouseManagerDialog,
        LegacyWarehouseManagerExpandSubId,
        -1,
        -1};

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

    // Both capitals expose one character-owned expansion authority. City is
    // normalized so a map transfer cannot mint another retry UUID.
    Entry* entry = Find(
        SecureLegacyCommandFamily::WarehouseExpansion,
        0,
        identity,
        2);
    if (entry == nullptr) {
        if (now >
            (std::numeric_limits<std::uint64_t>::max)() -
                SecurePendingOperationLifetimeMilliseconds) {
            ReleaseSRWLockExclusive(&lock_);
            return SecureOperationRegistryResult::ClockFailure;
        }
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
        entry->family =
            SecureLegacyCommandFamily::WarehouseExpansion;
        entry->characterId = characterId_;
        entry->npcId = 0;
        entry->selectionCount = 2;
        std::memcpy(
            entry->bagSlots,
            identity,
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
        sizeof(descriptor->operation.operationId));
    ReleaseSRWLockExclusive(&lock_);
    return SecureOperationRegistryResult::Success;
}

} // namespace godswar::network
