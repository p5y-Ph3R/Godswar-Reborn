#include "SecureKitBagItemDeleteIdentityTests.h"

#include "../src/SecureKitBagItemDeleteIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using namespace godswar::network;

constexpr std::size_t LoginPacketBytes =
    4 + SecurePrincipalFingerprintBytes;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void Write16(
    std::uint8_t* destination,
    std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
}

void Write32(
    std::uint8_t* destination,
    std::uint32_t value) {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] = static_cast<std::uint8_t>(
            value >> (index * 8U));
    }
}

struct Hooks final {
    std::uint8_t randomSeed = 1;
    std::uint64_t now = 50'000;
};

bool Random(
    void* context,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* hooks = static_cast<Hooks*>(context);
    auto* output = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0;
         index < destinationBytes;
         ++index) {
        output[index] = static_cast<std::uint8_t>(
            hooks->randomSeed + index);
    }
    ++hooks->randomSeed;
    return true;
}

bool Clock(
    void* context,
    std::uint64_t* unixMilliseconds) noexcept {
    if (context == nullptr || unixMilliseconds == nullptr) {
        return false;
    }
    *unixMilliseconds =
        static_cast<Hooks*>(context)->now;
    return true;
}

void BuildLoginPacket(
    std::uint8_t principalSeed,
    std::uint8_t* packet) {
    std::memset(packet, 0, LoginPacketBytes);
    Write16(
        packet,
        static_cast<std::uint16_t>(LoginPacketBytes));
    Write16(packet + 2, LegacyLoginGameServerOpcode);
    for (std::size_t index = 0;
         index < SecurePrincipalFingerprintBytes;
         ++index) {
        packet[4 + index] = static_cast<std::uint8_t>(
            principalSeed + index);
    }
}

void BuildDeletePacket(
    int bagSlot,
    std::uint8_t tailSeed,
    std::uint8_t* packet) {
    std::memset(
        packet,
        0,
        LegacyKitBagItemDeletePacketBytes);
    Write16(
        packet,
        static_cast<std::uint16_t>(
            LegacyKitBagItemDeletePacketBytes));
    Write16(packet + 2, LegacyStorageItemOpcode);
    Write32(packet + 4, 0x001AF948U);
    Write16(
        packet + 8,
        static_cast<std::uint16_t>(
            bagSlot / LegacyKitBagSlotsPerPage));
    Write16(
        packet + 10,
        static_cast<std::uint16_t>(
            bagSlot % LegacyKitBagSlotsPerPage));
    Write16(packet + 12, UINT16_MAX);
    Write16(packet + 14, UINT16_MAX);
    for (std::size_t index = 16;
         index < LegacyKitBagItemDeletePacketBytes;
         ++index) {
        packet[index] = static_cast<std::uint8_t>(
            tailSeed + index);
    }
}

void BuildBagMovePacket(std::uint8_t* packet) {
    constexpr std::size_t PacketBytes = 20;
    std::memset(packet, 0, PacketBytes);
    Write16(
        packet,
        static_cast<std::uint16_t>(PacketBytes));
    Write16(packet + 2, LegacyStorageItemOpcode);
    Write16(packet + 8, 0);
    Write16(packet + 10, 1);
    Write16(packet + 12, 0);
    Write16(packet + 14, 2);
    Write16(packet + 16, UINT16_MAX);
    Write16(packet + 18, UINT16_MAX);
}

bool Establish(
    SecurePendingOperationRegistry* registry,
    std::uint8_t principalSeed = 30,
    int characterId = 700) {
    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(principalSeed, login);
    LegacyPacketDescriptor descriptor{};
    return registry != nullptr &&
        registry->DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry->SetCharacter(characterId) ==
            SecureOperationRegistryResult::Success;
}

bool DescribeDelete(
    SecurePendingOperationRegistry* registry,
    int bagSlot,
    std::uint8_t tailSeed,
    LegacyPacketDescriptor* descriptor) {
    std::uint8_t
        packet[LegacyKitBagItemDeletePacketBytes]{};
    BuildDeletePacket(bagSlot, tailSeed, packet);
    return registry != nullptr &&
        registry->DescribePacket(
            packet,
            sizeof(packet),
            descriptor) ==
            SecureOperationRegistryResult::Success;
}

bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return first.hasOperation &&
        second.hasOperation &&
        std::memcmp(
            first.operation.operationId,
            second.operation.operationId,
            sizeof(first.operation.operationId)) == 0;
}

SecureLegacyCommandResult ResultFor(
    const LegacyPacketDescriptor& descriptor,
    SecureLegacyCommandDisposition disposition,
    std::uint32_t resultCode,
    std::uint64_t inventoryRevision) {
    SecureLegacyCommandResult result{};
    result.disposition = disposition;
    result.commandFamily =
        SecureLegacyCommandFamily::KitBagItemDelete;
    result.resultCode = resultCode;
    result.inventoryRevision = inventoryRevision;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

void CheckExactParser() {
    std::uint8_t
        packet[LegacyKitBagItemDeletePacketBytes]{};
    BuildDeletePacket(0, 7, packet);
    int bagSlot = -1;
    Check(
        TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot) &&
            bagSlot == 0,
        "Kit-bag delete parser rejected captured slot zero");

    BuildDeletePacket(95, 30, packet);
    Check(
        TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot) &&
            bagSlot == 95,
        "Kit-bag delete parser rejected final bag slot");
    packet[20] ^= 0xFF;
    Check(
        TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot),
        "Kit-bag delete parser trusted unrelated trailing bytes");

    Check(
        !TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet) - 1,
            &bagSlot),
        "Kit-bag delete parser accepted a truncated packet");
    Write16(packet, 27);
    Check(
        !TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot),
        "Kit-bag delete parser accepted a mismatched length");

    BuildDeletePacket(0, 7, packet);
    Write16(packet + 2, 10051);
    Check(
        !TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot),
        "Kit-bag delete parser accepted another opcode");
    BuildDeletePacket(0, 7, packet);
    Write16(packet + 8, 4);
    Check(
        !TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot),
        "Kit-bag delete parser accepted page four");
    BuildDeletePacket(0, 7, packet);
    Write16(packet + 10, 24);
    Check(
        !TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot),
        "Kit-bag delete parser accepted slot twenty-four");
    BuildDeletePacket(0, 7, packet);
    Write16(packet + 12, 0);
    Check(
        !TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            &bagSlot),
        "Kit-bag delete parser accepted a move destination");
    Check(
        !TryReadLegacyKitBagItemDelete(
            packet,
            sizeof(packet),
            nullptr),
        "Kit-bag delete parser accepted a null output");
}

void CheckPrincipalAndReconnectIdentity() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    LegacyPacketDescriptor descriptor{};
    std::uint8_t
        deletePacket[LegacyKitBagItemDeletePacketBytes]{};
    BuildDeletePacket(25, 1, deletePacket);
    Check(
        registry.DescribePacket(
            deletePacket,
            sizeof(deletePacket),
            &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "Kit-bag delete received identity without a principal");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(30, login);
    Check(
        registry.DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            registry.DescribePacket(
                deletePacket,
                sizeof(deletePacket),
                &descriptor) ==
                SecureOperationRegistryResult::NoCharacter,
        "Kit-bag delete received identity without a character");
    Check(
        registry.SetCharacter(700) ==
            SecureOperationRegistryResult::Success,
        "Kit-bag delete character setup failed");

    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor changedTail{};
    Check(
        DescribeDelete(&registry, 25, 1, &first) &&
            first.hasOperation &&
            first.operation.packetBytes ==
                LegacyKitBagItemDeletePacketBytes &&
            first.operation.opcode ==
                LegacyStorageItemOpcode &&
            (first.operation.operationId[6] & 0xF0U) ==
                0x40U &&
            (first.operation.operationId[8] & 0xC0U) ==
                0x80U,
        "Kit-bag delete did not receive a UUID operation marker");
    Check(
        DescribeDelete(
            &registry,
            25,
            0xA0,
            &changedTail) &&
            SameOperation(first, changedTail),
        "Kit-bag delete scratch bytes changed its pending UUID");

    Check(
        registry.DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            registry.SetCharacter(700) ==
                SecureOperationRegistryResult::Success,
        "Kit-bag delete reconnect setup failed");
    LegacyPacketDescriptor reconnected{};
    Check(
        DescribeDelete(
            &registry,
            25,
            0xF0,
            &reconnected) &&
            SameOperation(first, reconnected),
        "Kit-bag delete did not reuse its UUID after reconnect");

    LegacyPacketDescriptor differentSlot{};
    Check(
        DescribeDelete(
            &registry,
            26,
            1,
            &differentSlot) &&
            !SameOperation(first, differentSlot),
        "Different kit-bag delete slots shared an operation UUID");

    std::uint8_t bagMove[20]{};
    BuildBagMovePacket(bagMove);
    LegacyPacketDescriptor moveDescriptor{};
    Check(
        registry.DescribePacket(
            bagMove,
            sizeof(bagMove),
            &moveDescriptor) ==
                SecureOperationRegistryResult::Success &&
            !moveDescriptor.hasOperation,
        "A non-delete StorageItem packet received delete identity");
}

void CheckTerminalSettlement() {
    struct Case final {
        SecureLegacyCommandDisposition disposition;
        std::uint32_t resultCode;
        std::uint64_t revision;
    };
    const Case cases[]{
        {SecureLegacyCommandDisposition::Applied, 1, 81},
        {SecureLegacyCommandDisposition::Replayed, 1, 0},
        {SecureLegacyCommandDisposition::Rejected, 2, 0},
        {SecureLegacyCommandDisposition::Conflict, 3, 0},
    };

    for (std::size_t index = 0;
         index < sizeof(cases) / sizeof(cases[0]);
         ++index) {
        Hooks hooks{};
        hooks.randomSeed =
            static_cast<std::uint8_t>(40 + index);
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        LegacyPacketDescriptor pending{};
        Check(
            Establish(&registry) &&
                DescribeDelete(
                    &registry,
                    10,
                    1,
                    &pending),
            "Kit-bag delete terminal setup failed");
        const auto& current = cases[index];
        const auto result = ResultFor(
            pending,
            current.disposition,
            current.resultCode,
            current.revision);
        Check(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success &&
                registry.Snapshot().pending == 0 &&
                registry.Snapshot().resolved == 1 &&
                registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success,
            "Kit-bag delete terminal result did not settle");

        auto wrongFamily = result;
        wrongFamily.commandFamily =
            SecureLegacyCommandFamily::EquipmentForge;
        Check(
            registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict,
            "Kit-bag delete tombstone accepted another family");

        LegacyPacketDescriptor fresh{};
        Check(
            DescribeDelete(
                &registry,
                10,
                1,
                &fresh) &&
                !SameOperation(pending, fresh),
            "Settled kit-bag delete reused its old UUID");
    }
}

void CheckFamilyThirteenResultCodec() {
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
            SecureLegacyCommandFamily::KitBagItemDelete;
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
        Check(
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
                        KitBagItemDelete &&
                decoded.resultCode == input.resultCode &&
                decoded.inventoryRevision ==
                    input.inventoryRevision &&
                std::memcmp(
                    decoded.operationId,
                    input.operationId,
                    sizeof(input.operationId)) == 0,
            "Kit-bag delete family result did not round-trip");
    }

    SecureLegacyCommandResult invalid{};
    invalid.disposition =
        SecureLegacyCommandDisposition::Applied;
    invalid.commandFamily =
        SecureLegacyCommandFamily::KitBagItemDelete;
    invalid.inventoryRevision = 0;
    invalid.operationId[0] = 1;
    std::uint8_t encoded[
        SecureLegacyCommandResultPayloadBytes]{};
    Check(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Applied kit-bag delete encoded without a revision");

    invalid.disposition =
        SecureLegacyCommandDisposition::Rejected;
    invalid.commandFamily =
        static_cast<SecureLegacyCommandFamily>(14);
    Check(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Unknown family encoded as a kit-bag delete result");
}

} // namespace

int RunSecureKitBagItemDeleteIdentityTests() {
    Failures = 0;
    CheckExactParser();
    CheckPrincipalAndReconnectIdentity();
    CheckTerminalSettlement();
    CheckFamilyThirteenResultCodec();
    return Failures;
}
