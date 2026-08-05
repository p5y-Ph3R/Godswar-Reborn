#include "SecurePendingOperationRegistry.h"

namespace godswar::network {

SecureOperationRegistryResult
SecurePendingOperationRegistry::DescribeHolyEquipmentPacket(
    const void* packet,
    std::size_t packetBytes,
    std::uint64_t now,
    LegacyPacketDescriptor* descriptor,
    bool* recognized) noexcept {
    if (recognized == nullptr) {
        return SecureOperationRegistryResult::InvalidPacket;
    }
    *recognized = false;

    LegacyHolySuitCommand holySuit{};
    switch (ClassifyLegacyHolySuitPacket(
                packet, packetBytes, &holySuit)) {
        case LegacyHolySuitPacketKind::Commit:
            *recognized = true;
            return DescribeHolySuitCommand(
                holySuit, now, descriptor);
        case LegacyHolySuitPacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        default:
            break;
    }

    LegacyHolyStoneCommand holyStone{};
    switch (ClassifyLegacyHolyStonePacket(
                packet, packetBytes, &holyStone)) {
        case LegacyHolyStonePacketKind::Commit:
            *recognized = true;
            return DescribeHolyStoneCommand(
                holyStone, now, descriptor);
        case LegacyHolyStonePacketKind::Navigation:
            *recognized = true;
            switch (holyStone.action) {
                case LegacyHolyStoneAction::Combine:
                    return DescribeHolyStoneCombineNavigation(
                        holyStone, now, descriptor);
                case LegacyHolyStoneAction::Upgrade:
                    return DescribeHolyStoneUpgradeNavigation(
                        holyStone, now, descriptor);
                case LegacyHolyStoneAction::ImplementSpirit:
                    return DescribeHolyStoneImplementNavigation(
                        holyStone, now, descriptor);
                default:
                    return SecureOperationRegistryResult::InvalidPacket;
            }
        case LegacyHolyStonePacketKind::StagedCommit:
            *recognized = true;
            switch (holyStone.action) {
                case LegacyHolyStoneAction::Combine:
                    return DescribeHolyStoneCombineCommit(
                        holyStone, now, descriptor);
                case LegacyHolyStoneAction::Upgrade:
                    return DescribeHolyStoneUpgradeCommit(
                        holyStone, now, descriptor);
                case LegacyHolyStoneAction::ImplementSpirit:
                    return DescribeHolyStoneImplementCommit(
                        holyStone, now, descriptor);
                default:
                    return SecureOperationRegistryResult::InvalidPacket;
            }
        case LegacyHolyStonePacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        default:
            return SecureOperationRegistryResult::Success;
    }
}

} // namespace godswar::network
