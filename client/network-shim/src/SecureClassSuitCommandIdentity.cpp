#include "SecureClassSuitCommandIdentity.h"

#include "SecureLegacyCommandIdentity.h"

namespace godswar::network {
namespace {

std::uint16_t ReadUInt16Little(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

std::uint32_t ReadUInt32Little(
    const std::uint8_t* source) noexcept {
    return source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

bool IsClassSuitNpc(std::uint32_t npcId) noexcept {
    return npcId == LegacySpartaClassSuitNpc ||
        npcId == LegacyAthensClassSuitNpc;
}

bool TryReadAction(
    std::int32_t subId,
    LegacyClassSuitAction* action) noexcept {
    if (action == nullptr) {
        return false;
    }

    switch (subId) {
        case static_cast<std::int32_t>(
                LegacyClassSuitAction::ExchangeTierI):
            *action = LegacyClassSuitAction::ExchangeTierI;
            return true;
        case static_cast<std::int32_t>(
                LegacyClassSuitAction::AddAttribute):
            *action = LegacyClassSuitAction::AddAttribute;
            return true;
        case static_cast<std::int32_t>(
                LegacyClassSuitAction::DeleteAttribute):
            *action = LegacyClassSuitAction::DeleteAttribute;
            return true;
        case static_cast<std::int32_t>(
                LegacyClassSuitAction::ConvertToCommon):
            *action = LegacyClassSuitAction::ConvertToCommon;
            return true;
        case static_cast<std::int32_t>(
                LegacyClassSuitAction::UpgradeTierII):
            *action = LegacyClassSuitAction::UpgradeTierII;
            return true;
        case static_cast<std::int32_t>(
                LegacyClassSuitAction::UpgradeTierIII):
            *action = LegacyClassSuitAction::UpgradeTierIII;
            return true;
        case static_cast<std::int32_t>(
                LegacyClassSuitAction::UpgradeTierIV):
            *action = LegacyClassSuitAction::UpgradeTierIV;
            return true;
        default:
            return false;
    }
}

bool IsBagReference(std::int32_t reference) noexcept {
    return reference >= LegacyClassSuitBagReferenceMinimum &&
        reference <= LegacyClassSuitBagReferenceMaximum;
}

bool RequiresSecondaryItem(LegacyClassSuitAction action) noexcept {
    return action != LegacyClassSuitAction::ConvertToCommon;
}

bool RequiresTertiaryItem(LegacyClassSuitAction action) noexcept {
    return action == LegacyClassSuitAction::AddAttribute;
}

} // namespace

LegacyClassSuitPacketKind ClassifyLegacyClassSuitPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyClassSuitCommand* command) noexcept {
    if (packet == nullptr || packetBytes < 12) {
        return LegacyClassSuitPacketKind::UnrelatedOrNavigation;
    }

    const auto* bytes = static_cast<const std::uint8_t*>(packet);
    const auto opcode = ReadUInt16Little(bytes + 2);
    const auto npcId = ReadUInt32Little(bytes + 4);
    const auto dialog = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 8));
    if (opcode != LegacyNpcFunctionActionOpcode ||
        !IsClassSuitNpc(npcId) ||
        dialog != LegacyClassSuitDialog) {
        return LegacyClassSuitPacketKind::UnrelatedOrNavigation;
    }

    std::uint16_t parsedOpcode = 0;
    if (packetBytes != LegacyClassSuitActionPacketBytes ||
        !TryReadLegacyPacketHeader(
            packet, packetBytes, &parsedOpcode) ||
        parsedOpcode != LegacyNpcFunctionActionOpcode) {
        return LegacyClassSuitPacketKind::InvalidMutation;
    }

    const auto duplicateDialog = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 12));
    const auto subId = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 16));
    LegacyClassSuitAction action{};
    if (duplicateDialog != LegacyClassSuitDialog) {
        return LegacyClassSuitPacketKind::InvalidMutation;
    }
    if (!TryReadAction(subId, &action)) {
        return LegacyClassSuitPacketKind::UnrelatedOrNavigation;
    }

    std::int32_t arguments[LegacyClassSuitArgumentCount]{};
    bool hasValue = false;
    for (std::size_t index = 0;
         index < LegacyClassSuitArgumentCount;
         ++index) {
        arguments[index] = static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 20 + index * 4));
        if (arguments[index] != -1 &&
            !(index == LegacyClassSuitScratchArgument &&
                arguments[index] == 0)) {
            hasValue = true;
        }
    }
    if (!hasValue) {
        return LegacyClassSuitPacketKind::UnrelatedOrNavigation;
    }

    for (std::size_t index = 0;
         index < LegacyClassSuitArgumentCount;
         ++index) {
        const bool isExpected =
            index == LegacyClassSuitScratchArgument ||
            index == LegacyClassSuitGearArgument ||
            (RequiresSecondaryItem(action) &&
                index == LegacyClassSuitInsigniaArgument) ||
            (RequiresTertiaryItem(action) &&
                index == LegacyClassSuitThirdItemArgument);
        if (!isExpected && arguments[index] != -1) {
            return LegacyClassSuitPacketKind::InvalidMutation;
        }
    }
    const auto scratch =
        arguments[LegacyClassSuitScratchArgument];
    const auto gearReference =
        arguments[LegacyClassSuitGearArgument];
    const auto insigniaReference =
        arguments[LegacyClassSuitInsigniaArgument];
    const auto thirdItemReference =
        arguments[LegacyClassSuitThirdItemArgument];
    if ((scratch != -1 && scratch != 0) ||
        !IsBagReference(gearReference) ||
        (RequiresSecondaryItem(action) &&
            (!IsBagReference(insigniaReference) ||
                insigniaReference == gearReference)) ||
        (!RequiresSecondaryItem(action) &&
            insigniaReference != -1) ||
        (RequiresTertiaryItem(action) &&
            (!IsBagReference(thirdItemReference) ||
                thirdItemReference == gearReference ||
                thirdItemReference == insigniaReference)) ||
        (!RequiresTertiaryItem(action) &&
            thirdItemReference != -1)) {
        return LegacyClassSuitPacketKind::InvalidMutation;
    }

    if (command != nullptr) {
        command->action = action;
        command->npcId = npcId;
        command->gearBagSlot = static_cast<int>(
            gearReference - LegacyClassSuitBagReferenceMinimum);
        command->secondaryBagSlot = RequiresSecondaryItem(action)
            ? static_cast<int>(
                insigniaReference -
                LegacyClassSuitBagReferenceMinimum)
            : -1;
        command->tertiaryBagSlot = RequiresTertiaryItem(action)
            ? static_cast<int>(
                thirdItemReference -
                LegacyClassSuitBagReferenceMinimum)
            : -1;
    }
    return LegacyClassSuitPacketKind::Commit;
}

bool TryReadLegacyClassSuitCommand(
    const void* packet,
    std::size_t packetBytes,
    LegacyClassSuitCommand* command) noexcept {
    return command != nullptr &&
        ClassifyLegacyClassSuitPacket(
            packet, packetBytes, command) ==
            LegacyClassSuitPacketKind::Commit;
}

} // namespace godswar::network
