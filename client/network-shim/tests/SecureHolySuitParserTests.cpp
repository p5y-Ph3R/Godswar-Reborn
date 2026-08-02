#include "SecureHolySuitTestSupport.h"

#include <climits>

namespace {

using namespace holy_suit_test;

void CheckCanonicalCommits(Checks* checks) {
    struct Case final {
        LegacyHolySuitAction action;
        int primary;
        int secondary;
        std::uint32_t amount;
    };
    const Case cases[]{
        // The stock client represents page-zero slot 12 literally as 12.
        {LegacyHolySuitAction::StoreExperience, 12, -1, 1},
        {LegacyHolySuitAction::TransferExperience, 0, 323, 0},
        {LegacyHolySuitAction::ConsumeWare, 123, 200, 0},
        {LegacyHolySuitAction::TransformExperience, -1, -1, 99},
    };
    const std::uint32_t cities[]{
        LegacySpartaHolySuitNpc,
        LegacyAthensHolySuitNpc,
    };

    for (const auto city : cities) {
        for (const auto& expected : cases) {
            std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
            BuildHolySuitPacket(
                packet,
                expected.action,
                expected.primary,
                expected.secondary,
                expected.amount,
                city);
            LegacyHolySuitCommand command{};
            checks->Require(
                ClassifyLegacyHolySuitPacket(
                    packet,
                    sizeof(packet),
                    &command) == LegacyHolySuitPacketKind::Commit &&
                TryReadLegacyHolySuitCommand(
                    packet,
                    sizeof(packet),
                    &command) &&
                command.action == expected.action &&
                command.primaryReference == expected.primary &&
                command.secondaryReference == expected.secondary &&
                command.amount == expected.amount,
                "Canonical Holy Suit commit did not parse");
        }
    }

    std::uint8_t maximum[LegacyHolySuitActionPacketBytes]{};
    BuildHolySuitPacket(
        maximum,
        LegacyHolySuitAction::StoreExperience,
        323,
        -1,
        INT_MAX);
    LegacyHolySuitCommand command{};
    checks->Require(
        TryReadLegacyHolySuitCommand(
            maximum,
            sizeof(maximum),
            &command) &&
        command.amount == INT_MAX,
        "Holy Suit parser narrowed the positive wire amount");

    BuildHolySuitPacket(
        maximum,
        LegacyHolySuitAction::StoreExperience,
        323,
        -1,
        UINT32_MAX - 1U);
    checks->Require(
        TryReadLegacyHolySuitCommand(
            maximum,
            sizeof(maximum),
            &command) &&
        command.amount == UINT32_MAX - 1U,
        "Holy Suit parser narrowed the highest non-sentinel amount");

    BuildHolySuitPacket(
        maximum,
        LegacyHolySuitAction::StoreExperience,
        323,
        -1,
        0x80000000U);
    checks->Require(
        TryReadLegacyHolySuitCommand(
            maximum,
            sizeof(maximum),
            &command) &&
        command.amount == 0x80000000U,
        "Holy Suit parser rejected the high unsigned amount domain");

    BuildHolySuitPacket(
        maximum,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        UINT32_MAX);
    checks->Require(
        TryReadLegacyHolySuitCommand(
            maximum,
            sizeof(maximum),
            &command) &&
        command.action == LegacyHolySuitAction::StoreExperience &&
        command.primaryReference == 12 &&
        command.amount == 0,
        "Holy Suit blank Store amount did not become auto/max intent");

    BuildHolySuitPacket(
        maximum,
        LegacyHolySuitAction::TransformExperience,
        -1,
        -1,
        LegacyHolySuitBlankAmount);
    checks->Require(
        TryReadLegacyHolySuitCommand(
            maximum,
            sizeof(maximum),
            &command) &&
        command.action == LegacyHolySuitAction::TransformExperience &&
        command.amount == LegacyHolySuitMouseOnlyTransformPrisms,
        "Holy Suit blank Transform amount did not use the 20-prism default");
}

void CheckCanonicalBagReferences(Checks* checks) {
    const int validReferences[]{
        0, 12, 23,
        100, 123,
        200, 223,
        300, 323,
    };
    for (const auto reference : validReferences) {
        std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
        BuildHolySuitPacket(
            packet,
            LegacyHolySuitAction::StoreExperience,
            reference,
            -1,
            1);
        LegacyHolySuitCommand command{};
        checks->Require(
            TryReadLegacyHolySuitCommand(
                packet,
                sizeof(packet),
                &command) &&
            command.primaryReference == reference,
            "Holy Suit rejected a canonical page/slot bag reference");
    }

    const int invalidReferences[]{
        -1,
        24, 99,
        124, 199,
        224, 299,
        324, INT_MAX,
    };
    for (const auto reference : invalidReferences) {
        std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
        BuildHolySuitPacket(
            packet,
            LegacyHolySuitAction::StoreExperience,
            reference,
            -1,
            1);
        LegacyHolySuitCommand command{};
        checks->Require(
            ClassifyLegacyHolySuitPacket(
                packet,
                sizeof(packet),
                &command) == LegacyHolySuitPacketKind::InvalidMutation &&
            !TryReadLegacyHolySuitCommand(
                packet,
                sizeof(packet),
                &command),
            "Holy Suit accepted a non-canonical page/slot bag reference");
    }
}

void CheckNavigationAndForeignPackets(Checks* checks) {
    const LegacyHolySuitAction actions[]{
        LegacyHolySuitAction::StoreExperience,
        LegacyHolySuitAction::TransferExperience,
        LegacyHolySuitAction::ConsumeWare,
        LegacyHolySuitAction::TransformExperience,
    };
    for (const auto action : actions) {
        std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
        BuildHolySuitPacket(
            packet, action, -1, -1, 0,
            LegacySpartaHolySuitNpc, true);
        LegacyHolySuitCommand command{};
        checks->Require(
            ClassifyLegacyHolySuitPacket(
                packet,
                sizeof(packet),
                &command) ==
                LegacyHolySuitPacketKind::UnrelatedOrNavigation &&
            !TryReadLegacyHolySuitCommand(
                packet,
                sizeof(packet),
                &command),
            "Holy Suit page navigation received a commit identity");
    }

    std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        1);
    LegacyHolySuitCommand command{};
    Write16(packet + 2, 10068);
    checks->Require(
        ClassifyLegacyHolySuitPacket(
            packet,
            sizeof(packet),
            &command) ==
            LegacyHolySuitPacketKind::UnrelatedOrNavigation,
        "Holy Suit parser claimed another opcode");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        1,
        5081);
    checks->Require(
        ClassifyLegacyHolySuitPacket(
            packet,
            sizeof(packet),
            &command) ==
            LegacyHolySuitPacketKind::UnrelatedOrNavigation,
        "Holy Suit parser claimed another NPC");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        1);
    Write32(packet + 16, 106);
    checks->Require(
        ClassifyLegacyHolySuitPacket(
            packet,
            sizeof(packet),
            &command) ==
            LegacyHolySuitPacketKind::UnrelatedOrNavigation,
        "Holy Suit parser claimed a server page sub-ID");
}

void CheckMalformedMutations(Checks* checks) {
    LegacyHolySuitCommand command{};
    std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
    auto requireInvalid = [&](const char* message) {
        checks->Require(
            ClassifyLegacyHolySuitPacket(
                packet,
                sizeof(packet),
                &command) ==
                LegacyHolySuitPacketKind::InvalidMutation &&
            !TryReadLegacyHolySuitCommand(
                packet,
                sizeof(packet),
                &command),
            message);
    };

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        1);
    Write16(packet, LegacyHolySuitActionPacketBytes - 1);
    requireInvalid("Holy Suit accepted a declared-length mismatch");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        100,
        -1,
        1);
    Write32(packet + 8, LegacyHolySuitDialog + 1);
    requireInvalid("Holy Suit accepted another dialog");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        100,
        -1,
        1);
    Write32(packet + 12, LegacyHolySuitDialog + 1);
    requireInvalid("Holy Suit accepted a mismatched duplicate dialog");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        0);
    requireInvalid("Holy Suit accepted zero Store amount");
    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::TransformExperience,
        -1,
        -1,
        0);
    requireInvalid("Holy Suit accepted zero Transform amount");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::TransferExperience,
        100,
        100,
        0);
    requireInvalid("Holy Suit accepted duplicate Transfer references");
    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::ConsumeWare,
        100,
        196,
        0);
    requireInvalid("Holy Suit accepted non-bag Ware reference");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::TransformExperience,
        -1,
        -1,
        1);
    Write32(
        packet + 20 + LegacyHolySuitScratchArgument * 4,
        1);
    requireInvalid("Holy Suit accepted a nonzero stock scratch argument");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::TransformExperience,
        -1,
        -1,
        1);
    Write32(packet + 20 + 1 * 4, 0);
    requireInvalid("Holy Suit accepted zero in another unused argument");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        1);
    Write32(
        packet + 20 + LegacyHolySuitScratchArgument * 4,
        UINT32_MAX);
    checks->Require(
        TryReadLegacyHolySuitCommand(
            packet,
            sizeof(packet),
            &command),
        "Holy Suit rejected the normal -1 scratch sentinel");

    std::uint8_t shortPacket[20]{};
    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        1);
    std::memcpy(shortPacket, packet, sizeof(shortPacket));
    Write16(shortPacket, sizeof(shortPacket));
    checks->Require(
        ClassifyLegacyHolySuitPacket(
            shortPacket,
            sizeof(shortPacket),
            &command) == LegacyHolySuitPacketKind::InvalidMutation,
        "Truncated Holy Suit mutation did not fail closed");
    checks->Require(
        !TryReadLegacyHolySuitCommand(
            packet,
            sizeof(packet),
            nullptr),
        "Holy Suit parser accepted a null output command");
}

} // namespace

int RunSecureHolySuitParserTests() {
    Checks checks{};
    CheckCanonicalCommits(&checks);
    CheckCanonicalBagReferences(&checks);
    CheckNavigationAndForeignPackets(&checks);
    CheckMalformedMutations(&checks);
    return checks.failures;
}
