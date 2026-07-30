#include "SecureZodiacSkillGridUpgradeTestSupport.h"

namespace {

using namespace zodiac_upgrade_test;

void CheckPrincipalCharacterAndUnrelatedPackets(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    BuildUpgradePacket(packet, 1);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "Zodiac upgrade did not require a principal");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(70, login);
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
        registry.SetCharacter(920) ==
            SecureOperationRegistryResult::Success,
        "Zodiac upgrade did not require a character");

    Write16(packet + 8, 0);
    Write16(packet + 10, 100);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 0,
        "Zodiac activation received an upgrade marker");

    Write16(packet + 8, LegacyZodiacNativeModule);
    Write16(packet + 10, 102);
    Write32(packet + 16, 10'057);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
        descriptor.hasOperation &&
        registry.Snapshot().pending == 1,
        "Zodiac skill selection was not routed to its own identity");
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
        "Zodiac upgrade identity setup failed");

    LegacyPacketDescriptor native{};
    LegacyPacketDescriptor compatible{};
    LegacyPacketDescriptor changedPlayer{};
    checks->Require(
        DescribeUpgrade(&registry, 1, &native) ==
                SecureOperationRegistryResult::Success &&
        native.hasOperation &&
        native.operation.packetBytes ==
            LegacyZodiacPacketBytes &&
        native.operation.opcode == LegacyZodiacOpcode &&
        (native.operation.operationId[6] & 0xF0U) == 0x40U &&
        (native.operation.operationId[8] & 0xC0U) == 0x80U,
        "Zodiac upgrade did not receive a UUID marker");
    checks->Require(
        DescribeUpgrade(
            &registry,
            1,
            &compatible,
            LegacyZodiacCompatibilityModule) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(native, compatible) &&
        DescribeUpgrade(
            &registry,
            1,
            &changedPlayer,
            LegacyZodiacNativeModule,
            0x12345678U) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(native, changedPlayer),
        "Equivalent Zodiac upgrade packets changed UUID");

    LegacyPacketDescriptor anotherGrid{};
    checks->Require(
        DescribeUpgrade(&registry, 2, &anotherGrid) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(native, anotherGrid),
        "Two Zodiac grids shared one operation UUID");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(70, login);
    LegacyPacketDescriptor ignored{};
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &ignored) ==
                SecureOperationRegistryResult::Success &&
        registry.SetCharacter(920) ==
            SecureOperationRegistryResult::Success,
        "Zodiac upgrade reconnect setup failed");
    LegacyPacketDescriptor reconnected{};
    checks->Require(
        DescribeUpgrade(&registry, 1, &reconnected) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(native, reconnected),
        "Zodiac upgrade did not retain UUID across reconnect");
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
        "Zodiac upgrade isolation setup failed");
    LegacyPacketDescriptor original{};
    checks->Require(
        DescribeUpgrade(&registry, 4, &original) ==
            SecureOperationRegistryResult::Success,
        "Zodiac upgrade original identity failed");

    checks->Require(
        registry.SetCharacter(921) ==
            SecureOperationRegistryResult::Success,
        "Zodiac upgrade character switch failed");
    LegacyPacketDescriptor anotherCharacter{};
    checks->Require(
        DescribeUpgrade(
            &registry,
            4,
            &anotherCharacter) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(original, anotherCharacter),
        "Two characters shared a Zodiac upgrade UUID");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(71, login);
    LegacyPacketDescriptor ignored{};
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &ignored) ==
                SecureOperationRegistryResult::Success &&
        registry.SetCharacter(920) ==
            SecureOperationRegistryResult::Success,
        "Zodiac upgrade principal switch failed");
    LegacyPacketDescriptor anotherPrincipal{};
    checks->Require(
        DescribeUpgrade(
            &registry,
            4,
            &anotherPrincipal) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(original, anotherPrincipal),
        "Two principals shared a Zodiac upgrade UUID");
}

void CheckInvalidMutationDoesNotAllocate(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Zodiac invalid-mutation setup failed");

    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    LegacyPacketDescriptor descriptor{};
    BuildUpgradePacket(packet, 16);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::InvalidPacket &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 0,
        "Invalid Zodiac grid allocated operation state");

    BuildUpgradePacket(packet, 1);
    Write32(packet + 20, 1);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::InvalidPacket &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 0,
        "Malformed Zodiac tail allocated operation state");
}

void CheckTerminalSettlement(Checks* checks) {
    const SecureLegacyCommandDisposition dispositions[]{
        SecureLegacyCommandDisposition::Applied,
        SecureLegacyCommandDisposition::Replayed,
        SecureLegacyCommandDisposition::Rejected,
        SecureLegacyCommandDisposition::Conflict,
    };
    for (std::size_t index = 0;
         index < sizeof(dispositions) / sizeof(dispositions[0]);
         ++index) {
        Hooks hooks{};
        hooks.randomSeed =
            static_cast<std::uint8_t>(80 + index);
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        LegacyPacketDescriptor pending{};
        checks->Require(
            Establish(&registry) &&
            DescribeUpgrade(&registry, 7, &pending) ==
                SecureOperationRegistryResult::Success,
            "Zodiac upgrade settlement setup failed");

        const auto result =
            ResultFor(pending, dispositions[index]);
        checks->Require(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 0 &&
            registry.Snapshot().resolved == 1 &&
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success,
            "Zodiac upgrade result did not settle idempotently");

        auto wrongFamily = result;
        wrongFamily.commandFamily =
            SecureLegacyCommandFamily::HolyStoneDrill;
        checks->Require(
            registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict,
            "Zodiac upgrade tombstone accepted another family");

        LegacyPacketDescriptor fresh{};
        checks->Require(
            DescribeUpgrade(&registry, 7, &fresh) ==
                    SecureOperationRegistryResult::Success &&
            !SameOperation(pending, fresh),
            "Settled Zodiac upgrade reused its old UUID");
    }
}

void CheckCapacityAndExpiry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Zodiac upgrade capacity setup failed");
    LegacyPacketDescriptor descriptor{};
    bool filled = true;
    for (int grid = LegacyZodiacSkillGridMinimum;
         grid <= LegacyZodiacSkillGridMaximum;
         ++grid) {
        filled =
            DescribeUpgrade(&registry, grid, &descriptor) ==
                SecureOperationRegistryResult::Success &&
            filled;
    }
    checks->Require(
        filled &&
        registry.Snapshot().pending ==
            SecurePendingOperationCapacity,
        "Zodiac upgrades did not fill bounded capacity");

    checks->Require(
        registry.SetCharacter(921) ==
            SecureOperationRegistryResult::Success &&
        DescribeUpgrade(&registry, 0, &descriptor) ==
            SecureOperationRegistryResult::Capacity,
        "Zodiac upgrades exceeded bounded capacity");

    hooks.now += SecurePendingOperationLifetimeMilliseconds;
    checks->Require(
        DescribeUpgrade(&registry, 0, &descriptor) ==
                SecureOperationRegistryResult::Success &&
        descriptor.hasOperation &&
        registry.Snapshot().pending == 1,
        "Expired Zodiac upgrades did not release capacity");
}

void CheckResultCodec(Checks* checks) {
    const SecureLegacyCommandDisposition dispositions[]{
        SecureLegacyCommandDisposition::Applied,
        SecureLegacyCommandDisposition::Replayed,
        SecureLegacyCommandDisposition::Rejected,
        SecureLegacyCommandDisposition::Conflict,
    };
    for (const auto disposition : dispositions) {
        SecureLegacyCommandResult input{};
        input.disposition = disposition;
        input.commandFamily =
            SecureLegacyCommandFamily::ZodiacSkillGridUpgrade;
        input.resultCode = 20;
        input.inventoryRevision =
            disposition == SecureLegacyCommandDisposition::Applied
            ? 49
            : 0;
        for (std::size_t index = 0;
             index < sizeof(input.operationId);
             ++index) {
            input.operationId[index] =
                static_cast<std::uint8_t>(index + 1);
        }

        std::uint8_t encoded[
            SecureLegacyCommandResultPayloadBytes]{};
        SecureLegacyCommandResult decoded{};
        checks->Require(
            TryEncodeSecureLegacyCommandResult(
                input,
                encoded,
                sizeof(encoded)) &&
            TryDecodeSecureLegacyCommandResult(
                encoded,
                sizeof(encoded),
                &decoded) &&
            decoded.disposition == disposition &&
            decoded.commandFamily ==
                SecureLegacyCommandFamily::
                    ZodiacSkillGridUpgrade &&
            decoded.resultCode == input.resultCode &&
            decoded.inventoryRevision ==
                input.inventoryRevision &&
            std::memcmp(
                decoded.operationId,
                input.operationId,
                sizeof(input.operationId)) == 0,
            "Zodiac upgrade result family did not round-trip");
    }

    SecureLegacyCommandResult invalid{};
    invalid.disposition =
        SecureLegacyCommandDisposition::Applied;
    invalid.commandFamily =
        SecureLegacyCommandFamily::ZodiacSkillGridUpgrade;
    invalid.operationId[0] = 1;
    std::uint8_t encoded[
        SecureLegacyCommandResultPayloadBytes]{};
    checks->Require(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Applied Zodiac upgrade encoded without revision");

    invalid.disposition =
        SecureLegacyCommandDisposition::Rejected;
    invalid.commandFamily =
        static_cast<SecureLegacyCommandFamily>(19);
    checks->Require(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Managed-only family 19 encoded as a native result");
}

} // namespace

int RunSecureZodiacSkillGridUpgradeRegistryTests() {
    Checks checks{};
    CheckPrincipalCharacterAndUnrelatedPackets(&checks);
    CheckIdentityAndReconnect(&checks);
    CheckPrincipalAndCharacterIsolation(&checks);
    CheckInvalidMutationDoesNotAllocate(&checks);
    CheckTerminalSettlement(&checks);
    CheckCapacityAndExpiry(&checks);
    CheckResultCodec(&checks);
    return checks.failures;
}
