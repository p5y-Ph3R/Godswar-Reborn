#include "SecureHolyStoneTestSupport.h"

#include <initializer_list>

namespace {

using namespace holy_stone_test;

void CheckPrincipalCharacterAndNavigation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        205,
        107);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "Holy Stone commit did not require a principal");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(50, login);
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
        registry.SetCharacter(910) ==
            SecureOperationRegistryResult::Success,
        "Holy Stone commit did not require a character");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 0,
        "Mount navigation received an operation marker");
}

void CheckIdentityAndCrossCityRetry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Holy Stone identity setup failed");

    LegacyPacketDescriptor sparta{};
    LegacyPacketDescriptor athens{};
    LegacyPacketDescriptor changedTarget{};
    LegacyPacketDescriptor changedStone{};
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Mount,
            205,
            107,
            LegacySpartaHolyStoneNpc,
            &sparta) ==
                SecureOperationRegistryResult::Success &&
        sparta.hasOperation &&
        sparta.operation.packetBytes ==
            LegacyHolyStoneActionPacketBytes &&
        sparta.operation.opcode ==
            LegacyNpcFunctionActionOpcode &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Mount,
            205,
            107,
            LegacyAthensHolyStoneNpc,
            &athens) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(sparta, athens),
        "Cross-city Holy Stone retry changed operation UUID");

    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Mount,
            112,
            107,
            LegacySpartaHolyStoneNpc,
            &changedTarget) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(sparta, changedTarget) &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Mount,
            205,
            109,
            LegacySpartaHolyStoneNpc,
            &changedStone) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(sparta, changedStone),
        "Holy Stone identity aliased target or material roles");

    LegacyPacketDescriptor remove{};
    LegacyPacketDescriptor drill{};
    LegacyPacketDescriptor mountGearDrill{};
    LegacyPacketDescriptor athensMountGearDrill{};
    LegacyPacketDescriptor advancedDrill{};
    LegacyPacketDescriptor changedAdvancedSpell{};
    LegacyPacketDescriptor athensAdvancedDrill{};
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Remove,
            205,
            1,
            LegacySpartaHolyStoneNpc,
            &remove) ==
                SecureOperationRegistryResult::Success &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Drill,
            205,
            -1,
            LegacySpartaHolyStoneNpc,
            &drill) ==
                SecureOperationRegistryResult::Success &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::AdvancedDrill,
            205,
            307,
            LegacySpartaHolyStoneNpc,
            &advancedDrill) ==
                SecureOperationRegistryResult::Success &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::MountGearDrill,
            205,
            -1,
            LegacySpartaHolyStoneNpc,
            &mountGearDrill) ==
                SecureOperationRegistryResult::Success &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::MountGearDrill,
            205,
            -1,
            LegacyAthensHolyStoneNpc,
            &athensMountGearDrill) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(sparta, remove) &&
        !SameOperation(remove, drill) &&
        !SameOperation(drill, advancedDrill) &&
        !SameOperation(drill, mountGearDrill) &&
        SameOperation(mountGearDrill, athensMountGearDrill) &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::AdvancedDrill,
            205,
            309,
            LegacySpartaHolyStoneNpc,
            &changedAdvancedSpell) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(advancedDrill, changedAdvancedSpell) &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::AdvancedDrill,
            205,
            307,
            LegacyAthensHolyStoneNpc,
            &athensAdvancedDrill) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(advancedDrill, athensAdvancedDrill),
        "Distinct Holy Stone families shared operation UUID");

    LegacyPacketDescriptor pageZeroDrill{};
    LegacyPacketDescriptor pageOneDrill{};
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Drill,
            16,
            -1,
            LegacySpartaHolyStoneNpc,
            &pageZeroDrill) ==
                SecureOperationRegistryResult::Success &&
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Drill,
            116,
            -1,
            LegacySpartaHolyStoneNpc,
            &pageOneDrill) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(pageZeroDrill, pageOneDrill),
        "Distinct page-zero and page-one Drill slots shared UUID");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(50, login);
    LegacyPacketDescriptor ignored{};
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &ignored) ==
            SecureOperationRegistryResult::Success &&
        registry.SetCharacter(910) ==
            SecureOperationRegistryResult::Success,
        "Holy Stone reconnect setup failed");
    LegacyPacketDescriptor reconnected{};
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Mount,
            205,
            107,
            LegacyAthensHolyStoneNpc,
            &reconnected) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(sparta, reconnected),
        "Holy Stone reconnect did not retain operation UUID");
}

void CheckPrincipalAndCharacterIsolation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Holy Stone isolation setup failed");
    LegacyPacketDescriptor original{};
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Remove,
            205,
            1,
            LegacySpartaHolyStoneNpc,
            &original) ==
            SecureOperationRegistryResult::Success,
        "Holy Stone original identity failed");

    checks->Require(
        registry.SetCharacter(911) ==
            SecureOperationRegistryResult::Success,
        "Holy Stone character switch failed");
    LegacyPacketDescriptor anotherCharacter{};
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Remove,
            205,
            1,
            LegacySpartaHolyStoneNpc,
            &anotherCharacter) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(original, anotherCharacter),
        "Two characters shared a Holy Stone UUID");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(51, login);
    LegacyPacketDescriptor ignored{};
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &ignored) ==
                SecureOperationRegistryResult::Success &&
        registry.SetCharacter(910) ==
            SecureOperationRegistryResult::Success,
        "Holy Stone principal switch failed");
    LegacyPacketDescriptor anotherPrincipal{};
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Remove,
            205,
            1,
            LegacySpartaHolyStoneNpc,
            &anotherPrincipal) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(original, anotherPrincipal),
        "Two principals shared a Holy Stone UUID");
}

void CheckSettlementAndResultCodec(Checks* checks) {
    checks->Require(
        static_cast<std::uint16_t>(
            SecureLegacyCommandFamily::MountGearDrill) == 45,
        "Mount Gear Drill changed its stable command family");
    struct FamilyCase final {
        LegacyHolyStoneAction action;
        SecureLegacyCommandFamily family;
        int secondary;
    };
    const FamilyCase families[]{
        {
            LegacyHolyStoneAction::Mount,
            SecureLegacyCommandFamily::HolyStoneMount,
            107,
        },
        {
            LegacyHolyStoneAction::Remove,
            SecureLegacyCommandFamily::HolyStoneRemove,
            1,
        },
        {
            LegacyHolyStoneAction::Drill,
            SecureLegacyCommandFamily::HolyStoneDrill,
            -1,
        },
        {
            LegacyHolyStoneAction::AdvancedDrill,
            SecureLegacyCommandFamily::HolyStoneAdvancedDrill,
            307,
        },
        {
            LegacyHolyStoneAction::MountGearDrill,
            SecureLegacyCommandFamily::MountGearDrill,
            -1,
        },
    };

    for (const auto& family : families) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        LegacyPacketDescriptor pending{};
        checks->Require(
            Establish(&registry) &&
            DescribeHolyStone(
                &registry,
                family.action,
                205,
                family.secondary,
                LegacySpartaHolyStoneNpc,
                &pending) ==
                SecureOperationRegistryResult::Success,
            "Holy Stone settlement setup failed");

        const auto result = ResultFor(pending, family.family);
        checks->Require(
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 0 &&
            registry.Snapshot().resolved == 1 &&
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success,
            "Holy Stone result did not settle idempotently");

        auto wrongFamily = result;
        wrongFamily.commandFamily =
            family.family ==
                SecureLegacyCommandFamily::HolyStoneMount
            ? SecureLegacyCommandFamily::HolyStoneRemove
            : SecureLegacyCommandFamily::HolyStoneMount;
        checks->Require(
            registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict,
            "Holy Stone tombstone accepted a wrong family");

        std::uint8_t encoded[
            SecureLegacyCommandResultPayloadBytes]{};
        SecureLegacyCommandResult decoded{};
        checks->Require(
            TryEncodeSecureLegacyCommandResult(
                result,
                encoded,
                sizeof(encoded)) &&
            TryDecodeSecureLegacyCommandResult(
                encoded,
                sizeof(encoded),
                &decoded) &&
            decoded.commandFamily == family.family &&
            std::memcmp(
                decoded.operationId,
                result.operationId,
                sizeof(result.operationId)) == 0,
            "Holy Stone result family did not round-trip");
    }
}

void CheckInvalidLookalikeAndCapacity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Holy Stone invalid/capacity setup failed");

    LegacyPacketDescriptor descriptor{};
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    auto requireRejected = [&](const char* message) {
        descriptor = LegacyPacketDescriptor{};
        const auto randomBefore = hooks.randomSeed;
        checks->Require(
            registry.DescribePacket(
                packet,
                sizeof(packet),
                &descriptor) ==
                SecureOperationRegistryResult::InvalidPacket &&
            !descriptor.hasOperation &&
            registry.Snapshot().pending == 0 &&
            hooks.randomSeed == randomBefore,
            message);
    };

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        205,
        107);
    Write16(packet, LegacyHolyStoneActionPacketBytes - 1);
    requireRejected(
        "Declared-length-mismatched Mount received a descriptor");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    Write32(packet + 16, 106);
    Write16(packet, LegacyHolyStoneActionPacketBytes - 1);
    requireRejected(
        "Declared-length-mismatched alias received a descriptor");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        205,
        107);
    Write32(packet + 20 + 1 * 4, 0);
    requireRejected(
        "Invalid Mount lookalike did not fail closed");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Remove,
        205,
        0);
    requireRejected(
        "Invalid Remove lookalike did not fail closed");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Drill,
        205,
        -1);
    Write32(packet + 20 + 10 * 4, 1);
    requireRejected(
        "Invalid Drill lookalike did not fail closed");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::MountGearDrill,
        205,
        -1);
    Write32(packet + 20 + 7 * 4, 107);
    requireRejected(
        "Invalid Mount Gear Drill lookalike did not fail closed");

    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Mount,
        -1,
        -1,
        LegacySpartaHolyStoneNpc,
        true);
    Write32(packet + 16, 106);
    requireRejected(
        "Legacy Mount alias did not fail closed");

    bool filled = true;
    for (std::size_t index = 0;
         index < SecurePendingOperationCapacity;
         ++index) {
        filled =
            DescribeHolyStone(
                &registry,
                LegacyHolyStoneAction::Mount,
                static_cast<int>(index),
                LegacyHolyStoneBagReferenceMaximum,
                LegacySpartaHolyStoneNpc,
                &descriptor) ==
                SecureOperationRegistryResult::Success &&
            filled;
    }
    checks->Require(
        filled &&
        registry.Snapshot().pending ==
            SecurePendingOperationCapacity,
        "Holy Stone operations did not fill bounded registry");

    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Mount,
            16,
            LegacyHolyStoneBagReferenceMaximum,
            LegacySpartaHolyStoneNpc,
            &descriptor) ==
            SecureOperationRegistryResult::Capacity,
        "Holy Stone operations exceeded bounded capacity");

    hooks.now += SecurePendingOperationLifetimeMilliseconds;
    checks->Require(
        DescribeHolyStone(
            &registry,
            LegacyHolyStoneAction::Mount,
            16,
            LegacyHolyStoneBagReferenceMaximum,
            LegacySpartaHolyStoneNpc,
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
        descriptor.hasOperation &&
        registry.Snapshot().pending == 1,
        "Expired Holy Stone operations retained capacity");
}

} // namespace

int RunSecureHolyStoneRegistryTests() {
    Checks checks{};
    CheckPrincipalCharacterAndNavigation(&checks);
    CheckIdentityAndCrossCityRetry(&checks);
    CheckPrincipalAndCharacterIsolation(&checks);
    CheckSettlementAndResultCodec(&checks);
    CheckInvalidLookalikeAndCapacity(&checks);
    return checks.failures;
}
