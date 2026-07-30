#include "SecureZodiacSkillGridSelectionTestSupport.h"

namespace {

using namespace zodiac_selection_test;

void CheckSessionBinding(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    BuildSelectionPacket(packet, 1, 10'057);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "Zodiac selection did not require a principal");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(75, login);
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::NoCharacter &&
        registry.SetCharacter(925) ==
            SecureOperationRegistryResult::Success,
        "Zodiac selection did not require a character");
}

void CheckIdentityAndReconnect(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Zodiac selection identity setup failed");

    LegacyPacketDescriptor native{};
    LegacyPacketDescriptor compatible{};
    LegacyPacketDescriptor changedPlayer{};
    checks->Require(
        DescribeSelection(&registry, 1, 10'057, &native) ==
                SecureOperationRegistryResult::Success &&
        native.hasOperation &&
        native.operation.packetBytes == LegacyZodiacPacketBytes &&
        native.operation.opcode == LegacyZodiacOpcode &&
        DescribeSelection(
            &registry,
            1,
            10'057,
            &compatible,
            LegacyZodiacCompatibilityModule) ==
                SecureOperationRegistryResult::Success &&
        DescribeSelection(
            &registry,
            1,
            10'057,
            &changedPlayer,
            LegacyZodiacNativeModule,
            0x12345678U) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(native, compatible) &&
        SameOperation(native, changedPlayer),
        "Equivalent Zodiac selections changed UUID");

    LegacyPacketDescriptor anotherKind{};
    LegacyPacketDescriptor clear{};
    LegacyPacketDescriptor anotherGrid{};
    checks->Require(
        DescribeSelection(
            &registry,
            1,
            10'071,
            &anotherKind) ==
                SecureOperationRegistryResult::Success &&
        DescribeSelection(&registry, 1, -1, &clear) ==
                SecureOperationRegistryResult::Success &&
        DescribeSelection(
            &registry,
            2,
            10'057,
            &anotherGrid) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(native, anotherKind) &&
        !SameOperation(native, clear) &&
        !SameOperation(native, anotherGrid),
        "Different Zodiac selection intents shared a UUID");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(75, login);
    LegacyPacketDescriptor ignored{};
    registry.DescribePacket(login, sizeof(login), &ignored);
    registry.SetCharacter(925);
    LegacyPacketDescriptor reconnected{};
    checks->Require(
        DescribeSelection(
            &registry,
            1,
            10'057,
            &reconnected) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(native, reconnected),
        "Zodiac selection did not retain UUID across reconnect");
}

void CheckIsolationAndInvalidMutation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Zodiac selection isolation setup failed");
    LegacyPacketDescriptor original{};
    DescribeSelection(&registry, 4, 20'053, &original);

    registry.SetCharacter(926);
    LegacyPacketDescriptor anotherCharacter{};
    DescribeSelection(
        &registry,
        4,
        20'053,
        &anotherCharacter);
    checks->Require(
        !SameOperation(original, anotherCharacter),
        "Two characters shared a Zodiac selection UUID");

    const auto before = registry.Snapshot().pending;
    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    BuildSelectionPacket(packet, 4, 10'057);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::InvalidPacket &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == before,
        "Invalid Zodiac selection allocated operation state");
}

void CheckSettlementAndExpiry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    LegacyPacketDescriptor pending{};
    checks->Require(
        Establish(&registry) &&
        DescribeSelection(
            &registry,
            0,
            10'057,
            &pending) ==
            SecureOperationRegistryResult::Success,
        "Zodiac selection settlement setup failed");

    const auto result = ResultFor(pending);
    checks->Require(
        registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
        registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
        registry.Snapshot().pending == 0 &&
        registry.Snapshot().resolved == 1,
        "Zodiac selection did not settle idempotently");
    auto wrongFamily = result;
    wrongFamily.commandFamily =
        SecureLegacyCommandFamily::ZodiacSkillGridUpgrade;
    checks->Require(
        registry.Resolve(wrongFamily) ==
            SecureOperationRegistryResult::FamilyConflict,
        "Zodiac selection tombstone accepted another family");

    LegacyPacketDescriptor fresh{};
    DescribeSelection(&registry, 0, 10'057, &fresh);
    checks->Require(
        !SameOperation(pending, fresh),
        "Settled Zodiac selection reused its UUID");

    hooks.now +=
        SecurePendingOperationLifetimeMilliseconds + 1;
    LegacyPacketDescriptor afterExpiry{};
    DescribeSelection(&registry, 0, 10'057, &afterExpiry);
    checks->Require(
        !SameOperation(fresh, afterExpiry),
        "Expired Zodiac selection retained its UUID");
}

void CheckCapacity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Zodiac selection capacity setup failed");
    LegacyPacketDescriptor descriptor{};
    bool filled = true;
    for (int grid = 0; grid < 16; ++grid) {
        const int kind = grid % 8 < 4 ? 10'057 : 20'053;
        filled =
            DescribeSelection(
                &registry,
                grid,
                kind,
                &descriptor) ==
                SecureOperationRegistryResult::Success &&
            filled;
    }
    checks->Require(
        filled && registry.Snapshot().pending == 16,
        "Zodiac selections did not fill the bounded registry");
    checks->Require(
        DescribeSelection(
            &registry,
            0,
            10'071,
            &descriptor) ==
            SecureOperationRegistryResult::Capacity,
        "Zodiac selection exceeded bounded capacity");
}

} // namespace

int RunSecureZodiacSkillGridSelectionRegistryTests() {
    Checks checks{};
    CheckSessionBinding(&checks);
    CheckIdentityAndReconnect(&checks);
    CheckIsolationAndInvalidMutation(&checks);
    CheckSettlementAndExpiry(&checks);
    CheckCapacity(&checks);
    return checks.failures;
}
