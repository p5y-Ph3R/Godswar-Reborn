#include "SecurePetAlterCommandIdentity.h"

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

bool HasZeroTail(const std::uint8_t* bytes) noexcept {
    return bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0;
}

bool IsRebirthSelection(
    std::uint32_t material,
    std::uint8_t quantity) noexcept {
    const bool stockMaterial =
        material == LegacyRebirthSpiritItemId ||
        material == LegacyRebornHarpyiaItemId;
    // The modal's fresh zero selection has material 0. Decrementing its final
    // item leaves the last stock template at +0x18 while count becomes zero.
    return quantity == 0
        ? material == 0 || stockMaterial
        : stockMaterial &&
            quantity <= LegacyMaximumPetAlterMaterialQuantity;
}

} // namespace

bool IsLegacyPetAlterOpcode(std::uint16_t opcode) noexcept {
    return opcode == LegacyPetSoulContractOpcode ||
        opcode == LegacyPetRebirthOpcode;
}

LegacyPetCommandPacketKind ClassifyLegacyPetAlterPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept {
    if (packet == nullptr || packetBytes < 4) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    const auto* bytes = static_cast<const std::uint8_t*>(packet);
    const std::uint16_t opcode = Read16(bytes + 2);
    if (!IsLegacyPetAlterOpcode(opcode)) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr || packetBytes != 12 ||
        Read16(bytes) != packetBytes || !HasZeroTail(bytes)) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    const std::uint32_t material = Read32(bytes + 4);
    const std::uint8_t quantity = bytes[8];
    if (opcode == LegacyPetSoulContractOpcode) {
        if (material != LegacyContractSpiritItemId ||
            quantity > LegacyMaximumPetAlterMaterialQuantity) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
        intent->family = SecureLegacyCommandFamily::PetSoulContract;
    } else {
        if (!IsRebirthSelection(material, quantity)) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
        intent->family = SecureLegacyCommandFamily::PetRebirth;
    }

    intent->bytes[0] = 1;
    intent->bytes[1] = 1;
    Write32(intent->bytes + 2, material);
    intent->bytes[6] = quantity;
    return LegacyPetCommandPacketKind::Command;
}

} // namespace godswar::network
