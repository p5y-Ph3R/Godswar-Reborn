#include "SecurePetBindCommandIdentity.h"

namespace godswar::network {
namespace {

std::uint32_t Read32(const std::uint8_t* source) noexcept {
    return source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

bool IsPetManagerNpc(std::uint32_t npcId) noexcept {
    return npcId == LegacySpartaPetManagerNpc ||
        npcId == LegacySpartaSourcePetManagerNpc ||
        npcId == LegacyAthensPetManagerNpc;
}

} // namespace

bool IsLegacyPetBindCandidate(
    const std::uint8_t* bytes,
    std::size_t packetBytes) noexcept {
    if (bytes == nullptr || packetBytes < 20 ||
        !IsPetManagerNpc(Read32(bytes + 4)) ||
        static_cast<std::int32_t>(Read32(bytes + 8)) !=
            LegacyPetManagerDialog) {
        return false;
    }
    const auto wireSubId =
        static_cast<std::int32_t>(Read32(bytes + 16));
    return wireSubId == LegacyPetBindMenuSubId ||
        wireSubId == LegacyPetBindActionSubId;
}

LegacyPetCommandPacketKind ClassifyLegacyPetBindPacket(
    const std::uint8_t* bytes,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept {
    if (!IsLegacyPetBindCandidate(bytes, packetBytes)) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (packetBytes != LegacyPetManagerActionPacketBytes ||
        static_cast<std::int32_t>(Read32(bytes + 12)) !=
            LegacyPetManagerDialog) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    const auto wireSubId =
        static_cast<std::int32_t>(Read32(bytes + 16));
    if (wireSubId != LegacyPetBindMenuSubId) {
        // No stock capture supports a flattened sub-ID 112 mutation.
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    const std::uint32_t childSubId = Read32(bytes + 20);
    bool remainingArgumentsAreNavigation = true;
    for (std::size_t index = 1;
         index < LegacyPetManagerArgumentCount;
         ++index) {
        if (Read32(bytes + 20 + index * 4) != 0xFFFF'FFFFU) {
            remainingArgumentsAreNavigation = false;
            break;
        }
    }
    if (childSubId == 0xFFFF'FFFFU &&
        remainingArgumentsAreNavigation) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr ||
        childSubId !=
            static_cast<std::uint32_t>(LegacyPetBindActionSubId) ||
        !remainingArgumentsAreNavigation) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    intent->family = SecureLegacyCommandFamily::PetBind;
    intent->bytes[0] = 1;
    intent->bytes[1] = 1;
    return LegacyPetCommandPacketKind::Command;
}

} // namespace godswar::network
