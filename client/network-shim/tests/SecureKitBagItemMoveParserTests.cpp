#include "SecureKitBagItemMoveTestSupport.h"

namespace {

using namespace kit_bag_move_test;

void CheckExactCompactAndDetailed(Checks* checks) {
    std::uint8_t compact[
        LegacyKitBagItemMoveCompactPacketBytes]{};
    BuildMovePacket(compact, sizeof(compact), 0, 95, 0);
    int source = -1;
    int destination = -1;
    checks->Require(
        TryReadLegacyKitBagItemMove(
            compact,
            sizeof(compact),
            &source,
            &destination) &&
            source == 0 &&
            destination == 95,
        "Compact kit-bag move parser rejected boundary slots");

    std::uint8_t detailed[
        LegacyKitBagItemMoveDetailedPacketBytes]{};
    BuildMovePacket(detailed, sizeof(detailed), 25, 70, 0x41);
    checks->Require(
        TryReadLegacyKitBagItemMove(
            detailed,
            sizeof(detailed),
            &source,
            &destination) &&
            source == 25 &&
            destination == 70,
        "Detailed kit-bag move parser rejected valid coordinates");
    detailed[16] ^= 0xFFU;
    detailed[79] ^= 0x7FU;
    checks->Require(
        TryReadLegacyKitBagItemMove(
            detailed,
            sizeof(detailed),
            &source,
            &destination),
        "Detailed kit-bag move parser trusted its opaque tail");
}

void CheckRejectedShapes(Checks* checks) {
    std::uint8_t compact[
        LegacyKitBagItemMoveCompactPacketBytes]{};
    int source = 30;
    int destination = 31;
    BuildMovePacket(compact, sizeof(compact), 2, 3, 0);
    Write16(compact + 16, 0);
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            compact,
            sizeof(compact),
            &source,
            &destination),
        "Compact kit-bag move accepted a non-sentinel tail");

    BuildMovePacket(compact, sizeof(compact), 2, 3, 0);
    Write16(compact, 19);
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            compact,
            sizeof(compact),
            &source,
            &destination),
        "Kit-bag move accepted a mismatched declared length");
    Write16(compact, 20);
    Write16(compact + 2, 10051);
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            compact,
            sizeof(compact),
            &source,
            &destination),
        "Kit-bag move accepted another opcode");

    std::uint8_t wrongDetailed[81]{};
    BuildMovePacket(wrongDetailed, 79, 2, 3, 0);
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            wrongDetailed,
            79,
            &source,
            &destination),
        "Kit-bag move accepted a 79-byte packet");
    BuildMovePacket(wrongDetailed, 81, 2, 3, 0);
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            wrongDetailed,
            81,
            &source,
            &destination),
        "Kit-bag move accepted an 81-byte packet");

    const std::size_t coordinateOffsets[]{8, 10, 12, 14};
    const std::uint16_t invalidValues[]{4, 24, 4, 24};
    for (std::size_t index = 0; index < 4; ++index) {
        BuildMovePacket(compact, sizeof(compact), 2, 3, 0);
        Write16(
            compact + coordinateOffsets[index],
            invalidValues[index]);
        checks->Require(
            !TryReadLegacyKitBagItemMove(
                compact,
                sizeof(compact),
                &source,
                &destination),
            "Kit-bag move accepted an out-of-range coordinate");
    }

    BuildMovePacket(compact, sizeof(compact), 24, 24, 0);
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            compact,
            sizeof(compact),
            &source,
            &destination),
        "Kit-bag move accepted identical source and destination");
    BuildMovePacket(compact, sizeof(compact), 2, 3, 0);
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            compact,
            sizeof(compact) - 1,
            &source,
            &destination) &&
            !TryReadLegacyKitBagItemMove(
                compact,
                sizeof(compact),
                nullptr,
                &destination) &&
            !TryReadLegacyKitBagItemMove(
                compact,
                sizeof(compact),
                &source,
                nullptr),
        "Kit-bag move accepted a truncated or outputless packet");
}

void CheckCompatibilityShapes(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    std::uint8_t packet[24]{};
    Write16(packet, sizeof(packet));
    Write16(packet + 2, LegacyStorageItemOpcode);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation,
        "Another StorageItem shape poisoned compatibility routing");

    std::uint8_t compact[
        LegacyKitBagItemMoveCompactPacketBytes]{};
    BuildMovePacket(compact, sizeof(compact), 1, 1, 0);
    checks->Require(
        registry.DescribePacket(
            compact,
            sizeof(compact),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation,
        "A same-slot StorageItem packet poisoned compatibility routing");
    BuildMovePacket(compact, sizeof(compact), 1, 2, 0);
    Write16(compact + 18, 0);
    checks->Require(
        registry.DescribePacket(
            compact,
            sizeof(compact),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation,
        "A non-move compact shape poisoned compatibility routing");
}

} // namespace

int RunSecureKitBagItemMoveParserTests() {
    Checks checks{};
    CheckExactCompactAndDetailed(&checks);
    CheckRejectedShapes(&checks);
    CheckCompatibilityShapes(&checks);
    return checks.failures;
}
