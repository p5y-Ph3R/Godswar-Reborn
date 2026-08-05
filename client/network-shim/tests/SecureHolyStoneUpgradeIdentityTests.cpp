#include "SecureHolyStoneUpgradeIdentityTests.h"

#include "SecureHolyStoneTestSupport.h"

namespace {

using namespace holy_stone_test;

bool Describe(
    SecurePendingOperationRegistry* registry,
    const std::uint8_t* packet,
    LegacyPacketDescriptor* descriptor,
    SecureOperationRegistryResult expected =
        SecureOperationRegistryResult::Success) {
    if (registry == nullptr || packet == nullptr || descriptor == nullptr) {
        return false;
    }
    *descriptor = LegacyPacketDescriptor{};
    return registry->DescribePacket(
               packet,
               LegacyHolyStoneActionPacketBytes,
               descriptor) == expected;
}

void BuildUpgrade(
    std::uint8_t* packet,
    std::uint32_t npcId,
    bool navigation) {
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Upgrade,
        -1,
        -1,
        npcId,
        navigation);
}

bool StageAndClear(
    SecurePendingOperationRegistry* registry,
    const int* slots,
    std::size_t count) {
    for (std::size_t index = 0; index < count; ++index) {
        if (!StageSelection(registry, slots[index], true)) {
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
    BuildUpgrade(packet, LegacySpartaHolyStoneNpc, true);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
                LegacyHolyStonePacketKind::Navigation &&
        command.action == LegacyHolyStoneAction::Upgrade &&
        command.npcId == LegacySpartaHolyStoneNpc &&
        command.targetReference == -1 &&
        command.secondaryValue == -1 &&
        !TryReadLegacyHolyStoneCommand(
            packet, sizeof(packet), &command),
        "action-401 page navigation was not isolated");

    BuildUpgrade(packet, LegacyAthensHolyStoneNpc, false);
    Write32(packet + 20 + 11 * 4, 0x12345678U);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
                LegacyHolyStonePacketKind::StagedCommit &&
        command.action == LegacyHolyStoneAction::Upgrade &&
        command.npcId == LegacyAthensHolyStoneNpc &&
        command.targetReference == -1 &&
        command.secondaryValue == -1 &&
        !TryReadLegacyHolyStoneCommand(
            packet, sizeof(packet), &command),
        "action-401 scratch values became a trusted command identity");

    Write16(packet, LegacyHolyStoneActionPacketBytes - 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "malformed action-401 declared length did not fail closed");

    BuildUpgrade(packet, LegacySpartaHolyStoneNpc, false);
    Write32(packet + 8, LegacyHolyStoneDialog + 1);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "malformed action-401 dialog did not fail closed");

    BuildUpgrade(packet, LegacySpartaHolyStoneNpc, false);
    Write32(packet + 16, 406);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::InvalidMutation,
        "response alias 406 was accepted as an Upgrade command");

    BuildUpgrade(packet, 9999, false);
    checks->Require(
        ClassifyLegacyHolyStonePacket(
            packet, sizeof(packet), &command) ==
            LegacyHolyStonePacketKind::UnrelatedOrNavigation,
        "foreign NPC action 401 entered the Holy Stone boundary");
}

void CheckTwoSlotIdentityAndRetry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t commit[LegacyHolyStoneActionPacketBytes]{};
    BuildUpgrade(page, LegacySpartaHolyStoneNpc, true);
    BuildUpgrade(commit, LegacySpartaHolyStoneNpc, false);
    LegacyPacketDescriptor descriptor{};
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    const int immediateRetrySlots[]{70, 4};

    checks->Require(
        Establish(&registry) &&
        StageSelection(&registry, 70) &&
        StageSelection(&registry, 4) &&
        Describe(&registry, page, &descriptor) &&
        !descriptor.hasOperation &&
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "opening Upgrade did not discard stale global selections");

    checks->Require(
        StageAndClear(
            &registry,
            immediateRetrySlots,
            sizeof(immediateRetrySlots) /
                sizeof(immediateRetrySlots[0])) &&
        Describe(&registry, commit, &first) &&
        first.hasOperation &&
        first.operation.packetBytes ==
            LegacyHolyStoneActionPacketBytes &&
        first.operation.opcode == LegacyNpcFunctionActionOpcode,
        "ordered two-slot Upgrade did not receive an operation UUID");

    Write32(commit + 20 + 5 * 4, 0x76543210U);
    checks->Require(
        Describe(&registry, commit, &retry) &&
        SameOperation(first, retry),
        "scratch changes did not retain the exact unresolved Upgrade UUID");

    const auto result = ResultFor(
        first, SecureLegacyCommandFamily::HolyStoneUpgrade);
    std::uint8_t encoded[SecureLegacyCommandResultPayloadBytes]{};
    SecureLegacyCommandResult decoded{};
    checks->Require(
        TryEncodeSecureLegacyCommandResult(
            result, encoded, sizeof(encoded)) &&
        TryDecodeSecureLegacyCommandResult(
            encoded, sizeof(encoded), &decoded) &&
        decoded.commandFamily ==
            SecureLegacyCommandFamily::HolyStoneUpgrade &&
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success &&
        registry.Snapshot().pending == 0,
        "Upgrade settlement did not clear its one-shot operation state");

    LegacyPacketDescriptor immediateRetry{};
    LegacyPacketDescriptor afterClearRetry{};
    // Deliberately do not describe `page` after Resolve. The stock client
    // receives the server's [3100, result] response and uses its observed A3
    // order: select, action 401, then clear. Only that result-rearmed page may
    // bind the all-unset action to live selections.
    checks->Require(
        StageSelection(&registry, immediateRetrySlots[0]) &&
        StageSelection(&registry, immediateRetrySlots[1]) &&
        Describe(&registry, page, &immediateRetry) &&
        immediateRetry.hasOperation &&
        !SameOperation(first, immediateRetry) &&
        StageSelection(
            &registry, immediateRetrySlots[0], false) &&
        StageSelection(
            &registry, immediateRetrySlots[1], false) &&
        Describe(&registry, page, &afterClearRetry) &&
        SameOperation(immediateRetry, afterClearRetry),
        "server-rebuilt Upgrade page did not bind action-before-clear to one fresh UUID");

    const auto immediateResult = ResultFor(
        immediateRetry,
        SecureLegacyCommandFamily::HolyStoneUpgrade);
    LegacyPacketDescriptor nextResultPageOperation{};
    checks->Require(
        registry.Resolve(immediateResult) ==
            SecureOperationRegistryResult::Success &&
        StageSelection(&registry, immediateRetrySlots[0]) &&
        StageSelection(&registry, immediateRetrySlots[1]) &&
        Describe(
            &registry,
            page,
            &nextResultPageOperation) &&
        nextResultPageOperation.hasOperation &&
        !SameOperation(
            immediateRetry,
            nextResultPageOperation),
        "a second settled result page did not issue a new one-shot UUID");
}

void CheckClearedAllUnsetCommit(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    BuildUpgrade(page, LegacySpartaHolyStoneNpc, true);
    const int slots[]{19, 63};
    LegacyPacketDescriptor descriptor{};
    LegacyPacketDescriptor premature{};
    LegacyPacketDescriptor commit{};
    checks->Require(
        Establish(&registry) &&
        Describe(&registry, page, &descriptor) &&
        StageSelection(&registry, slots[0]) &&
        StageSelection(&registry, slots[1]) &&
        Describe(&registry, page, &premature) &&
        !premature.hasOperation &&
        StageAndClear(&registry, slots, 2) &&
        registry.Snapshot().pending == 0,
        "initial Upgrade page accepted action-before-clear or clearing alone created an operation");
    checks->Require(
        Describe(&registry, page, &commit) &&
        commit.hasOperation,
        "ordered cleared selections did not promote all-unset action 401");
}

void CheckThreeSlotsAndCityIsolation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t spartaPage[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t spartaCommit[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t athensPage[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t athensCommit[LegacyHolyStoneActionPacketBytes]{};
    BuildUpgrade(spartaPage, LegacySpartaHolyStoneNpc, true);
    BuildUpgrade(spartaCommit, LegacySpartaHolyStoneNpc, false);
    BuildUpgrade(athensPage, LegacyAthensHolyStoneNpc, true);
    BuildUpgrade(athensCommit, LegacyAthensHolyStoneNpc, false);
    const int slots[]{41, 2, 88};
    LegacyPacketDescriptor ignored{};
    LegacyPacketDescriptor sparta{};
    LegacyPacketDescriptor athens{};

    checks->Require(
        Establish(&registry) &&
        Describe(&registry, spartaPage, &ignored) &&
        StageAndClear(&registry, slots, 3) &&
        Describe(&registry, spartaCommit, &sparta) &&
        sparta.hasOperation,
        "ordered three-slot Upgrade did not receive an operation UUID");

    checks->Require(
        Describe(&registry, athensPage, &ignored) &&
        !ignored.hasOperation &&
        StageAndClear(&registry, slots, 3) &&
        Describe(&registry, athensCommit, &athens) &&
        SameOperation(sparta, athens),
        "equivalent city Upgrade retry did not retain its UUID");
}

void CheckIncompleteOverflowAndExpiry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t page[LegacyHolyStoneActionPacketBytes]{};
    std::uint8_t commit[LegacyHolyStoneActionPacketBytes]{};
    BuildUpgrade(page, LegacySpartaHolyStoneNpc, true);
    BuildUpgrade(commit, LegacySpartaHolyStoneNpc, false);
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
        "partial one-slot Upgrade received an operation UUID");

    BuildUpgrade(page, LegacyAthensHolyStoneNpc, true);
    BuildUpgrade(commit, LegacyAthensHolyStoneNpc, false);
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
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "over-capacity Upgrade selection was truncated into an identity");

    BuildUpgrade(page, LegacySpartaHolyStoneNpc, true);
    BuildUpgrade(commit, LegacySpartaHolyStoneNpc, false);
    const int cleared[]{8, 9};
    checks->Require(
        Describe(&registry, page, &descriptor) &&
        StageAndClear(&registry, cleared, 2),
        "cleared-selection expiry fixture setup failed");
    hooks.now += SecureSelectionClearCorrelationLifetimeMilliseconds;
    checks->Require(
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "expired cleared Upgrade selection received an operation UUID");

    BuildUpgrade(page, LegacyAthensHolyStoneNpc, true);
    BuildUpgrade(commit, LegacyAthensHolyStoneNpc, false);
    checks->Require(
        Describe(&registry, page, &descriptor) &&
        StageSelection(&registry, 10) &&
        StageSelection(&registry, 11),
        "Upgrade page-expiry fixture setup failed");
    hooks.now += SecurePendingOperationLifetimeMilliseconds;
    checks->Require(
        Describe(
            &registry,
            commit,
            &descriptor,
            SecureOperationRegistryResult::NoSelection) &&
        !descriptor.hasOperation,
        "expired Upgrade page received an operation UUID");
}

} // namespace

int RunSecureHolyStoneUpgradeIdentityTests() {
    Checks checks{};
    CheckParserBoundary(&checks);
    CheckTwoSlotIdentityAndRetry(&checks);
    CheckClearedAllUnsetCommit(&checks);
    CheckThreeSlotsAndCityIsolation(&checks);
    CheckIncompleteOverflowAndExpiry(&checks);
    return checks.failures;
}
