#include "SecureHolyStoneTestSupport.h"

#include <initializer_list>

namespace {

using namespace holy_stone_test;

int HexNibble(char value) {
    if (value >= '0' && value <= '9') {
        return value - '0';
    }
    if (value >= 'A' && value <= 'F') {
        return value - 'A' + 10;
    }
    return -1;
}

bool DecodePacketHex(
    const char* source,
    std::uint8_t* destination) {
    if (source == nullptr || destination == nullptr ||
        std::strlen(source) !=
            LegacyHolyStoneActionPacketBytes * 2) {
        return false;
    }
    for (std::size_t index = 0;
         index < LegacyHolyStoneActionPacketBytes;
         ++index) {
        const int high = HexNibble(source[index * 2]);
        const int low = HexNibble(source[index * 2 + 1]);
        if (high < 0 || low < 0) {
            return false;
        }
        destination[index] = static_cast<std::uint8_t>(
            (high << 4) | low);
    }
    return true;
}

void CheckCapturedGoldenVectors(Checks* checks) {
    struct GoldenVector final {
        const char* hex;
        LegacyHolyStoneAction action;
        int target;
        int secondary;
    };
    const GoldenVector vectors[]{
        {
            "5C005527DB1300001E0000001E000000C9000000"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "CD000000FFFFFFFFFFFFFFFFFFFFFFFF01000000"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "FFFFFFFF",
            LegacyHolyStoneAction::Remove,
            53,
            1,
        },
        {
            "5C005527DB1300001E0000001E000000C9000000"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "70000000FFFFFFFFFFFFFFFFFFFFFFFF01000000"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "FFFFFFFF",
            LegacyHolyStoneAction::Remove,
            36,
            1,
        },
        {
            "5C005527DB1300001E0000001E00000065000000"
            "00000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "CD0000006B000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            LegacyHolyStoneAction::Mount,
            53,
            31,
        },
        {
            "5C005527DB1300001E0000001E00000065000000"
            "00000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "700000006D000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            LegacyHolyStoneAction::Mount,
            36,
            33,
        },
        {
            "5C005527DB1300001E0000001E0000002D010000"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "6B000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            LegacyHolyStoneAction::Drill,
            31,
            -1,
        },
        {
            // Exact retail-client packet captured on 2026-08-04. Page zero,
            // slot 16 is wire reference 16 and canonical linear slot 16.
            "5C005527DB1300001E0000001E0000002D010000"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "10000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            LegacyHolyStoneAction::Drill,
            16,
            -1,
        },
    };

    for (const auto& vector : vectors) {
        std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
        LegacyHolyStoneCommand command{};
        checks->Require(
            DecodePacketHex(vector.hex, packet) &&
                ClassifyLegacyHolyStonePacket(
                    packet,
                    sizeof(packet),
                    &command) ==
                    LegacyHolyStonePacketKind::Commit &&
                TryReadLegacyHolyStoneCommand(
                    packet,
                    sizeof(packet),
                    &command) &&
                command.action == vector.action &&
                command.targetReference == vector.target &&
                command.secondaryValue == vector.secondary,
            "Holy Stone parser rejected a captured commit");
    }
}

void CheckCitiesAndBoundaries(Checks* checks) {
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    LegacyHolyStoneCommand command{};
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        LegacyHolyStoneBagReferenceMinimum,
        LegacyHolyStoneBagReferenceMaximum,
        LegacyAthensHolyStoneNpc);
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
            command.action == LegacyHolyStoneAction::Mount &&
            command.targetReference ==
                LegacyHolyStoneBagReferenceMinimum &&
            command.secondaryValue ==
                (LegacyHolyStoneBagPageCount *
                 LegacyHolyStoneBagSlotsPerPage) - 1,
        "Athens Holy Stone page-coordinate boundaries failed");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Drill,
        205,
        -1);
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
            command.action == LegacyHolyStoneAction::Drill &&
            command.targetReference == 53,
        "Page-two Holy Stone reference was not normalized");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        16,
        107);
    checks->Require(
        TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command) &&
        command.action == LegacyHolyStoneAction::Mount &&
        command.targetReference == 16 &&
        command.secondaryValue == 31,
        "Mount did not normalize target and material coordinates");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Drill,
        100,
        -1);
    checks->Require(
        TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command) &&
        command.action == LegacyHolyStoneAction::Drill &&
        command.targetReference == 24,
        "Page-one slot zero did not normalize to bag slot 24");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Remove,
        116,
        1);
    checks->Require(
        TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command) &&
        command.action == LegacyHolyStoneAction::Remove &&
        command.targetReference == 40 &&
        command.secondaryValue == 1,
        "Remove did not normalize its target coordinate");
}

void CheckNavigationAndForeignActions(Checks* checks) {
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    LegacyHolyStoneCommand command{};
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
                LegacyHolyStonePacketKind::
                    UnrelatedOrNavigation &&
        !TryReadLegacyHolyStoneCommand(
            packet,
            sizeof(packet),
            &command),
        "Mount navigation did not remain an untagged navigation");

    for (const auto action : {
             LegacyHolyStoneAction::Remove,
             LegacyHolyStoneAction::Drill}) {
        BuildHolyStonePacket(
            packet,
            action,
            -1,
            -1,
            LegacySpartaHolyStoneNpc,
            true);
        checks->Require(
            ClassifyLegacyHolyStonePacket(
                packet,
                sizeof(packet),
                &command) ==
                LegacyHolyStonePacketKind::InvalidMutation,
            "Empty Remove or Drill mutation did not fail closed");
    }

    for (const std::int32_t subId : {106, 206, 306, 406}) {
        BuildHolyStonePacket(
            packet,
            LegacyHolyStoneAction::Mount,
            -1,
            -1,
            LegacySpartaHolyStoneNpc,
            true);
        Write32(
            packet + 16,
            static_cast<std::uint32_t>(subId));
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
            "Legacy Mount alias did not fail closed");
    }

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    Write32(packet + 16, 999);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet,
            sizeof(packet),
            &command) ==
            LegacyHolyStonePacketKind::UnrelatedOrNavigation,
        "Unknown Holy Stone menu value was treated as a mutation");
}

void CheckUnsupportedAdvancedDrillBoundary(Checks* checks) {
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    LegacyHolyStoneCommand command{};
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(
            LegacyHolyStoneAdvancedDrillSubId));

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

    Write32(packet + 20 + 6 * 4, 205);
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
        "Unknown Advanced Drill commit shape did not fail closed");

    Write32(packet + 20 + 6 * 4, 0xFFFFFFFFU);
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

void CheckStrictShapeRejections(Checks* checks) {
    std::uint8_t valid[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t changed[LegacyHolyStoneActionPacketBytes]{};
    LegacyHolyStoneCommand command{};
    BuildHolyStonePacket(
        valid,
        LegacyHolyStoneAction::Mount,
        205,
        107);

    auto rejectsMutation = [&](std::size_t offset,
                               std::uint32_t value,
                               const char* message) {
        std::memcpy(changed, valid, sizeof(changed));
        Write32(changed + offset, value);
        checks->Require(
            ClassifyLegacyHolyStonePacket(
                changed,
                sizeof(changed),
                &command) ==
                LegacyHolyStonePacketKind::InvalidMutation &&
            !TryReadLegacyHolyStoneCommand(
                changed,
                sizeof(changed),
                &command),
            message);
    };

    std::memcpy(changed, valid, sizeof(changed));
    Write16(changed, 91);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            changed,
            sizeof(changed),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation &&
        ClassifyLegacyHolyStonePacket(
            valid,
            sizeof(valid) - 1,
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            valid,
            sizeof(valid),
            nullptr),
        "Canonical Holy Stone length mismatch did not fail closed");

    BuildHolyStonePacket(
        changed,
        LegacyHolyStoneAction::Mount,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    Write32(changed + 16, 106);
    Write16(changed, 91);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            changed,
            sizeof(changed),
            &command) ==
                LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            changed,
            sizeof(changed),
            &command),
        "Holy Stone alias length mismatch did not fail closed");

    std::memcpy(changed, valid, sizeof(changed));
    Write16(changed + 2, 10068);
    checks->Require(
        !TryReadLegacyHolyStoneCommand(
            changed,
            sizeof(changed),
            &command),
        "Holy Stone accepted another opcode");
    std::memcpy(changed, valid, sizeof(changed));
    Write32(changed + 4, 5067);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            changed,
            sizeof(changed),
            &command) ==
            LegacyHolyStonePacketKind::UnrelatedOrNavigation,
        "Another NPC was claimed by the Holy Stone boundary");
    rejectsMutation(8, 31, "Holy Stone accepted wrong dialog");
    rejectsMutation(
        12,
        31,
        "Holy Stone accepted wrong duplicated dialog");
    rejectsMutation(
        20 + 1 * 4,
        0,
        "Holy Stone accepted scratch argument data");
    rejectsMutation(
        20 + 6 * 4,
        224,
        "Holy Stone accepted page slot 24");
    rejectsMutation(
        20 + 7 * 4,
        196,
        "Holy Stone accepted non-bag material reference");
    rejectsMutation(
        20 + 7 * 4,
        205,
        "Holy Stone accepted the same target and material");
    rejectsMutation(
        20,
        1,
        "Holy Stone accepted wrong Mount mode");

    BuildHolyStonePacket(
        valid,
        LegacyHolyStoneAction::Remove,
        205,
        1);
    std::memcpy(changed, valid, sizeof(changed));
    Write32(changed + 20 + 10 * 4, 0);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            changed,
            sizeof(changed),
            &command) ==
            LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            changed,
            sizeof(changed),
            &command),
        "Holy Stone accepted remove ordinal zero");
    std::memcpy(changed, valid, sizeof(changed));
    Write32(changed + 20 + 10 * 4, 5);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            changed,
            sizeof(changed),
            &command) ==
            LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            changed,
            sizeof(changed),
            &command),
        "Holy Stone accepted remove ordinal five");

    BuildHolyStonePacket(
        valid,
        LegacyHolyStoneAction::Drill,
        107,
        -1);
    std::memcpy(changed, valid, sizeof(changed));
    Write32(changed + 20 + 10 * 4, 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            changed,
            sizeof(changed),
            &command) ==
            LegacyHolyStonePacketKind::InvalidMutation &&
        !TryReadLegacyHolyStoneCommand(
            changed,
            sizeof(changed),
            &command),
        "Holy Stone Drill accepted stray arguments");

    for (const int invalidReference :
         {24, 99, 124, 195, 199, 224, 299, 324}) {
        for (const auto action : {
                 LegacyHolyStoneAction::Mount,
                 LegacyHolyStoneAction::Remove,
                 LegacyHolyStoneAction::Drill}) {
            BuildHolyStonePacket(
                changed,
                action,
                invalidReference,
                action == LegacyHolyStoneAction::Mount
                    ? 107
                    : action == LegacyHolyStoneAction::Remove
                        ? 1
                        : -1);
            checks->Require(
                ClassifyLegacyHolyStonePacket(
                    changed,
                    sizeof(changed),
                    &command) ==
                    LegacyHolyStonePacketKind::InvalidMutation,
                "Holy Stone accepted an invalid page coordinate");
        }
    }

    std::uint8_t shortMutation[20]{};
    std::memcpy(shortMutation, valid, sizeof(shortMutation));
    Write16(shortMutation, sizeof(shortMutation));
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            shortMutation,
            sizeof(shortMutation),
            &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "Truncated Holy Stone mutation did not fail closed");
}

} // namespace

int RunSecureHolyStoneParserTests() {
    Checks checks{};
    CheckCapturedGoldenVectors(&checks);
    CheckCitiesAndBoundaries(&checks);
    CheckNavigationAndForeignActions(&checks);
    CheckUnsupportedAdvancedDrillBoundary(&checks);
    CheckStrictShapeRejections(&checks);
    return checks.failures;
}
