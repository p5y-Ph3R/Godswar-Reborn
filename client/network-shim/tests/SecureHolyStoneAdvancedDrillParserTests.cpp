#include "SecureHolyStoneAdvancedDrillParserTests.h"

#include "SecureHolyStoneTestSupport.h"

namespace {

using namespace holy_stone_test;

void CheckAdvancedDrillBoundary(Checks* checks) {
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    LegacyHolyStoneCommand command{};
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::AdvancedDrill,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::UnrelatedOrNavigation &&
        !TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command),
        "Advanced Drill page transition did not remain untagged");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::AdvancedDrill,
        205,
        307);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::Commit &&
        TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command) &&
        command.action == LegacyHolyStoneAction::AdvancedDrill &&
        command.targetReference == 53 &&
        command.secondaryValue == 79,
        "Advanced Drill did not decode scratch arg 0, gear arg 6, and "
        "spell arg 7");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::AdvancedDrill,
        205,
        307);
    Write32(packet + 20, UINT32_MAX);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command),
        "Advanced Drill accepted an unset scratch argument");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::AdvancedDrill,
        205,
        307);
    Write32(packet + 20, 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command),
        "Advanced Drill accepted a non-zero scratch argument");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::AdvancedDrill,
        205,
        307);
    Write32(packet + 20 + 7 * 4, 205);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command),
        "Advanced Drill accepted one bag slot for both roles");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::AdvancedDrill,
        205,
        307);
    Write32(packet + 20 + 3 * 4, 0);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation,
        "Advanced Drill accepted an unexpected argument");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::AdvancedDrill,
        205,
        307);
    Write16(packet, LegacyHolyStoneActionPacketBytes - 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation,
        "Malformed Advanced Drill length did not fail closed");

    Write16(packet, LegacyHolyStoneActionPacketBytes);
    Write32(packet + 8, LegacyHolyStoneDialog + 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation,
        "Malformed Advanced Drill dialog did not fail closed");
}

} // namespace

int RunSecureHolyStoneAdvancedDrillParserTests() {
    Checks checks{};
    CheckAdvancedDrillBoundary(&checks);
    return checks.failures;
}
