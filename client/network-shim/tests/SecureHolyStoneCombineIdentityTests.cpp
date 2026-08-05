#include "SecureHolyStoneCombineIdentityTests.h"

#include "SecureHolyStoneTestSupport.h"

namespace {

using namespace holy_stone_test;

bool Describe(
    SecurePendingOperationRegistry* registry,
    const std::uint8_t* packet,
    LegacyPacketDescriptor* descriptor,
    SecureOperationRegistryResult expected =
        SecureOperationRegistryResult::Success) {
    return registry != nullptr &&
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
    const int slots[]{70, 4, 55, 23};
    LegacyHolyStoneCommand command{};

    BuildHolyStoneCombinePacket(
        packet, nullptr, LegacySpartaHolyStoneNpc, true);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
                LegacyHolyStonePacketKind::Navigation &&
        command.action == LegacyHolyStoneAction::Combine &&
        command.npcId == LegacySpartaHolyStoneNpc &&
        command.combinationCount == 0,
        "all-unset action 601 was not isolated as navigation");

    BuildHolyStoneCombinePacket(
        packet, slots, LegacyAthensHolyStoneNpc);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
                LegacyHolyStonePacketKind::StagedCommit &&
        command.action == LegacyHolyStoneAction::Combine &&
        command.npcId == LegacyAthensHolyStoneNpc &&
        command.combinationCount == 4 &&
        command.combinationBagSlots[0] == slots[0] &&
        command.combinationBagSlots[1] == slots[1] &&
        command.combinationBagSlots[2] == slots[2] &&
        command.combinationBagSlots[3] == slots[3] &&
        !TryReadLegacyHolyStoneCommand(
            packet, sizeof(packet), &command),
        "action 601 did not preserve its four fixed item roles");

    Write32(packet + 20 + 9 * 4, 0xFFFFFFFFU);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "partial action 601 did not fail closed");

    BuildHolyStoneCombinePacket(packet, slots);
    Write32(
        packet + 20 + 9 * 4,
        EncodeHolyStoneBagReference(slots[0]));
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "duplicate combination role did not fail closed");

    BuildHolyStoneCombinePacket(packet, slots);
    Write32(packet + 20 + 10 * 4, 0);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "unexpected action-601 argument did not fail closed");

    BuildHolyStoneCombinePacket(packet, slots);
    Write16(packet, LegacyHolyStoneActionPacketBytes - 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "malformed action-601 length did not fail closed");
}

void CheckInitialClearBeforeAction(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    const int slots[]{70, 4, 55, 23};
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t commit[LegacyHolyStoneActionPacketBytes]{};
    BuildHolyStoneCombinePacket(page, nullptr,
        LegacySpartaHolyStoneNpc, true);
    BuildHolyStoneCombinePacket(commit, slots);
    LegacyPacketDescriptor descriptor{};
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};

    checks->Require(
        Establish(&registry) &&
        Describe(&registry, page, &descriptor) &&
        !descriptor.hasOperation &&
        StageSelection(&registry, slots[0]) &&
        StageSelection(&registry, slots[1]) &&
        StageSelection(&registry, slots[2]) &&
        StageSelection(&registry, slots[3]) &&
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "initial combination page accepted action before its clear burst");

    // Reopening the page discards the live fixture before the real stock
    // select -> clear -> action sequence.
    checks->Require(
        Describe(&registry, page, &descriptor) &&
        StageAndClear(&registry, slots, 4) &&
        Describe(&registry, commit, &first) &&
        first.hasOperation &&
        Describe(&registry, commit, &retry) &&
        SameOperation(first, retry),
        "four-role combination retry did not retain one operation UUID");

    const auto result = ResultFor(
        first, SecureLegacyCommandFamily::HolyStoneCombine);
    std::uint8_t encoded[SecureLegacyCommandResultPayloadBytes]{};
    SecureLegacyCommandResult decoded{};
    checks->Require(
        TryEncodeSecureLegacyCommandResult(
            result, encoded, sizeof(encoded)) &&
        TryDecodeSecureLegacyCommandResult(
            encoded, sizeof(encoded), &decoded) &&
        decoded.commandFamily ==
            SecureLegacyCommandFamily::HolyStoneCombine &&
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success,
        "family-43 result could not settle the combination operation");
}

void CheckResultActionBeforeClear(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    const int firstSlots[]{1, 2, 3, 4};
    const int nextSlots[]{40, 17, 72, 9};
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t firstCommit[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t nextCommit[LegacyHolyStoneActionPacketBytes]{};
    BuildHolyStoneCombinePacket(page, nullptr,
        LegacySpartaHolyStoneNpc, true);
    BuildHolyStoneCombinePacket(firstCommit, firstSlots);
    BuildHolyStoneCombinePacket(nextCommit, nextSlots);
    LegacyPacketDescriptor ignored{};
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor next{};
    LegacyPacketDescriptor afterClearRetry{};

    checks->Require(
        Establish(&registry) &&
        Describe(&registry, page, &ignored) &&
        StageAndClear(&registry, firstSlots, 4) &&
        Describe(&registry, firstCommit, &first) &&
        registry.Resolve(ResultFor(
            first, SecureLegacyCommandFamily::HolyStoneCombine)) ==
                SecureOperationRegistryResult::Success &&
        StageSelection(&registry, nextSlots[0]) &&
        StageSelection(&registry, nextSlots[1]) &&
        StageSelection(&registry, nextSlots[2]) &&
        StageSelection(&registry, nextSlots[3]) &&
        Describe(&registry, nextCommit, &next) &&
        next.hasOperation &&
        !SameOperation(first, next),
        "result-rebuilt page did not accept its action-before-clear flow");

    checks->Require(
        StageSelection(&registry, nextSlots[0], false) &&
        StageSelection(&registry, nextSlots[1], false) &&
        StageSelection(&registry, nextSlots[2], false) &&
        StageSelection(&registry, nextSlots[3], false) &&
        Describe(&registry, nextCommit, &afterClearRetry) &&
        SameOperation(next, afterClearRetry),
        "late result-page clear changed the unresolved combination UUID");
}

void CheckExactOrderAndBounds(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    const int slots[]{10, 11, 12, 13};
    const int reversed[]{13, 12, 11, 10};
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t wrongOrder[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t commit[LegacyHolyStoneActionPacketBytes]{};
    BuildHolyStoneCombinePacket(page, nullptr,
        LegacySpartaHolyStoneNpc, true);
    BuildHolyStoneCombinePacket(wrongOrder, reversed);
    BuildHolyStoneCombinePacket(commit, slots);
    LegacyPacketDescriptor descriptor{};

    checks->Require(
        Establish(&registry) &&
        Describe(&registry, page, &descriptor) &&
        StageAndClear(&registry, slots, 4) &&
        Describe(
            &registry,
            wrongOrder,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation &&
        Describe(&registry, commit, &descriptor) &&
        descriptor.hasOperation,
        "combination identity did not enforce fixed four-slot order");

    BuildHolyStoneCombinePacket(page, nullptr,
        LegacyAthensHolyStoneNpc, true);
    checks->Require(
        Describe(&registry, page, &descriptor) &&
        StageSelection(&registry, 20) &&
        StageSelection(&registry, 21) &&
        StageSelection(&registry, 22) &&
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "three-slot combination received an operation UUID");

    hooks.now += SecurePendingOperationLifetimeMilliseconds;
    checks->Require(
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection),
        "expired combination page retained selection authority");
}

} // namespace

int RunSecureHolyStoneCombineIdentityTests() {
    Checks checks{};
    CheckParserBoundary(&checks);
    CheckInitialClearBeforeAction(&checks);
    CheckResultActionBeforeClear(&checks);
    CheckExactOrderAndBounds(&checks);
    return checks.failures;
}
