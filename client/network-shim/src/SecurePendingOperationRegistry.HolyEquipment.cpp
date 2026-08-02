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
        case LegacyHolyStonePacketKind::InvalidMutation:
            *recognized = true;
            return SecureOperationRegistryResult::InvalidPacket;
        default:
            return SecureOperationRegistryResult::Success;
    }
}

} // namespace godswar::network
