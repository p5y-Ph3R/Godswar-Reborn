#include "SecureHolyStoneCommandIdentity.h"

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

bool IsHolyStoneNpc(std::uint32_t npcId) noexcept {
    return npcId == LegacySpartaHolyStoneNpc ||
        npcId == LegacyAthensHolyStoneNpc;
}

bool TryDecodeBagReference(
    std::int32_t reference,
    int* bagSlot) noexcept {
    if (bagSlot == nullptr || reference < 0) {
        return false;
    }
    const auto page =
        reference / LegacyHolyStoneBagPageStride;
    const auto pageSlot =
        reference % LegacyHolyStoneBagPageStride;
    if (page >= LegacyHolyStoneBagPageCount ||
        pageSlot >= LegacyHolyStoneBagSlotsPerPage) {
        return false;
    }
    *bagSlot =
        page * LegacyHolyStoneBagSlotsPerPage + pageSlot;
    return true;
}

bool IsMutationSubId(std::int32_t subId) noexcept {
    return subId == LegacyHolyStoneMountSubId ||
        subId == LegacyHolyStoneRemoveSubId ||
        subId == LegacyHolyStoneDrillSubId ||
        subId == LegacyHolyStoneUpgradeSubId ||
        subId == LegacyHolyStoneImplementSpiritSubId ||
        subId == LegacyHolyStoneCombineSubId ||
        subId == LegacyHolyStoneAdvancedDrillSubId;
}

bool IsLegacyMutationAlias(std::int32_t subId) noexcept {
    // These are server response/page values. The old handler also accepted
    // them client-to-server as Mount, so a secure client must not be able to
    // use them to bypass the exact durable command boundary.
    return subId == 106 ||
        subId == 206 ||
        subId == 306 ||
        subId == 406 ||
        subId == 506;
}

bool AreAllArgumentsUnset(
    const std::int32_t* arguments) noexcept {
    for (std::size_t index = 0;
         index < LegacyHolyStoneArgumentCount;
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
        LegacyHolyStoneArgumentCount,
    std::size_t thirdExpected =
        LegacyHolyStoneArgumentCount,
    std::size_t fourthExpected =
        LegacyHolyStoneArgumentCount) noexcept {
    for (std::size_t index = 0;
         index < LegacyHolyStoneArgumentCount;
         ++index) {
        if (index != firstExpected &&
            index != secondExpected &&
            index != thirdExpected &&
            index != fourthExpected &&
            arguments[index] != -1) {
            return false;
        }
    }
    return true;
}

} // namespace

LegacyHolyStonePacketKind ClassifyLegacyHolyStonePacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolyStoneCommand* command) noexcept {
    if (packet == nullptr ||
        packetBytes < 4 ||
        packetBytes > SecureLegacyMaximumPacketBytes) {
        return LegacyHolyStonePacketKind::UnrelatedOrNavigation;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const auto opcode = ReadUInt16Little(bytes + 2);
    if (opcode != LegacyNpcFunctionActionOpcode) {
        return LegacyHolyStonePacketKind::UnrelatedOrNavigation;
    }
    if (packetBytes < 20 ||
        !IsHolyStoneNpc(ReadUInt32Little(bytes + 4))) {
        return LegacyHolyStonePacketKind::UnrelatedOrNavigation;
    }

    const auto subId = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 16));
    if (!IsMutationSubId(subId) &&
        !IsLegacyMutationAlias(subId)) {
        return LegacyHolyStonePacketKind::UnrelatedOrNavigation;
    }
    if (ReadUInt16Little(bytes) != packetBytes ||
        packetBytes != LegacyHolyStoneActionPacketBytes ||
        static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 8)) !=
            LegacyHolyStoneDialog ||
        static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 12)) !=
            LegacyHolyStoneDialog) {
        return LegacyHolyStonePacketKind::InvalidMutation;
    }

    std::int32_t arguments[LegacyHolyStoneArgumentCount]{};
    for (std::size_t index = 0;
         index < LegacyHolyStoneArgumentCount;
         ++index) {
        arguments[index] = static_cast<std::int32_t>(
            ReadUInt32Little(bytes + 20 + index * 4));
    }

    const auto npcId = ReadUInt32Little(bytes + 4);
    if (subId == LegacyHolyStoneCombineSubId) {
        LegacyHolyStoneCommand staged{};
        staged.action = LegacyHolyStoneAction::Combine;
        staged.npcId = npcId;
        if (AreAllArgumentsUnset(arguments)) {
            if (command != nullptr) {
                *command = staged;
            }
            return LegacyHolyStonePacketKind::Navigation;
        }

        if (!HasOnlyExpectedArguments(arguments, 6, 7, 8, 9)) {
            return LegacyHolyStonePacketKind::InvalidMutation;
        }
        for (std::size_t index = 0; index < 4; ++index) {
            if (!TryDecodeBagReference(
                    arguments[6 + index],
                    &staged.combinationBagSlots[index])) {
                return LegacyHolyStonePacketKind::InvalidMutation;
            }
            for (std::size_t prior = 0; prior < index; ++prior) {
                if (staged.combinationBagSlots[prior] ==
                    staged.combinationBagSlots[index]) {
                    return LegacyHolyStonePacketKind::InvalidMutation;
                }
            }
        }
        staged.combinationCount = 4;
        if (command != nullptr) {
            *command = staged;
        }
        return LegacyHolyStonePacketKind::StagedCommit;
    }
    if (subId == LegacyHolyStoneUpgradeSubId ||
        subId == LegacyHolyStoneImplementSpiritSubId) {
        LegacyHolyStoneCommand staged{};
        staged.action =
            subId == LegacyHolyStoneUpgradeSubId
            ? LegacyHolyStoneAction::Upgrade
            : LegacyHolyStoneAction::ImplementSpirit;
        staged.npcId = npcId;
        if (command != nullptr) {
            *command = staged;
        }
        // ItemBtn selections travel in bounded opcode-10193 packets. The
        // action scratch array has no captured role contract, so it must
        // never become the operation identity or authorize an item choice.
        return AreAllArgumentsUnset(arguments)
            ? LegacyHolyStonePacketKind::Navigation
            : LegacyHolyStonePacketKind::StagedCommit;
    }

    if ((subId == LegacyHolyStoneMountSubId ||
         subId == LegacyHolyStoneAdvancedDrillSubId) &&
        AreAllArgumentsUnset(arguments)) {
        return LegacyHolyStonePacketKind::UnrelatedOrNavigation;
    }
    if (IsLegacyMutationAlias(subId)) {
        return LegacyHolyStonePacketKind::InvalidMutation;
    }

    LegacyHolyStoneCommand parsed{};
    parsed.npcId = npcId;
    int targetBagSlot = -1;
    int materialBagSlot = -1;
    switch (subId) {
        case LegacyHolyStoneMountSubId:
            if (arguments[0] != 0 ||
                !TryDecodeBagReference(
                    arguments[6], &targetBagSlot) ||
                !TryDecodeBagReference(
                    arguments[7], &materialBagSlot) ||
                targetBagSlot == materialBagSlot ||
                !HasOnlyExpectedArguments(arguments, 0, 6, 7)) {
                return LegacyHolyStonePacketKind::InvalidMutation;
            }
            parsed.action = LegacyHolyStoneAction::Mount;
            parsed.targetReference = targetBagSlot;
            parsed.secondaryValue = materialBagSlot;
            break;
        case LegacyHolyStoneRemoveSubId:
            if (!TryDecodeBagReference(
                    arguments[6], &targetBagSlot) ||
                arguments[10] < 1 ||
                arguments[10] > 4 ||
                !HasOnlyExpectedArguments(arguments, 6, 10)) {
                return LegacyHolyStonePacketKind::InvalidMutation;
            }
            parsed.action = LegacyHolyStoneAction::Remove;
            parsed.targetReference = targetBagSlot;
            parsed.secondaryValue = arguments[10];
            break;
        case LegacyHolyStoneDrillSubId:
            if (!TryDecodeBagReference(
                    arguments[6], &targetBagSlot) ||
                !HasOnlyExpectedArguments(arguments, 6)) {
                return LegacyHolyStonePacketKind::InvalidMutation;
            }
            parsed.action = LegacyHolyStoneAction::Drill;
            parsed.targetReference = targetBagSlot;
            parsed.secondaryValue = -1;
            break;
        case LegacyHolyStoneAdvancedDrillSubId:
            if (arguments[0] != 0 ||
                !TryDecodeBagReference(
                    arguments[6], &targetBagSlot) ||
                !TryDecodeBagReference(
                    arguments[7], &materialBagSlot) ||
                targetBagSlot == materialBagSlot ||
                !HasOnlyExpectedArguments(arguments, 0, 6, 7)) {
                return LegacyHolyStonePacketKind::InvalidMutation;
            }
            parsed.action = LegacyHolyStoneAction::AdvancedDrill;
            parsed.targetReference = targetBagSlot;
            parsed.secondaryValue = materialBagSlot;
            break;
        default:
            return LegacyHolyStonePacketKind::UnrelatedOrNavigation;
    }

    if (command != nullptr) {
        *command = parsed;
    }
    return LegacyHolyStonePacketKind::Commit;
}

bool TryReadLegacyHolyStoneCommand(
    const void* packet,
    std::size_t packetBytes,
    LegacyHolyStoneCommand* command) noexcept {
    return command != nullptr &&
        ClassifyLegacyHolyStonePacket(
            packet,
            packetBytes,
            command) == LegacyHolyStonePacketKind::Commit;
}

} // namespace godswar::network
