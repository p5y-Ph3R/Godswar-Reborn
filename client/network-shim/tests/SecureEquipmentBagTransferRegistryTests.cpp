#include "SecureEquipmentBagTransferTestSupport.h"

namespace {

using namespace equipment_bag_transfer_test;

void CheckSharedOpcode10051UsesBagActivationIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry),
        "Opcode 10051 regression setup failed");

    // captures/working-multiplayer-20260514-193356.log:5840-5841.
    // This is the captured client-to-server right-click request. Server-side
    // routing also uses opcode 10051 for pet-egg activation. The stable intent
    // is the authoritative bag slot; the server decides whether that slot is
    // equipment or an egg without trusting the client item hint.
    const std::uint8_t packet[92]{
        0x5C, 0x00, 0x43, 0x27, 0x49, 0xF9, 0x93, 0x77,
        0xDB, 0x05, 0x00, 0x00, 0x01, 0x00, 0x17, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xA0, 0xF8, 0x93, 0x77, 0xC4, 0xF6, 0x1A, 0x00,
        0x88, 0xFB, 0x1A, 0x00, 0x2C, 0xFA, 0x1A, 0x00,
        0xB0, 0x1B, 0x98, 0x77, 0x79, 0xA9, 0x6D, 0x51,
        0xFE, 0xFF, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00,
        0xCF, 0x96, 0x7B, 0x00, 0x00, 0x00, 0xCE, 0x03,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x7B, 0x27, 0x00, 0x00, 0x2C, 0x5C, 0x7A, 0x0C,
        0xD8, 0xA3, 0x06, 0x14, 0x24, 0xF7, 0x1A, 0x00,
        0x90, 0x7C, 0x7B, 0x00};
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            descriptor.packetBytes == sizeof(packet) &&
            descriptor.opcode == 10051 &&
            descriptor.hasOperation &&
            registry.Snapshot().pending == 1,
        "Shared opcode 10051 missed its bag-activation operation marker");
}

void CheckPrincipalCharacterAndIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    LegacyPacketDescriptor descriptor{};
    std::uint8_t
        packet[LegacyEquipmentBagTransferPacketBytes]{};
    BuildTransferPacket(packet, sizeof(packet), 10, 55, 0x11);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "Equipment transfer received identity without principal");

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
        "Equipment transfer did not require character identity");

    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor changedOpaque{};
    checks->Require(
        DescribeTransfer(
            &registry,
            10,
            55,
            0x11,
            &first) &&
            first.hasOperation &&
            first.operation.packetBytes ==
                LegacyEquipmentBagTransferPacketBytes &&
            first.operation.opcode ==
                LegacyStorageItemOpcode &&
            (first.operation.operationId[6] & 0xF0U) ==
                0x40U &&
            (first.operation.operationId[8] & 0xC0U) ==
                0x80U &&
            DescribeTransfer(
                &registry,
                10,
                55,
                0xA5,
                &changedOpaque) &&
            SameOperation(first, changedOpaque),
        "Equivalent equipment transfers did not share UUID");

    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            registry.SetCharacter(910) ==
                SecureOperationRegistryResult::Success,
        "Equipment transfer reconnect setup failed");
    LegacyPacketDescriptor reconnected{};
    checks->Require(
        DescribeTransfer(
            &registry,
            10,
            55,
            0x44,
            &reconnected) &&
            SameOperation(first, reconnected),
        "Equipment transfer did not reuse UUID after reconnect");

    LegacyPacketDescriptor anotherEquipment{};
    LegacyPacketDescriptor anotherBag{};
    checks->Require(
        DescribeTransfer(
            &registry,
            11,
            55,
            0,
            &anotherEquipment) &&
            !SameOperation(first, anotherEquipment) &&
            DescribeTransfer(
                &registry,
                10,
                56,
                0,
                &anotherBag) &&
            !SameOperation(first, anotherBag),
        "Equipment and bag roles aliased another transfer");
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
        "Equipment transfer isolation setup failed");
    LegacyPacketDescriptor original{};
    checks->Require(
        DescribeTransfer(
            &registry,
            3,
            47,
            0,
            &original),
        "Equipment transfer original identity failed");

    checks->Require(
        registry.SetCharacter(911) ==
            SecureOperationRegistryResult::Success,
        "Equipment transfer character switch failed");
    LegacyPacketDescriptor anotherCharacter{};
    checks->Require(
        DescribeTransfer(
            &registry,
            3,
            47,
            0,
            &anotherCharacter) &&
            !SameOperation(original, anotherCharacter),
        "Two characters shared equipment-transfer UUID");

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
        "Equipment transfer principal switch failed");
    LegacyPacketDescriptor anotherPrincipal{};
    checks->Require(
        DescribeTransfer(
            &registry,
            3,
            47,
            0,
            &anotherPrincipal) &&
            !SameOperation(original, anotherPrincipal),
        "Two principals shared equipment-transfer UUID");
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
            static_cast<std::uint8_t>(60 + index);
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        LegacyPacketDescriptor pending{};
        checks->Require(
            Establish(&registry) &&
                DescribeTransfer(
                    &registry,
                    10,
                    55,
                    0,
                    &pending),
            "Equipment transfer terminal setup failed");

        const auto result = ResultFor(
            pending,
            dispositions[index],
            1,
            dispositions[index] ==
                    SecureLegacyCommandDisposition::Applied
                ? 91U
                : 0U);
        checks->Require(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success &&
                registry.Snapshot().pending == 0 &&
                registry.Snapshot().resolved == 1 &&
                registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success,
            "Equipment transfer result did not settle");

        auto wrongFamily = result;
        wrongFamily.commandFamily =
            SecureLegacyCommandFamily::KitBagItemMove;
        checks->Require(
            registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict,
            "Equipment transfer tombstone accepted wrong family");

        LegacyPacketDescriptor fresh{};
        checks->Require(
            DescribeTransfer(
                &registry,
                10,
                55,
                0,
                &fresh) &&
                !SameOperation(pending, fresh),
            "Settled equipment transfer reused old UUID");
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
        "Equipment transfer capacity setup failed");
    LegacyPacketDescriptor descriptor{};
    bool filled = true;
    for (std::size_t index = 0;
         index < SecurePendingOperationCapacity;
         ++index) {
        filled =
            DescribeTransfer(
                &registry,
                10,
                static_cast<int>(index),
                0,
                &descriptor) &&
            filled;
    }
    checks->Require(
        filled &&
            registry.Snapshot().pending ==
                SecurePendingOperationCapacity,
        "Equipment transfers did not fill bounded registry");

    std::uint8_t
        packet[LegacyEquipmentBagTransferPacketBytes]{};
    BuildTransferPacket(packet, sizeof(packet), 10, 16, 0);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
            SecureOperationRegistryResult::Capacity,
        "Equipment transfers exceeded bounded capacity");

    hooks.now +=
        SecurePendingOperationLifetimeMilliseconds;
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            descriptor.hasOperation &&
            registry.Snapshot().pending == 1,
        "Expired equipment transfers retained capacity");
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
            SecureLegacyCommandFamily::EquipmentBagTransfer;
        input.resultCode = 12;
        input.inventoryRevision =
            disposition ==
                    SecureLegacyCommandDisposition::Applied
                ? 31U
                : 0U;
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
                        EquipmentBagTransfer &&
                decoded.resultCode == input.resultCode &&
                decoded.inventoryRevision ==
                    input.inventoryRevision &&
                std::memcmp(
                    decoded.operationId,
                    input.operationId,
                    sizeof(input.operationId)) == 0,
            "Equipment transfer family did not round-trip");
    }

    SecureLegacyCommandResult invalid{};
    invalid.disposition =
        SecureLegacyCommandDisposition::Applied;
    invalid.commandFamily =
        SecureLegacyCommandFamily::EquipmentBagTransfer;
    invalid.operationId[0] = 1;
    std::uint8_t encoded[
        SecureLegacyCommandResultPayloadBytes]{};
    checks->Require(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Applied equipment transfer encoded without revision");

    invalid.disposition =
        SecureLegacyCommandDisposition::Rejected;
    invalid.commandFamily =
        static_cast<SecureLegacyCommandFamily>(19);
    checks->Require(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Unknown family encoded as equipment transfer");
}

} // namespace

int RunSecureEquipmentBagTransferRegistryTests() {
    Checks checks{};
    CheckSharedOpcode10051UsesBagActivationIdentity(&checks);
    CheckPrincipalCharacterAndIdentity(&checks);
    CheckPrincipalAndCharacterIsolation(&checks);
    CheckTerminalSettlement(&checks);
    CheckCapacityAndExpiry(&checks);
    CheckResultCodec(&checks);
    return checks.failures;
}
