#include "SecurePetManagerUtilityIdentity.h"

#include "SecureLegacyCommandIdentity.h"

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

bool IsManagerNpc(std::uint32_t npcId) noexcept {
    return npcId == LegacySpartaPetManagerNpc ||
        npcId == LegacySpartaSourcePetManagerNpc ||
        npcId == LegacyAthensPetManagerNpc;
}

bool IsScratch(std::size_t index) noexcept {
    return index >= LegacyPetManagerScratchArgumentFirst &&
        index < LegacyPetManagerScratchArgumentFirst +
            LegacyPetManagerScratchArgumentCount;
}

bool HasExactPadding(
    const std::uint8_t* bytes,
    std::uint32_t argumentZero) noexcept {
    if (Read32(bytes + 20) != argumentZero) {
        return false;
    }
    for (std::size_t index = 1;
         index < LegacyPetManagerArgumentCount;
         ++index) {
        // CNpcFun's shared fixed-frame sender copies its numeric-control
        // cache into arguments 10..12 on all of these pages. The values can
        // be zero, stale coordinates, or other 32-bit scratch; they never
        // select the pet, item, or operation.
        if (!IsScratch(index) &&
            Read32(bytes + 20 + index * 4) != 0xFFFF'FFFFU) {
            return false;
        }
    }
    return true;
}

bool ResolveOperation(
    const std::uint8_t* bytes,
    LegacyPetManagerUtilityOperation* operation,
    bool* navigation) noexcept {
    if (operation == nullptr || navigation == nullptr) {
        return false;
    }
    *navigation = false;
    const auto subId =
        static_cast<std::int32_t>(Read32(bytes + 16));
    const std::uint32_t argumentZero = Read32(bytes + 20);
    switch (subId) {
        case LegacyPetGrowthCheckMenuSubId:
            if (argumentZero == 0xFFFF'FFFFU) {
                *navigation = true;
                return true;
            }
            *operation = LegacyPetManagerUtilityOperation::CheckGrowth;
            return argumentZero == static_cast<std::uint32_t>(
                LegacyPetGrowthCheckActionSubId);
        case LegacyPetSealMenuSubId:
            if (argumentZero == 0xFFFF'FFFFU) {
                *navigation = true;
                return true;
            }
            *operation = LegacyPetManagerUtilityOperation::Seal;
            return argumentZero == static_cast<std::uint32_t>(
                LegacyPetSealActionSubId);
        case LegacyPetCallClaimSubId:
            *operation = LegacyPetManagerUtilityOperation::ClaimPetCall;
            return argumentZero == 0xFFFF'FFFFU;
        case LegacyPetMergeClaimSubId:
            *operation = LegacyPetManagerUtilityOperation::ClaimMerge;
            return argumentZero == 0xFFFF'FFFFU;
        case LegacyPetGenderMenuSubId:
            if (argumentZero == 0xFFFF'FFFFU) {
                *navigation = true;
                return true;
            }
            *operation = LegacyPetManagerUtilityOperation::ChangeGender;
            return argumentZero == static_cast<std::uint32_t>(
                LegacyPetGenderConfirmArgument);
        default:
            return false;
    }
}

} // namespace

bool IsLegacyPetManagerUtilityCandidate(
    const void* packet,
    std::size_t packetBytes) noexcept {
    if (packet == nullptr || packetBytes < 24) {
        return false;
    }
    const auto* bytes = static_cast<const std::uint8_t*>(packet);
    if (Read16(bytes + 2) != LegacyNpcFunctionActionOpcode ||
        !IsManagerNpc(Read32(bytes + 4)) ||
        static_cast<std::int32_t>(Read32(bytes + 8)) !=
            LegacyPetManagerDialog) {
        return false;
    }
    const auto subId =
        static_cast<std::int32_t>(Read32(bytes + 16));
    const std::uint32_t argumentZero = Read32(bytes + 20);
    return subId == LegacyPetCallClaimSubId ||
        subId == LegacyPetMergeClaimSubId ||
        ((subId == LegacyPetGrowthCheckMenuSubId ||
          subId == LegacyPetSealMenuSubId ||
          subId == LegacyPetGenderMenuSubId) &&
         argumentZero != 0xFFFF'FFFFU);
}

LegacyPetCommandPacketKind ClassifyLegacyPetManagerUtilityPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept {
    if (packet == nullptr || packetBytes < 20) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    const auto* bytes = static_cast<const std::uint8_t*>(packet);
    if (!IsManagerNpc(Read32(bytes + 4)) ||
        static_cast<std::int32_t>(Read32(bytes + 8)) !=
            LegacyPetManagerDialog) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    const auto subId =
        static_cast<std::int32_t>(Read32(bytes + 16));
    if (subId != LegacyPetGrowthCheckMenuSubId &&
        subId != LegacyPetSealMenuSubId &&
        subId != LegacyPetCallClaimSubId &&
        subId != LegacyPetMergeClaimSubId &&
        subId != LegacyPetGenderMenuSubId) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (packetBytes != LegacyPetManagerActionPacketBytes ||
        Read16(bytes) != packetBytes ||
        Read16(bytes + 2) != LegacyNpcFunctionActionOpcode ||
        static_cast<std::int32_t>(Read32(bytes + 12)) !=
            LegacyPetManagerDialog) {
        return IsLegacyPetManagerUtilityCandidate(packet, packetBytes)
            ? LegacyPetCommandPacketKind::InvalidMutation
            : LegacyPetCommandPacketKind::Unrelated;
    }

    LegacyPetManagerUtilityOperation operation{};
    bool navigation = false;
    if (!ResolveOperation(bytes, &operation, &navigation)) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    const auto expectedArgument = navigation ||
            operation == LegacyPetManagerUtilityOperation::ClaimPetCall ||
            operation == LegacyPetManagerUtilityOperation::ClaimMerge
        ? 0xFFFF'FFFFU
        : operation == LegacyPetManagerUtilityOperation::CheckGrowth
            ? static_cast<std::uint32_t>(
                LegacyPetGrowthCheckActionSubId)
            : operation == LegacyPetManagerUtilityOperation::Seal
                ? static_cast<std::uint32_t>(
                    LegacyPetSealActionSubId)
                : static_cast<std::uint32_t>(
                    LegacyPetGenderConfirmArgument);
    if (!HasExactPadding(bytes, expectedArgument)) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }
    if (navigation) {
        return LegacyPetCommandPacketKind::Unrelated;
    }
    if (intent == nullptr) {
        return LegacyPetCommandPacketKind::InvalidMutation;
    }

    intent->family = SecureLegacyCommandFamily::PetManagerUtility;
    intent->bytes[0] = 1;
    intent->bytes[1] = static_cast<std::uint8_t>(operation);
    return LegacyPetCommandPacketKind::Command;
}

} // namespace godswar::network
