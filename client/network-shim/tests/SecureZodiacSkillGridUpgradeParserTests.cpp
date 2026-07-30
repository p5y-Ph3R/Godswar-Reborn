#include "SecureZodiacSkillGridUpgradeTestSupport.h"

namespace {

using namespace zodiac_upgrade_test;

void CheckNativeGoldenVector(Checks* checks) {
    const std::uint8_t packet[LegacyZodiacPacketBytes]{
        0x18, 0x00, 0x39, 0x28, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0x00, 0x65, 0x00, 0x01, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00};
    LegacyZodiacSkillGridUpgradeCommand command{};
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyZodiacSkillGridUpgradePacketKind::Commit &&
            command.gridIndex == 1,
        "Zodiac upgrade rejected its native SID-101 vector");
}

void CheckBoundsAndCompatibility(Checks* checks) {
    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    LegacyZodiacSkillGridUpgradeCommand command{};
    BuildUpgradePacket(packet, LegacyZodiacSkillGridMinimum);
    checks->Require(
        TryReadLegacyZodiacSkillGridUpgrade(
            packet,
            sizeof(packet),
            &command) &&
            command.gridIndex == LegacyZodiacSkillGridMinimum,
        "Zodiac upgrade rejected grid zero");

    BuildUpgradePacket(
        packet,
        LegacyZodiacSkillGridMaximum,
        LegacyZodiacCompatibilityModule,
        0xAABBCCDDU);
    checks->Require(
        TryReadLegacyZodiacSkillGridUpgrade(
            packet,
            sizeof(packet),
            &command) &&
            command.gridIndex == LegacyZodiacSkillGridMaximum,
        "Zodiac upgrade rejected module-zero compatibility");
}

void CheckUnrelatedZodiacSids(Checks* checks) {
    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    BuildUpgradePacket(packet, 1);
    Write16(packet + 8, 0);
    Write16(packet + 10, 100);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            sizeof(packet),
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::Unrelated,
        "Zodiac activation was mistaken for an upgrade");

    Write16(packet + 8, LegacyZodiacNativeModule);
    Write16(packet + 10, 102);
    Write32(packet + 16, 10'057);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            sizeof(packet),
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::Unrelated,
        "Zodiac skill selection was mistaken for an upgrade");

    Write16(packet + 2, LegacyZodiacOpcode - 1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            sizeof(packet),
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::Unrelated,
        "Another opcode was mistaken for a Zodiac upgrade");
}

void CheckMalformedUpgradeFailsClosed(Checks* checks) {
    std::uint8_t packet[LegacyZodiacPacketBytes + 1]{};
    BuildUpgradePacket(packet, 1);
    Write16(packet, LegacyZodiacPacketBytes - 1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted a mismatched declared length");

    BuildUpgradePacket(packet, 1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes - 1,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted a truncated packet");
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes + 1,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted an oversized packet");

    BuildUpgradePacket(packet, -1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted a negative grid");
    BuildUpgradePacket(packet, LegacyZodiacSkillGridMaximum + 1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted grid sixteen");

    BuildUpgradePacket(packet, 1, 1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted an unknown module");
    BuildUpgradePacket(
        packet,
        1,
        LegacyZodiacNativeModule,
        0,
        0);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted a changed placeholder");
    BuildUpgradePacket(
        packet,
        1,
        LegacyZodiacNativeModule,
        0,
        -1,
        1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            LegacyZodiacPacketBytes,
            nullptr) ==
            LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation,
        "Zodiac upgrade accepted a nonzero tail");
}

void CheckNullContracts(Checks* checks) {
    LegacyZodiacSkillGridUpgradeCommand command{};
    checks->Require(
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            nullptr,
            0,
            &command) ==
                LegacyZodiacSkillGridUpgradePacketKind::Unrelated &&
            !TryReadLegacyZodiacSkillGridUpgrade(
                nullptr,
                0,
                &command),
        "Zodiac upgrade violated its null input contract");

    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    BuildUpgradePacket(packet, 1);
    checks->Require(
        !TryReadLegacyZodiacSkillGridUpgrade(
            packet,
            sizeof(packet),
            nullptr),
        "Zodiac upgrade wrote through a null command");
}

} // namespace

int RunSecureZodiacSkillGridUpgradeParserTests() {
    Checks checks{};
    CheckNativeGoldenVector(&checks);
    CheckBoundsAndCompatibility(&checks);
    CheckUnrelatedZodiacSids(&checks);
    CheckMalformedUpgradeFailsClosed(&checks);
    CheckNullContracts(&checks);
    return checks.failures;
}
