#include "SecureWarehouseCommandIdentity.h"

#include "SecureLegacyCommandIdentity.h"

namespace godswar::network {
namespace {

std::uint16_t ReadUInt16Little(const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

std::uint32_t ReadUInt32Little(const std::uint8_t* source) noexcept {
    return source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

std::int16_t ReadInt16Little(const std::uint8_t* source) noexcept {
    return static_cast<std::int16_t>(ReadUInt16Little(source));
}

std::int32_t ReadInt32Little(const std::uint8_t* source) noexcept {
    return static_cast<std::int32_t>(ReadUInt32Little(source));
}

bool IsWarehouseSlot(int slot) noexcept {
    return slot >= 0 && slot < LegacyWarehouseSlots;
}

bool IsKitBagPage(int page) noexcept {
    return page >= 0 && page < LegacyWarehouseKitBagPages;
}

bool IsKitBagCell(int cell) noexcept {
    return cell >= 0 &&
        cell < LegacyWarehouseKitBagSlotsPerPage;
}

int FlattenKitBagSlot(int page, int cell) noexcept {
    return page * LegacyWarehouseKitBagSlotsPerPage + cell;
}

bool IsWarehouseManager(std::uint32_t npcId) noexcept {
    return npcId == LegacyAthensWarehouseManagerNpc ||
        npcId == LegacySpartaWarehouseManagerNpc;
}

} // namespace

LegacyWarehousePacketKind ClassifyLegacyWarehouseTransferPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyWarehouseTransferCommand* command) noexcept {
    if (packet == nullptr || packetBytes < 4) {
        return LegacyWarehousePacketKind::Unrelated;
    }

    const auto* bytes = static_cast<const std::uint8_t*>(packet);
    if (ReadUInt16Little(bytes + 2) != LegacyWarehouseTransferOpcode) {
        return LegacyWarehousePacketKind::Unrelated;
    }

    std::uint16_t opcode = 0;
    if (packetBytes != LegacyWarehouseTransferPacketBytes ||
        !TryReadLegacyPacketHeader(packet, packetBytes, &opcode) ||
        opcode != LegacyWarehouseTransferOpcode) {
        return LegacyWarehousePacketKind::InvalidMutation;
    }

    const int warehouseSlot = ReadInt16Little(bytes + 4);
    const int secondIndex = ReadInt16Little(bytes + 6);
    const int thirdIndex = ReadInt16Little(bytes + 8);
    const auto money = ReadInt32Little(bytes + 12);
    const auto direction = bytes[16];
    const auto storageType = ReadUInt16Little(bytes + 18);
    if (money != 0 || direction > 1) {
        return LegacyWarehousePacketKind::InvalidMutation;
    }

    LegacyWarehouseTransferCommand parsed{};
    if (direction == 1) {
        // The stock normal-deposit sender leaves the tail word as scratch.
        // Direction 1 has no award-storage operation, so normalize it away.
        if ((warehouseSlot != -1 && !IsWarehouseSlot(warehouseSlot)) ||
            !IsKitBagPage(secondIndex) ||
            !IsKitBagCell(thirdIndex)) {
            return LegacyWarehousePacketKind::InvalidMutation;
        }
        parsed.operation = LegacyWarehouseTransferOperation::Deposit;
        parsed.warehouseSlot = warehouseSlot;
        parsed.kitBagSlot = FlattenKitBagSlot(secondIndex, thirdIndex);
    } else {
        if (!IsWarehouseSlot(warehouseSlot)) {
            return LegacyWarehousePacketKind::InvalidMutation;
        }
        // The stock normal-warehouse drag sender writes 1 in the tail for
        // this exact internal-move shape. Its receive path ignores the tail,
        // so normalize 0/1 here while keeping award withdrawals rejected.
        if (IsWarehouseSlot(secondIndex) && thirdIndex == -1 &&
            secondIndex != warehouseSlot && storageType <= 1) {
            parsed.operation =
                LegacyWarehouseTransferOperation::InternalMove;
            parsed.warehouseSlot = warehouseSlot;
            parsed.destinationWarehouseSlot = secondIndex;
        } else if (storageType != 0) {
            // Award storage is a separate client collection and is not part
            // of character warehouse authority.
            return LegacyWarehousePacketKind::InvalidMutation;
        } else if (secondIndex == -1 && thirdIndex == -1) {
            parsed.operation = LegacyWarehouseTransferOperation::Withdraw;
            parsed.warehouseSlot = warehouseSlot;
        } else if (IsKitBagPage(secondIndex) &&
                   IsKitBagCell(thirdIndex)) {
            parsed.operation = LegacyWarehouseTransferOperation::Withdraw;
            parsed.warehouseSlot = warehouseSlot;
            parsed.kitBagSlot =
                FlattenKitBagSlot(secondIndex, thirdIndex);
        } else {
            return LegacyWarehousePacketKind::InvalidMutation;
        }
    }

    if (command != nullptr) {
        *command = parsed;
    }
    return LegacyWarehousePacketKind::Transfer;
}

LegacyWarehousePacketKind ClassifyLegacyWarehouseExpansionPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyWarehouseExpansionCommand* command) noexcept {
    if (packet == nullptr || packetBytes < 12) {
        return LegacyWarehousePacketKind::Unrelated;
    }

    const auto* bytes = static_cast<const std::uint8_t*>(packet);
    const auto opcode = ReadUInt16Little(bytes + 2);
    const auto npcId = ReadUInt32Little(bytes + 4);
    const auto dialog = ReadInt32Little(bytes + 8);
    if (opcode != LegacyNpcFunctionActionOpcode ||
        !IsWarehouseManager(npcId) ||
        dialog != LegacyWarehouseManagerDialog) {
        return LegacyWarehousePacketKind::Unrelated;
    }

    std::uint16_t parsedOpcode = 0;
    if (packetBytes != LegacyWarehouseManagerPacketBytes ||
        !TryReadLegacyPacketHeader(
            packet, packetBytes, &parsedOpcode) ||
        parsedOpcode != LegacyNpcFunctionActionOpcode ||
        ReadInt32Little(bytes + 12) !=
            LegacyWarehouseManagerDialog) {
        return LegacyWarehousePacketKind::InvalidMutation;
    }

    const auto subId = ReadInt32Little(bytes + 16);
    if (subId == LegacyWarehouseManagerInitialSubId) {
        return LegacyWarehousePacketKind::Navigation;
    }
    if (subId != LegacyWarehouseManagerExpandSubId) {
        return LegacyWarehousePacketKind::InvalidMutation;
    }

    if (command != nullptr) {
        command->npcId = npcId;
    }
    return LegacyWarehousePacketKind::Expansion;
}

} // namespace godswar::network
