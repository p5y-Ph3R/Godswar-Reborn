#include "SecureHolyStoneImplementSpiritIdentityTests.h"

#include "SecureHolyStoneTestSupport.h"

namespace {

using namespace holy_stone_test;

void BuildImplement(
    std::uint8_t* packet,
    std::uint32_t npcId,
    bool navigation) {
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::ImplementSpirit,
        -1,
        -1,
        npcId,
        navigation);
}

bool Describe(
    SecurePendingOperationRegistry* registry,
    const std::uint8_t* packet,
    LegacyPacketDescriptor* descriptor,
    SecureOperationRegistryResult expected =
        SecureOperationRegistryResult::Success) {
    return registry != nullptr &&
        packet != nullptr &&
        descriptor != nullptr &&
        registry->DescribePacket(
            packet,
            LegacyHolyStoneActionPacketBytes,
            descriptor) == expected;
}

bool StageAndClear(
    SecurePendingOperationRegistry* registry,
    const int* slots,
    std::size_t count) {
    for (std::size_t index = 0; index < count; ++index) {
        if (!StageSelection(registry, slots[index])) {
            return false;
        }
    }
    for (std::size_t index = 0; index < count; ++index) {
        if (!StageSelection(registry, slots[index], false)) {
            return false;
        }
    }
    return true;
}

void CheckParserBoundary(Checks* checks) {
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    LegacyHolyStoneCommand command{};
    BuildImplement(packet, LegacySpartaHolyStoneNpc, true);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
                LegacyHolyStonePacketKind::Navigation &&
        command.action == LegacyHolyStoneAction::ImplementSpirit &&
        command.npcId == LegacySpartaHolyStoneNpc &&
        !TryReadLegacyHolyStoneCommand(
            packet, sizeof(packet), &command),
        "action-501 navigation was not isolated");

    BuildImplement(packet, LegacyAthensHolyStoneNpc, false);
    Write32(packet + 20 + 11 * 4, 0x12345678U);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
                LegacyHolyStonePacketKind::StagedCommit &&
        command.action == LegacyHolyStoneAction::ImplementSpirit &&
        command.npcId == LegacyAthensHolyStoneNpc &&
        command.targetReference == -1 &&
        command.secondaryValue == -1,
        "action-501 scratch values became trusted item roles");

    Write16(packet, LegacyHolyStoneActionPacketBytes - 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "malformed action-501 length did not fail closed");

    BuildImplement(packet, LegacySpartaHolyStoneNpc, false);
    Write32(packet + 8, LegacyHolyStoneDialog + 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "malformed action-501 dialog did not fail closed");

    BuildImplement(packet, LegacySpartaHolyStoneNpc, false);
    Write32(packet + 16, 506);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "response alias 506 was accepted as an implementation command");

    BuildImplement(packet, 9999, false);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::UnrelatedOrNavigation,
        "foreign NPC action 501 entered the Holy Stone boundary");
}

void CheckTwoAndThreeSlotLifecycle(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t commit[LegacyHolyStoneActionPacketBytes]{};
    BuildImplement(page, LegacySpartaHolyStoneNpc, true);
    BuildImplement(commit, LegacySpartaHolyStoneNpc, false);
    const int baseSlots[]{40, 7};
    LegacyPacketDescriptor descriptor{};
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};

    checks->Require(
        Establish(&registry) &&
        StageSelection(&registry, 70) &&
        Describe(&registry, page, &descriptor) &&
        !descriptor.hasOperation &&
        StageAndClear(&registry, baseSlots, 2) &&
        Describe(&registry, commit, &first) &&
        first.hasOperation,
        "ordered Holy Stone and Spirit slots received no operation UUID");

    Write32(commit + 20 + 14 * 4, 0x76543210U);
    checks->Require(
        Describe(&registry, commit, &retry) &&
        SameOperation(first, retry),
        "untrusted action-501 scratch changed the retry UUID");

    const auto result = ResultFor(
        first,
        SecureLegacyCommandFamily::HolyStoneImplementSpirit);
    std::uint8_t encoded[SecureLegacyCommandResultPayloadBytes]{};
    SecureLegacyCommandResult decoded{};
    checks->Require(
        TryEncodeSecureLegacyCommandResult(
            result, encoded, sizeof(encoded)) &&
        TryDecodeSecureLegacyCommandResult(
            encoded, sizeof(encoded), &decoded) &&
        decoded.commandFamily ==
            SecureLegacyCommandFamily::HolyStoneImplementSpirit &&
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success &&
        registry.Snapshot().pending == 0,
        "implementation settlement did not clear its operation");

    const int goddessSlots[]{40, 7, 91};
    LegacyPacketDescriptor withGoddess{};
    LegacyPacketDescriptor afterClear{};
    checks->Require(
        StageSelection(&registry, goddessSlots[0]) &&
        StageSelection(&registry, goddessSlots[1]) &&
        StageSelection(&registry, goddessSlots[2]) &&
        Describe(&registry, page, &withGoddess) &&
        withGoddess.hasOperation &&
        !SameOperation(first, withGoddess) &&
        StageSelection(&registry, goddessSlots[0], false) &&
        StageSelection(&registry, goddessSlots[1], false) &&
        StageSelection(&registry, goddessSlots[2], false) &&
        Describe(&registry, page, &afterClear) &&
        SameOperation(withGoddess, afterClear),
        "optional Goddess slot was not part of the fixed identity");
}

void CheckRoleOrderAndCrossCityRetry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t spartaPage[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t spartaCommit[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t athensPage[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t athensCommit[LegacyHolyStoneActionPacketBytes]{};
    BuildImplement(spartaPage, LegacySpartaHolyStoneNpc, true);
    BuildImplement(spartaCommit, LegacySpartaHolyStoneNpc, false);
    BuildImplement(athensPage, LegacyAthensHolyStoneNpc, true);
    BuildImplement(athensCommit, LegacyAthensHolyStoneNpc, false);
    const int slots[]{12, 13, 14};
    const int reversed[]{13, 12, 14};
    LegacyPacketDescriptor ignored{};
    LegacyPacketDescriptor sparta{};
    LegacyPacketDescriptor athens{};
    LegacyPacketDescriptor changedOrder{};

    checks->Require(
        Establish(&registry) &&
        Describe(&registry, spartaPage, &ignored) &&
        StageAndClear(&registry, slots, 3) &&
        Describe(&registry, spartaCommit, &sparta) &&
        sparta.hasOperation &&
        Describe(&registry, athensPage, &ignored) &&
        StageAndClear(&registry, slots, 3) &&
        Describe(&registry, athensCommit, &athens) &&
        SameOperation(sparta, athens),
        "equivalent cross-city implementation retry changed UUID");

    checks->Require(
        Describe(&registry, spartaPage, &ignored) &&
        StageAndClear(&registry, reversed, 3) &&
        Describe(&registry, spartaCommit, &changedOrder) &&
        changedOrder.hasOperation &&
        !SameOperation(sparta, changedOrder),
        "changing target and Spirit role order retained a UUID");
}

void CheckIncompleteOverflowAndExpiry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t commit[LegacyHolyStoneActionPacketBytes]{};
    BuildImplement(page, LegacySpartaHolyStoneNpc, true);
    BuildImplement(commit, LegacySpartaHolyStoneNpc, false);
    LegacyPacketDescriptor descriptor{};

    checks->Require(
        Establish(&registry) &&
        Describe(&registry, page, &descriptor) &&
        StageSelection(&registry, 1) &&
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "one-slot implementation received an operation UUID");

    checks->Require(
        Describe(&registry, page, &descriptor) &&
        StageSelection(&registry, 1) &&
        StageSelection(&registry, 2) &&
        StageSelection(&registry, 3) &&
        StageSelection(&registry, 4) &&
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection),
        "four-slot implementation was truncated into an identity");

    const int cleared[]{8, 9};
    checks->Require(
        Describe(&registry, page, &descriptor) &&
        StageAndClear(&registry, cleared, 2),
        "implementation expiry fixture setup failed");
    hooks.now += SecureSelectionClearCorrelationLifetimeMilliseconds;
    checks->Require(
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "expired implementation selection received an operation UUID");
}

} // namespace

int RunSecureHolyStoneImplementSpiritIdentityTests() {
    Checks checks{};
    CheckParserBoundary(&checks);
    CheckTwoAndThreeSlotLifecycle(&checks);
    CheckRoleOrderAndCrossCityRetry(&checks);
    CheckIncompleteOverflowAndExpiry(&checks);
    return checks.failures;
}
