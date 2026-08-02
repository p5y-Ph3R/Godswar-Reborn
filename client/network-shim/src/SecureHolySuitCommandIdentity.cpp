#include "SecureHolySuitCommandIdentity.h"

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

bool IsHolySuitNpc(std::uint32_t npcId) noexcept {
    return npcId == LegacySpartaHolySuitNpc ||
        npcId == LegacyAthensHolySuitNpc;
}

bool IsBagReference(std::int32_t reference) noexcept {
    if (reference < LegacyHolySuitBagReferenceMinimum ||
        reference > LegacyHolySuitBagReferenceMaximum) {
        return false;
    }

    const auto bagPage =
        reference / LegacyHolySuitBagReferencePageStride;
    const auto pageSlot =
        reference % LegacyHolySuitBagReferencePageStride;
    return bagPage < LegacyHolySuitBagPageCount &&
        pageSlot < LegacyHolySuitBagSlotsPerPage;
}

bool TryReadAction(
    std::int32_t subId,
    LegacyHolySuitAction* action) noexcept {
    if (action == nullptr) {
        return false;
    }
    switch (subId) {
        case LegacyHolySuitStoreSubId:
            *action = LegacyHolySuitAction::StoreExperience;
            return true;
        case LegacyHolySuitTransferSubId:
            *action = LegacyHolySuitAction::TransferExperience;
            return true;
        case LegacyHolySuitConsumeWareSubId:
            *action = LegacyHolySuitAction::ConsumeWare;
            return true;
        case LegacyHolySuitTransformSubId:
            *action = LegacyHolySuitAction::TransformExperience;
            return true;
        default:
            return false;
    }
}

bool AreAllArgumentsUnset(
    const std::int32_t* arguments) noexcept {
    for (std::size_t index = 0;
         index < LegacyHolySuitArgumentCount;
         ++index) {
        if (arguments[index] != -1) {
            return false;
        }
    }
    return true;
}

bool HasOnlyExpectedArguments(
    const std::int32_t* arguments,
    std::size_t firstExpected,
    std::size_t secondExpected =
        LegacyHolySuitArgumentCount) noexcept {
    for (std::size_t index = 0;
         index < LegacyHolySuitArgumentCount;
         ++index) {
        if (index == LegacyHolySuitScratchArgument &&
            arguments[index] == 0) {
            continue;
        }
        if (index != firstExpected &&
            index != secondExpected &&
            arguments[index] != -1) {
            return false;
        }
    }
    return true;
}

} // namespace

LegacyHolySuitPacketKind ClassifyLegacyHolySuitPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolySuitCommand* command) noexcept {
    if (packet == nullptr ||
        packetBytes < 4 ||
        packetBytes > SecureLegacyMaximumPacketBytes) {
        return LegacyHolySuitPacketKind::UnrelatedOrNavigation;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    if (ReadUInt16Little(bytes + 2) !=
        LegacyNpcFunctionActionOpcode) {
        return LegacyHolySuitPacketKind::UnrelatedOrNavigation;
    }
    if (packetBytes < 20 ||
        !IsHolySuitNpc(ReadUInt32Little(bytes + 4))) {
        return LegacyHolySuitPacketKind::UnrelatedOrNavigation;
    }

    LegacyHolySuitAction action =
        LegacyHolySuitAction::StoreExperience;
    if (!TryReadAction(
            static_cast<std::int32_t>(
                ReadUInt32Little(bytes + 16)),
            &action)) {
        return LegacyHolySuitPacketKind::UnrelatedOrNavigation;
    }
    if (packetBytes != LegacyHolySuitActionPacketBytes ||
        ReadUInt16Little(bytes) != packetBytes ||
        static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 8)) !=
            LegacyHolySuitDialog ||
        static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 12)) !=
            LegacyHolySuitDialog) {
        return LegacyHolySuitPacketKind::InvalidMutation;
    }

    std::int32_t arguments[LegacyHolySuitArgumentCount]{};
    for (std::size_t index = 0;
         index < LegacyHolySuitArgumentCount;
         ++index) {
        arguments[index] = static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 20 + (index * 4)));
    }
    if (AreAllArgumentsUnset(arguments)) {
        return LegacyHolySuitPacketKind::UnrelatedOrNavigation;
    }

    LegacyHolySuitCommand parsed{};
    const auto amount = ReadUInt32Little(
        bytes + 20 + (LegacyHolySuitAmountArgument * 4));
    parsed.action = action;
    switch (action) {
        case LegacyHolySuitAction::StoreExperience:
            if (!IsBagReference(
                    arguments[LegacyHolySuitFirstItemArgument]) ||
                amount == 0 ||
                !HasOnlyExpectedArguments(
                    arguments,
                    LegacyHolySuitFirstItemArgument,
                    LegacyHolySuitAmountArgument)) {
                return LegacyHolySuitPacketKind::InvalidMutation;
            }
            parsed.primaryReference =
                arguments[LegacyHolySuitFirstItemArgument];
            parsed.amount = amount == LegacyHolySuitBlankAmount
                ? 0
                : amount;
            break;

        case LegacyHolySuitAction::TransferExperience:
        case LegacyHolySuitAction::ConsumeWare:
            if (!IsBagReference(
                    arguments[LegacyHolySuitFirstItemArgument]) ||
                !IsBagReference(
                    arguments[LegacyHolySuitSecondItemArgument]) ||
                arguments[LegacyHolySuitFirstItemArgument] ==
                    arguments[LegacyHolySuitSecondItemArgument] ||
                !HasOnlyExpectedArguments(
                    arguments,
                    LegacyHolySuitFirstItemArgument,
                    LegacyHolySuitSecondItemArgument)) {
                return LegacyHolySuitPacketKind::InvalidMutation;
            }
            parsed.primaryReference =
                arguments[LegacyHolySuitFirstItemArgument];
            parsed.secondaryReference =
                arguments[LegacyHolySuitSecondItemArgument];
            break;

        case LegacyHolySuitAction::TransformExperience:
            if (amount == 0 ||
                !HasOnlyExpectedArguments(
                    arguments,
                    LegacyHolySuitAmountArgument)) {
                return LegacyHolySuitPacketKind::InvalidMutation;
            }
            parsed.amount = amount == LegacyHolySuitBlankAmount
                ? LegacyHolySuitMouseOnlyTransformPrisms
                : amount;
            break;

        default:
            return LegacyHolySuitPacketKind::InvalidMutation;
    }

    if (command != nullptr) {
        *command = parsed;
    }
    return LegacyHolySuitPacketKind::Commit;
}

bool TryReadLegacyHolySuitCommand(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolySuitCommand* command) noexcept {
    return command != nullptr &&
        ClassifyLegacyHolySuitPacket(
            packet,
            packetBytes,
            command) == LegacyHolySuitPacketKind::Commit;
}

} // namespace godswar::network
