#include "SecurePetCommandIdentity.h"

#include "SecureLegacyCommandIdentity.h"

#include <cstring>

namespace godswar::network {
namespace {

std::uint16_t Read16(const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

std::uint32_t Read32(const std::uint8_t* source) noexcept {
    return source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

void Write32(
    std::uint8_t* destination,
    std::uint32_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] = static_cast<std::uint8_t>(value >> 8U);
    destination[2] = static_cast<std::uint8_t>(value >> 16U);
    destination[3] = static_cast<std::uint8_t>(value >> 24U);
}

bool IsPetOpcode(std::uint16_t opcode) noexcept {
    return opcode == LegacyBagItemActivationOpcode ||
        opcode == LegacyPetTakeOpcode ||
        opcode == LegacyPetCallOutOpcode ||
        opcode == LegacyPetRecallOpcode ||
        opcode == LegacyPetLevelUpgradeOpcode;
}

} // namespace

LegacyPetCommandPacketKind ClassifyLegacyPetCommandPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept {
    std::uint16_t opcode = 0;
    if (!TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode) ||
        !IsPetOpcode(opcode)) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    *intent = LegacyPetCommandIntent{};
    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);

    if (opcode == LegacyBagItemActivationOpcode) {
        if (packetBytes != LegacyBagItemActivationPacketBytes) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
        const std::uint16_t page = Read16(bytes + 12);
        const std::uint16_t slot = Read16(bytes + 14);
        if (page >= 4 || slot >= 24) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
        intent->family =
            SecureLegacyCommandFamily::BagItemActivation;
        intent->bytes[0] = 1;
        intent->bytes[1] = 1;
        const std::uint16_t absolute =
            static_cast<std::uint16_t>(page * 24 + slot);
        intent->bytes[2] = static_cast<std::uint8_t>(absolute);
        intent->bytes[3] =
            static_cast<std::uint8_t>(absolute >> 8U);
        // Deliberately ignore the item/action hint. Only the authenticated
        // character and authoritative bag slot define this operation.
        return LegacyPetCommandPacketKind::Command;
    }

    if (packetBytes != LegacyPetCommandPacketBytes) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    const std::uint32_t petId = Read32(bytes + 4);
    if (petId == 0) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    intent->family =
        opcode == LegacyPetLevelUpgradeOpcode
        ? SecureLegacyCommandFamily::PetLevelUpgrade
        : SecureLegacyCommandFamily::PetPresenceTransition;
    intent->bytes[0] = 1;
    intent->bytes[1] =
        opcode == LegacyPetTakeOpcode ? 1 :
        opcode == LegacyPetCallOutOpcode ? 2 :
        opcode == LegacyPetRecallOpcode ? 3 : 4;
    Write32(intent->bytes + 2, petId);
    return LegacyPetCommandPacketKind::Command;
}

bool EqualPetCommandIntent(
    const LegacyPetCommandIntent& first,
    const LegacyPetCommandIntent& second) noexcept {
    return first.family == second.family &&
        std::memcmp(
            first.bytes,
            second.bytes,
            sizeof(first.bytes)) == 0;
}

} // namespace godswar::network
