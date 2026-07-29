#include "SecureEquipmentBagTransferTestSupport.h"

namespace {

using namespace equipment_bag_transfer_test;

void CheckCapturedGoldenVector(Checks* checks) {
    // captures/service-unequip-fixed.log:472
    const std::uint8_t packet[
        LegacyEquipmentBagTransferPacketBytes]{
        0x50, 0x00, 0x44, 0x27, 0x0F, 0x00, 0x00, 0x00,
        0x0A, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x01, 0x00,
        0x6B, 0xA3, 0xD4, 0xA5, 0x00, 0xC0, 0x3D, 0x44,
        0xEC, 0xF9, 0x1A, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x0F, 0x00, 0x00, 0x00, 0x8B, 0x75, 0x57, 0x00,
        0x00, 0x12, 0xD2, 0x04, 0x97, 0x75, 0x57, 0x00,
        0x4B, 0xA3, 0xD4, 0xA5, 0x50, 0xD9, 0x10, 0x14,
        0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00,
        0xC8, 0xFA, 0x1A, 0x00, 0x00, 0xFA, 0x1A, 0x00,
        0xA3, 0xA3, 0xD4, 0xA5, 0x50, 0xD9, 0x10, 0x14};
    int equipment = -1;
    int bag = -1;
    checks->Require(
        TryReadLegacyEquipmentBagTransfer(
            packet,
            sizeof(packet),
            &equipment,
            &bag) &&
            equipment == 10 &&
            bag == 1,
        "Equipment transfer rejected the captured golden vector");
}

void CheckExactShapeAndOpaqueBytes(Checks* checks) {
    std::uint8_t
        packet[LegacyEquipmentBagTransferPacketBytes]{};
    int equipment = -1;
    int bag = -1;
    BuildTransferPacket(packet, sizeof(packet), 0, 0, 0x11);
    checks->Require(
        TryReadLegacyEquipmentBagTransfer(
            packet,
            sizeof(packet),
            &equipment,
            &bag) &&
            equipment == 0 &&
            bag == 0,
        "Equipment transfer rejected its lower boundaries");

    BuildTransferPacket(packet, sizeof(packet), 20, 95, 0x22);
    packet[4] ^= 0xFFU;
    packet[16] ^= 0x5AU;
    packet[79] ^= 0xA5U;
    checks->Require(
        TryReadLegacyEquipmentBagTransfer(
            packet,
            sizeof(packet),
            &equipment,
            &bag) &&
            equipment == 20 &&
            bag == 95,
        "Equipment transfer trusted opaque stock bytes");
}

void CheckRejectedShapes(Checks* checks) {
    std::uint8_t
        packet[LegacyEquipmentBagTransferPacketBytes + 1]{};
    int equipment = 7;
    int bag = 8;
    BuildTransferPacket(packet, 80, 10, 55, 0);
    Write16(packet, 79);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            80,
            &equipment,
            &bag),
        "Equipment transfer accepted a mismatched length");
    Write16(packet, 80);
    Write16(packet + 2, 10051);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            80,
            &equipment,
            &bag),
        "Equipment transfer accepted another opcode");

    BuildTransferPacket(packet, 79, 10, 55, 0);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            79,
            &equipment,
            &bag),
        "Equipment transfer accepted 79 bytes");
    BuildTransferPacket(packet, 81, 10, 55, 0);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            81,
            &equipment,
            &bag),
        "Equipment transfer accepted 81 bytes");

    BuildTransferPacket(packet, 80, 10, 55, 0);
    Write16(packet + 8, 21);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            80,
            &equipment,
            &bag),
        "Equipment transfer accepted equipment slot 21");
    BuildTransferPacket(packet, 80, 10, 55, 0);
    Write16(packet + 10, 0);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            80,
            &equipment,
            &bag),
        "Equipment transfer accepted a missing sentinel");
    BuildTransferPacket(packet, 80, 10, 55, 0);
    Write16(packet + 12, 4);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            80,
            &equipment,
            &bag),
        "Equipment transfer accepted bag page four");
    BuildTransferPacket(packet, 80, 10, 55, 0);
    Write16(packet + 14, 24);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            80,
            &equipment,
            &bag),
        "Equipment transfer accepted bag index 24");

    BuildTransferPacket(packet, 80, 10, 55, 0);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            packet,
            79,
            &equipment,
            &bag) &&
            !TryReadLegacyEquipmentBagTransfer(
                packet,
                80,
                nullptr,
                &bag) &&
            !TryReadLegacyEquipmentBagTransfer(
                packet,
                80,
                &equipment,
                nullptr),
        "Equipment transfer accepted truncation or null output");
}

void CheckOpcode10052Separation(Checks* checks) {
    std::uint8_t
        transfer[LegacyEquipmentBagTransferPacketBytes]{};
    BuildTransferPacket(
        transfer,
        sizeof(transfer),
        10,
        55,
        0x33);
    int source = -1;
    int destination = -1;
    checks->Require(
        !TryReadLegacyKitBagItemMove(
            transfer,
            sizeof(transfer),
            &source,
            &destination),
        "Equipment transfer overlapped kit-bag move");

    std::uint8_t move[
        LegacyKitBagItemMoveDetailedPacketBytes]{};
    std::memset(move, 0x44, sizeof(move));
    Write16(move, sizeof(move));
    Write16(move + 2, LegacyStorageItemOpcode);
    Write16(move + 8, 0);
    Write16(move + 10, 10);
    Write16(move + 12, 2);
    Write16(move + 14, 7);
    int equipment = -1;
    int bag = -1;
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            move,
            sizeof(move),
            &equipment,
            &bag) &&
            TryReadLegacyKitBagItemMove(
                move,
                sizeof(move),
                &source,
                &destination) &&
            source == 10 &&
            destination == 55,
        "Kit-bag move overlapped equipment transfer");

    std::uint8_t
        deleted[LegacyKitBagItemDeletePacketBytes]{};
    Write16(deleted, sizeof(deleted));
    Write16(deleted + 2, LegacyStorageItemOpcode);
    Write16(deleted + 8, 0);
    Write16(deleted + 10, 1);
    Write16(deleted + 12, UINT16_MAX);
    Write16(deleted + 14, UINT16_MAX);
    checks->Require(
        !TryReadLegacyEquipmentBagTransfer(
            deleted,
            sizeof(deleted),
            &equipment,
            &bag),
        "Kit-bag delete overlapped equipment transfer");
}

} // namespace

int RunSecureEquipmentBagTransferParserTests() {
    Checks checks{};
    CheckCapturedGoldenVector(&checks);
    CheckExactShapeAndOpaqueBytes(&checks);
    CheckRejectedShapes(&checks);
    CheckOpcode10052Separation(&checks);
    return checks.failures;
}
