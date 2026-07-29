#include "SecureKitBagItemMoveTestSupport.h"

namespace {

using namespace kit_bag_move_test;

void CheckPrincipalAndIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    LegacyPacketDescriptor descriptor{};
    std::uint8_t compact[
        LegacyKitBagItemMoveCompactPacketBytes]{};
    BuildMovePacket(compact, sizeof(compact), 25, 70, 0);
    checks->Require(
        registry.DescribePacket(
            compact,
            sizeof(compact),
            &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "Kit-bag move received identity without a principal");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(40, login);
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            registry.DescribePacket(
                compact,
                sizeof(compact),
                &descriptor) ==
                SecureOperationRegistryResult::NoCharacter &&
            registry.SetCharacter(900) ==
                SecureOperationRegistryResult::Success,
        "Kit-bag move did not enforce persistent character identity");

    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor detailed{};
    LegacyPacketDescriptor changedTail{};
    checks->Require(
        DescribeMove(
            &registry,
            LegacyKitBagItemMoveCompactPacketBytes,
            25,
            70,
            0,
            &first) &&
            first.hasOperation &&
            first.operation.packetBytes ==
                LegacyKitBagItemMoveCompactPacketBytes &&
            first.operation.opcode ==
                LegacyStorageItemOpcode &&
            (first.operation.operationId[6] & 0xF0U) ==
                0x40U &&
            (first.operation.operationId[8] & 0xC0U) ==
                0x80U,
        "Kit-bag move did not receive a UUID operation marker");
    checks->Require(
        DescribeMove(
            &registry,
            LegacyKitBagItemMoveDetailedPacketBytes,
            25,
            70,
            0x44,
            &detailed) &&
            SameOperation(first, detailed) &&
            detailed.operation.packetBytes ==
                LegacyKitBagItemMoveDetailedPacketBytes &&
            DescribeMove(
                &registry,
                LegacyKitBagItemMoveDetailedPacketBytes,
                25,
                70,
                0x99,
                &changedTail) &&
            SameOperation(first, changedTail),
        "Equivalent move variants did not share their UUID");

    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            registry.SetCharacter(900) ==
                SecureOperationRegistryResult::Success,
        "Kit-bag move reconnect setup failed");
    LegacyPacketDescriptor reconnected{};
    checks->Require(
        DescribeMove(
            &registry,
            LegacyKitBagItemMoveCompactPacketBytes,
            25,
            70,
            0,
            &reconnected) &&
            SameOperation(first, reconnected),
        "Kit-bag move did not reuse its UUID after reconnect");

    LegacyPacketDescriptor reverse{};
    LegacyPacketDescriptor differentDestination{};
    checks->Require(
        DescribeMove(
            &registry,
            LegacyKitBagItemMoveCompactPacketBytes,
            70,
            25,
            0,
            &reverse) &&
            !SameOperation(first, reverse) &&
            DescribeMove(
                &registry,
                LegacyKitBagItemMoveCompactPacketBytes,
                25,
                71,
                0,
                &differentDestination) &&
            !SameOperation(first, differentDestination),
        "Ordered kit-bag move coordinates aliased another operation");
}

void CheckPrincipalAndCharacterIsolation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        Establish(&registry, 40, 900),
        "Kit-bag move identity-isolation setup failed");
    LegacyPacketDescriptor original{};
    checks->Require(
        DescribeMove(
            &registry,
            LegacyKitBagItemMoveCompactPacketBytes,
            3,
            4,
            0,
            &original),
        "Kit-bag move original identity setup failed");

    checks->Require(
        registry.SetCharacter(901) ==
            SecureOperationRegistryResult::Success,
        "Kit-bag move character switch failed");
    LegacyPacketDescriptor anotherCharacter{};
    checks->Require(
        DescribeMove(
            &registry,
            LegacyKitBagItemMoveCompactPacketBytes,
            3,
            4,
            0,
            &anotherCharacter) &&
            !SameOperation(original, anotherCharacter),
        "Two characters shared a kit-bag move UUID");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(41, login);
    LegacyPacketDescriptor ignored{};
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &ignored) ==
                SecureOperationRegistryResult::Success &&
            registry.SetCharacter(900) ==
                SecureOperationRegistryResult::Success,
        "Kit-bag move principal switch failed");
    LegacyPacketDescriptor anotherPrincipal{};
    checks->Require(
        DescribeMove(
            &registry,
            LegacyKitBagItemMoveCompactPacketBytes,
            3,
            4,
            0,
            &anotherPrincipal) &&
            !SameOperation(original, anotherPrincipal),
        "Two principals shared a kit-bag move UUID");
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
                DescribeMove(
                    &registry,
                    LegacyKitBagItemMoveCompactPacketBytes,
                    7,
                    8,
                    0,
                    &pending),
            "Kit-bag move terminal setup failed");

        std::uint8_t selection[16]{};
        Write16(selection, sizeof(selection));
        Write16(selection + 2, LegacyGearSelectionOpcode);
        Write32(selection + 4, 0);
        Write32(selection + 8, 5);
        selection[12] = 1;
        LegacyPacketDescriptor ignored{};
        checks->Require(
            registry.DescribePacket(
                selection,
                sizeof(selection),
                &ignored) ==
                SecureOperationRegistryResult::Success,
            "Kit-bag move selection-isolation setup failed");

        const auto result = ResultFor(
            pending,
            dispositions[index],
            1,
            dispositions[index] ==
                    SecureLegacyCommandDisposition::Applied
                ? 81U
                : 0U);
        checks->Require(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success &&
                registry.Snapshot().pending == 0 &&
                registry.Snapshot().resolved == 1 &&
                registry.Snapshot().selectionCount == 1 &&
                registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success,
            "Kit-bag move terminal result did not settle generically");

        auto wrongFamily = result;
        wrongFamily.commandFamily =
            SecureLegacyCommandFamily::KitBagItemDelete;
        checks->Require(
            registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict,
            "Kit-bag move tombstone accepted another family");

        LegacyPacketDescriptor fresh{};
        checks->Require(
            DescribeMove(
                &registry,
                LegacyKitBagItemMoveCompactPacketBytes,
                7,
                8,
                0,
                &fresh) &&
                !SameOperation(pending, fresh),
            "Settled kit-bag move reused its old UUID");
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
        "Kit-bag move capacity setup failed");
    LegacyPacketDescriptor descriptor{};
    bool filled = true;
    for (std::size_t index = 0;
         index < SecurePendingOperationCapacity;
         ++index) {
        filled =
            DescribeMove(
                &registry,
                LegacyKitBagItemMoveCompactPacketBytes,
                0,
                static_cast<int>(index + 1),
                0,
                &descriptor) &&
            filled;
    }
    checks->Require(
        filled &&
            registry.Snapshot().pending ==
                SecurePendingOperationCapacity,
        "Kit-bag move did not fill the bounded registry");

    std::uint8_t packet[
        LegacyKitBagItemMoveCompactPacketBytes]{};
    BuildMovePacket(packet, sizeof(packet), 0, 17, 0);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) ==
            SecureOperationRegistryResult::Capacity,
        "Kit-bag move exceeded the bounded registry");

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
        "Expired kit-bag moves did not release bounded capacity");
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
            SecureLegacyCommandFamily::KitBagItemMove;
        input.resultCode = 9;
        input.inventoryRevision =
            disposition ==
                SecureLegacyCommandDisposition::Applied
            ? 27U
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
                    SecureLegacyCommandFamily::KitBagItemMove &&
                decoded.resultCode == input.resultCode &&
                decoded.inventoryRevision ==
                    input.inventoryRevision &&
                std::memcmp(
                    decoded.operationId,
                    input.operationId,
                    sizeof(input.operationId)) == 0,
            "Kit-bag move family result did not round-trip");
    }

    SecureLegacyCommandResult invalid{};
    invalid.disposition =
        SecureLegacyCommandDisposition::Applied;
    invalid.commandFamily =
        SecureLegacyCommandFamily::KitBagItemMove;
    invalid.operationId[0] = 1;
    std::uint8_t encoded[
        SecureLegacyCommandResultPayloadBytes]{};
    checks->Require(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Applied kit-bag move encoded without a revision");

    invalid.disposition =
        SecureLegacyCommandDisposition::Rejected;
    invalid.commandFamily =
        static_cast<SecureLegacyCommandFamily>(15);
    checks->Require(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Unknown family encoded as a kit-bag move result");
}

} // namespace

int RunSecureKitBagItemMoveRegistryTests() {
    Checks checks{};
    CheckPrincipalAndIdentity(&checks);
    CheckPrincipalAndCharacterIsolation(&checks);
    CheckTerminalSettlement(&checks);
    CheckCapacityAndExpiry(&checks);
    CheckResultCodec(&checks);
    return checks.failures;
}
