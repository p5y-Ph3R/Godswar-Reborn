#include "SecureZodiacSkillGridSelectionTestSupport.h"

namespace {

using namespace zodiac_selection_test;

void CheckNativeVectors(Checks* checks) {
    const std::uint8_t packet[LegacyZodiacPacketBytes]{
        0x18, 0x00, 0x39, 0x28, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0x00, 0x66, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x49, 0x27, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
    LegacyZodiacSkillGridSelectionCommand command{};
    checks->Require(
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyZodiacSkillGridSelectionPacketKind::Commit &&
            command.gridIndex == 1 &&
            command.selectedSkillKind == 10'057,
        "Zodiac selection rejected the native SID-102 vector");

    std::uint8_t mutablePacket[LegacyZodiacPacketBytes]{};
    BuildSelectionPacket(mutablePacket, 4, 20'053);
    checks->Require(
        TryReadLegacyZodiacSkillGridSelection(
            mutablePacket,
            sizeof(mutablePacket),
            &command) &&
            command.gridIndex == 4 &&
            command.selectedSkillKind == 20'053,
        "Zodiac selection rejected a second-row skill");

    BuildSelectionPacket(mutablePacket, 15, -1);
    checks->Require(
        TryReadLegacyZodiacSkillGridSelection(
            mutablePacket,
            sizeof(mutablePacket),
            &command) &&
            command.selectedSkillKind == -1,
        "Zodiac selection rejected the native clear sentinel");
}

void CheckCompatibilityAndUnrelated(Checks* checks) {
    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    LegacyZodiacSkillGridSelectionCommand command{};
    BuildSelectionPacket(
        packet,
        8,
        10'057,
        LegacyZodiacCompatibilityModule,
        0xAABBCCDDU);
    checks->Require(
        TryReadLegacyZodiacSkillGridSelection(
            packet,
            sizeof(packet),
            &command),
        "Zodiac selection rejected module-zero compatibility");

    Write16(packet + 10, LegacyZodiacSkillGridUpgradeSid);
    checks->Require(
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            packet,
            sizeof(packet),
            nullptr) ==
            LegacyZodiacSkillGridSelectionPacketKind::Unrelated,
        "Zodiac upgrade was mistaken for a selection");
    Write16(packet + 2, LegacyZodiacOpcode - 1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            packet,
            sizeof(packet),
            nullptr) ==
            LegacyZodiacSkillGridSelectionPacketKind::Unrelated,
        "Another opcode was mistaken for a Zodiac selection");
}

void CheckMalformedFailsClosed(Checks* checks) {
    std::uint8_t packet[LegacyZodiacPacketBytes + 1]{};
    BuildSelectionPacket(packet, 0, 10'057);
    Write16(packet, LegacyZodiacPacketBytes - 1);
    checks->Require(
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            packet,
            LegacyZodiacPacketBytes,
            nullptr) ==
            LegacyZodiacSkillGridSelectionPacketKind::InvalidMutation,
        "Zodiac selection accepted a mismatched length");

    BuildSelectionPacket(packet, 0, 10'057);
    checks->Require(
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            packet,
            LegacyZodiacPacketBytes - 1,
            nullptr) ==
                LegacyZodiacSkillGridSelectionPacketKind::InvalidMutation &&
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            packet,
            LegacyZodiacPacketBytes + 1,
            nullptr) ==
                LegacyZodiacSkillGridSelectionPacketKind::InvalidMutation,
        "Zodiac selection accepted truncation or trailing bytes");

    const struct {
        int grid;
        int kind;
        std::uint16_t module;
        int tail;
    } invalid[] {
        {-1, 10'057, LegacyZodiacNativeModule, 0},
        {16, 10'057, LegacyZodiacNativeModule, 0},
        {0, 9'999, LegacyZodiacNativeModule, 0},
        {0, 30'000, LegacyZodiacNativeModule, 0},
        {0, 20'053, LegacyZodiacNativeModule, 0},
        {4, 10'057, LegacyZodiacNativeModule, 0},
        {0, 10'057, 1, 0},
        {0, 10'057, LegacyZodiacNativeModule, 1},
    };
    for (const auto& value : invalid) {
        BuildSelectionPacket(
            packet,
            value.grid,
            value.kind,
            value.module,
            0,
            value.tail);
        checks->Require(
            ClassifyLegacyZodiacSkillGridSelectionPacket(
                packet,
                LegacyZodiacPacketBytes,
                nullptr) ==
                LegacyZodiacSkillGridSelectionPacketKind::InvalidMutation,
            "Zodiac selection accepted an invalid mutation field");
    }
}

void CheckNullContracts(Checks* checks) {
    LegacyZodiacSkillGridSelectionCommand command{};
    checks->Require(
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            nullptr,
            0,
            &command) ==
                LegacyZodiacSkillGridSelectionPacketKind::Unrelated &&
        !TryReadLegacyZodiacSkillGridSelection(
            nullptr,
            0,
            &command),
        "Zodiac selection violated its null input contract");

    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    BuildSelectionPacket(packet, 0, 10'057);
    checks->Require(
        !TryReadLegacyZodiacSkillGridSelection(
            packet,
            sizeof(packet),
            nullptr),
        "Zodiac selection wrote through a null command");
}

} // namespace

int RunSecureZodiacSkillGridSelectionParserTests() {
    Checks checks{};
    CheckNativeVectors(&checks);
    CheckCompatibilityAndUnrelated(&checks);
    CheckMalformedFailsClosed(&checks);
    CheckNullContracts(&checks);
    return checks.failures;
}
