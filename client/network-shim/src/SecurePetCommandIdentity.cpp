#include "SecurePetCommandIdentity.h"

#include "SecureLegacyCommandIdentity.h"
#include "SecurePetAlterCommandIdentity.h"
#include "SecurePetBindCommandIdentity.h"
#include "SecurePetManagerUtilityIdentity.h"

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
        opcode == LegacyPetToPetMergeOpcode ||
        opcode == LegacyPetOwnerMergeOpcode ||
        opcode == LegacyPetLevelUpgradeOpcode;
}

bool IsPetManagerNpc(std::uint32_t npcId) noexcept {
    return npcId == LegacySpartaPetManagerNpc ||
        npcId == LegacySpartaSourcePetManagerNpc ||
        npcId == LegacyAthensPetManagerNpc;
}

bool IsPetPointResetMutationCandidate(
    const std::uint8_t* bytes,
    std::size_t packetBytes) noexcept {
    if (bytes == nullptr || packetBytes < 20 ||
        !IsPetManagerNpc(Read32(bytes + 4)) ||
        static_cast<std::int32_t>(Read32(bytes + 8)) !=
            LegacyPetPointResetDialog) {
        return false;
    }
    const auto subId =
        static_cast<std::int32_t>(Read32(bytes + 16));
    if (subId == LegacyPetGrowthResetActionSubId ||
        subId == LegacyPetBasicSavvyResetActionSubId) {
        return true;
    }
    if ((subId == LegacyPetGrowthResetMenuSubId ||
         subId == LegacyPetBasicSavvyResetMenuSubId) &&
        packetBytes >= 24) {
        return Read32(bytes + 20) != 0xFFFF'FFFFU;
    }
    return false;
}

bool IsPetAppearanceChangeCandidate(
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
    return wireSubId == LegacyPetAppearanceChangeSubId ||
        wireSubId == LegacyPetAppearanceDescriptionSubId;
}

LegacyPetCommandPacketKind ClassifyPetAppearanceChange(
    const std::uint8_t* bytes,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept {
    if (!IsPetAppearanceChangeCandidate(bytes, packetBytes)) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (packetBytes != LegacyPetManagerActionPacketBytes ||
        static_cast<std::int32_t>(Read32(bytes + 12)) !=
            LegacyPetManagerDialog) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    const auto wireSubId =
        static_cast<std::int32_t>(Read32(bytes + 16));
    if (wireSubId != LegacyPetAppearanceChangeSubId) {
        // The stock confirmation retains root choice 8. Page 113 is an
        // informational row, not a selectable child, so its OK branch does
        // not flatten 113 into the wire sub-ID.
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    bool navigation = true;
    for (std::size_t index = 0;
         index < LegacyPetManagerArgumentCount;
         ++index) {
        const bool scratch =
            index >= LegacyPetManagerScratchArgumentFirst &&
            index < LegacyPetManagerScratchArgumentFirst +
                LegacyPetManagerScratchArgumentCount;
        if (!scratch &&
            Read32(bytes + 20 + index * 4) != 0xFFFF'FFFFU) {
            navigation = false;
            break;
        }
    }
    if (navigation) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    for (std::size_t index = 0;
         index < LegacyPetManagerArgumentCount;
         ++index) {
        const auto value = Read32(bytes + 20 + index * 4);
        const bool scratch =
            index >= LegacyPetManagerScratchArgumentFirst &&
            index < LegacyPetManagerScratchArgumentFirst +
                LegacyPetManagerScratchArgumentCount;
        if (index == 0) {
            if (value != static_cast<std::uint32_t>(
                    LegacyPetAppearanceConfirmationArgument)) {
                return LegacyPetCommandPacketKind::InvalidMutation;
            }
        } else if (index != LegacyPetAppearanceItemArgumentIndex &&
                   !scratch &&
                   value != 0xFFFF'FFFFU) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
    }

    const std::uint32_t coordinate = Read32(
        bytes + 20 + LegacyPetAppearanceItemArgumentIndex * 4);
    if (coordinate == 0xFFFF'FFFFU) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    const std::uint32_t page = coordinate / 100U;
    const std::uint32_t slot = coordinate % 100U;
    if (page >= LegacyPetBagPageCount ||
        slot >= LegacyPetBagSlotsPerPage) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    const std::uint16_t absolute = static_cast<std::uint16_t>(
        page * LegacyPetBagSlotsPerPage + slot);
    intent->family =
        SecureLegacyCommandFamily::PetAppearanceChange;
    intent->bytes[0] = 1;
    intent->bytes[1] = 1;
    intent->bytes[2] = static_cast<std::uint8_t>(absolute);
    intent->bytes[3] = static_cast<std::uint8_t>(absolute >> 8U);
    return LegacyPetCommandPacketKind::Command;
}

bool TryMapPetSkillUnlearnSubId(
    std::int32_t subId,
    std::uint8_t* skillSlot) noexcept {
    if (skillSlot == nullptr) {
        return false;
    }
    if (subId >= LegacyPetSkillUnlearnFirstSubId &&
        subId <= LegacyPetSkillUnlearnFirstRangeLastSubId) {
        *skillSlot = static_cast<std::uint8_t>(
            subId - LegacyPetSkillUnlearnFirstSubId);
        return true;
    }
    if (subId >= LegacyPetSkillUnlearnSecondRangeFirstSubId &&
        subId <= LegacyPetSkillUnlearnLastSubId) {
        *skillSlot = static_cast<std::uint8_t>(
            6 + subId -
                LegacyPetSkillUnlearnSecondRangeFirstSubId);
        return true;
    }
    return false;
}

LegacyPetCommandPacketKind ClassifyPetSkillUnlearn(
    const std::uint8_t* bytes,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept {
    // A different NPC-function packet is unrelated to pet skill removal.
    // Once the endpoint, dialog, and mutation sub-ID match, however, the
    // entire captured shape is security-sensitive and must be exact.
    if (bytes == nullptr || packetBytes < 20) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    const std::uint32_t npcId = Read32(bytes + 4);
    const auto dialog = static_cast<std::int32_t>(Read32(bytes + 8));
    const auto wireSubId = static_cast<std::int32_t>(Read32(bytes + 16));
    const bool nestedSelection =
        wireSubId == LegacyPetSkillUnlearnMenuSubId;
    const auto subId = nestedSelection && packetBytes >= 24
        ? static_cast<std::int32_t>(Read32(bytes + 20))
        : wireSubId;
    std::uint8_t skillSlot = 0;
    if (!IsPetManagerNpc(npcId) || dialog != LegacyPetManagerDialog) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (!TryMapPetSkillUnlearnSubId(subId, &skillSlot)) {
        // Parent choice 6 opens a read-only, server-projected menu. The stock
        // client may retain irrelevant UI scratch values in that request, so
        // only a bounded child erase choice becomes a secure mutation.
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr ||
        packetBytes != LegacyPetManagerActionPacketBytes ||
        static_cast<std::int32_t>(Read32(bytes + 12)) !=
            LegacyPetManagerDialog) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    const std::size_t firstPaddingIndex =
        nestedSelection ? 1 : 0;
    for (std::size_t index = firstPaddingIndex;
         index < LegacyPetManagerArgumentCount;
         ++index) {
        if (Read32(bytes + 20 + index * 4) != 0xFFFF'FFFFU) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
    }

    intent->family = SecureLegacyCommandFamily::PetSkillUnlearn;
    intent->bytes[0] = 1;
    intent->bytes[1] = 1;
    intent->bytes[2] = skillSlot;
    return LegacyPetCommandPacketKind::Command;
}

LegacyPetCommandPacketKind ClassifyPetPointReset(
    const std::uint8_t* bytes,
    std::size_t packetBytes,
    std::int32_t menuSubId,
    std::int32_t actionSubId,
    SecureLegacyCommandFamily family,
    LegacyPetCommandIntent* intent) noexcept {
    if (bytes == nullptr || packetBytes < 20) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    const std::uint32_t npcId = Read32(bytes + 4);
    const auto dialog = static_cast<std::int32_t>(Read32(bytes + 8));
    const auto wireSubId = static_cast<std::int32_t>(Read32(bytes + 16));
    if (!IsPetManagerNpc(npcId) || dialog != LegacyPetPointResetDialog) {
        return LegacyPetCommandPacketKind::Unrelated;
    }

    const bool nestedSelection =
        wireSubId == menuSubId &&
        packetBytes >= 24 &&
        static_cast<std::int32_t>(Read32(bytes + 20)) ==
            actionSubId;
    const bool directSelection =
        wireSubId == actionSubId;
    if (!nestedSelection && !directSelection) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr ||
        packetBytes != LegacyPetManagerActionPacketBytes ||
        static_cast<std::int32_t>(Read32(bytes + 12)) !=
            LegacyPetPointResetDialog) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    const std::size_t operationIndex = nestedSelection ? 1 : 0;
    const std::uint32_t operationValue =
        Read32(bytes + 20 + operationIndex * 4);
    const bool isPreview = operationValue == 0xFFFF'FFFFU;
    const bool isAccept = operationValue == 0;
    if (!isPreview && !isAccept) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    for (std::size_t index = operationIndex + 1;
         index < LegacyPetManagerArgumentCount;
         ++index) {
        if (Read32(bytes + 20 + index * 4) != 0xFFFF'FFFFU) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
    }

    intent->family = family;
    intent->bytes[0] = 1;
    intent->bytes[1] = isPreview ? 1 : 2;
    return LegacyPetCommandPacketKind::Command;
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
            &opcode)) {
        if (packet != nullptr && packetBytes >= 4 &&
            packetBytes <= SecureLegacyMaximumPacketBytes) {
            const auto* malformed =
                static_cast<const std::uint8_t*>(packet);
            if (IsLegacyPetAlterOpcode(Read16(malformed + 2))) {
                return LegacyPetCommandPacketKind::InvalidMutation;
            }
            if (Read16(malformed + 2) ==
                    LegacyNpcFunctionActionOpcode &&
                (IsPetPointResetMutationCandidate(
                     malformed,
                     packetBytes) ||
                 IsPetAppearanceChangeCandidate(
                     malformed,
                     packetBytes) ||
                 IsLegacyPetManagerUtilityCandidate(
                     malformed,
                     packetBytes) ||
                 IsLegacyPetBindCandidate(
                     malformed,
                     packetBytes))) {
                return LegacyPetCommandPacketKind::InvalidMutation;
            }
        }
        return LegacyPetCommandPacketKind::Unrelated;
    }
    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    if (opcode == LegacyNpcFunctionActionOpcode) {
        if (intent != nullptr) {
            *intent = LegacyPetCommandIntent{};
        }
        const auto appearanceChange =
            ClassifyPetAppearanceChange(
                bytes,
                packetBytes,
                intent);
        if (appearanceChange !=
            LegacyPetCommandPacketKind::Unrelated) {
            return appearanceChange;
        }
        const auto petBind =
            ClassifyLegacyPetBindPacket(bytes, packetBytes, intent);
        if (petBind != LegacyPetCommandPacketKind::Unrelated) {
            return petBind;
        }
        const auto utility =
            ClassifyLegacyPetManagerUtilityPacket(
                bytes,
                packetBytes,
                intent);
        if (utility != LegacyPetCommandPacketKind::Unrelated) {
            return utility;
        }
        const auto growthReset =
            ClassifyPetPointReset(
                bytes,
                packetBytes,
                LegacyPetGrowthResetMenuSubId,
                LegacyPetGrowthResetActionSubId,
                SecureLegacyCommandFamily::PetGrowthReset,
                intent);
        if (growthReset != LegacyPetCommandPacketKind::Unrelated) {
            return growthReset;
        }
        const auto basicSavvyReset =
            ClassifyPetPointReset(
                bytes,
                packetBytes,
                LegacyPetBasicSavvyResetMenuSubId,
                LegacyPetBasicSavvyResetActionSubId,
                SecureLegacyCommandFamily::PetBasicSavvyReset,
                intent);
        if (basicSavvyReset != LegacyPetCommandPacketKind::Unrelated) {
            return basicSavvyReset;
        }
        return IsPetPointResetMutationCandidate(bytes, packetBytes)
            ? LegacyPetCommandPacketKind::InvalidMutation
            : ClassifyPetSkillUnlearn(bytes, packetBytes, intent);
    }
    const auto alter =
        ClassifyLegacyPetAlterPacket(packet, packetBytes, intent);
    if (alter != LegacyPetCommandPacketKind::Unrelated) {
        return alter;
    }
    if (!IsPetOpcode(opcode)) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    *intent = LegacyPetCommandIntent{};

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

    if (opcode == LegacyPetOwnerMergeOpcode) {
        if (packetBytes != LegacyPetOwnerMergePacketBytes) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
        // Native owner Merge carries no client-selected item or pet. It is an
        // innate-talent toggle, so its retry identity and terminal result use
        // a dedicated command family rather than bag-item activation.
        intent->family =
            SecureLegacyCommandFamily::PetOwnerMergeToggle;
        intent->bytes[0] = 1;
        intent->bytes[1] = 1;
        return LegacyPetCommandPacketKind::Command;
    }

    if (opcode == LegacyPetToPetMergeOpcode) {
        if (packetBytes != LegacyPetToPetMergePacketBytes) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
        const std::uint32_t primaryPetId = Read32(bytes + 4);
        const std::uint32_t deputyPetId = Read32(bytes + 8);
        const std::uint32_t materialItemId = Read32(bytes + 12);
        const std::uint8_t quantity = bytes[16];
        const bool noMaterial = materialItemId == 0 && quantity == 0;
        const bool supportedMaterial =
            (materialItemId == LegacyMergedSpiritItemId ||
             materialItemId == LegacyFusedHarpyiaItemId) &&
            quantity >= 1 &&
            quantity <= LegacyMaximumPetMergeMaterialQuantity;
        if (primaryPetId == 0 || primaryPetId > 0x7FFF'FFFFU ||
            deputyPetId == 0 || deputyPetId > 0x7FFF'FFFFU ||
            primaryPetId == deputyPetId ||
            (!noMaterial && !supportedMaterial) ||
            bytes[17] != 0 || bytes[18] != 0 || bytes[19] != 0) {
            return LegacyPetCommandPacketKind::InvalidMutation;
        }
        intent->family = SecureLegacyCommandFamily::PetToPetMerge;
        intent->bytes[0] = 1;
        intent->bytes[1] = 1;
        Write32(intent->bytes + 2, primaryPetId);
        Write32(intent->bytes + 6, deputyPetId);
        Write32(intent->bytes + 10, materialItemId);
        intent->bytes[14] = quantity;
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
