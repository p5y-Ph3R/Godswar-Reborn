#include "SecureGearEnhancerResultIdentityTests.h"

#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::LegacyPacketDescriptor;
using godswar::network::SecureLegacyCommandDisposition;
using godswar::network::SecureLegacyCommandFamily;
using godswar::network::SecureLegacyCommandResult;
using godswar::network::SecureOperationRegistryResult;
using godswar::network::SecurePendingOperationRegistry;
using godswar::network::TryDecodeSecureLegacyCommandResult;
using godswar::network::TryEncodeSecureLegacyCommandResult;

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
        destination[index] =
            static_cast<std::uint8_t>(
                value >> (index * 8U));
    }
}

void Header(
    std::uint8_t* packet,
    std::uint16_t packetBytes,
    std::uint16_t opcode) {
    Write16(packet, packetBytes);
    Write16(packet + 2, opcode);
}

void LoginPacket(std::uint8_t* packet) {
    std::memset(packet, 0, 36);
    Header(packet, 36, 10000);
    std::memcpy(packet + 4, "test2", 5);
}

void SelectionPacket(
    int bagSlot,
    std::uint8_t* packet) {
    std::memset(packet, 0, 16);
    Header(packet, 16, 10193);
    Write32(
        packet + 4,
        static_cast<std::uint32_t>(bagSlot / 24));
    Write32(
        packet + 8,
        static_cast<std::uint32_t>(bagSlot % 24));
    packet[12] = 1;
}

void PhysicalActionPacket(std::uint8_t* packet) {
    std::memset(packet, 0xCD, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, 5067);
    Write32(packet + 8, 4);
    Write32(packet + 16, 2);
}

void OriginActionPacket(std::uint8_t* packet) {
    std::memset(packet, 0xFF, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, 5140);
    Write32(packet + 8, 118);
    Write32(packet + 16, 2);
    Write32(packet + 20 + 6 * 4, 100);
    Write32(packet + 20 + 7 * 4, 125);
    Write32(packet + 20 + 8 * 4, 195);
}

struct Hooks final {
    std::uint64_t now = 30'000;
    std::uint8_t randomSeed = 81;
};

bool Random(
    void* contextValue,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* context = static_cast<Hooks*>(contextValue);
    if (context == nullptr ||
        destination == nullptr ||
        destinationBytes != 16) {
        return false;
    }
    auto* bytes = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0; index < 16; ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            context->randomSeed + index);
    }
    ++context->randomSeed;
    return true;
}

bool Clock(
    void* contextValue,
    std::uint64_t* now) noexcept {
    auto* context = static_cast<Hooks*>(contextValue);
    if (context == nullptr || now == nullptr) {
        return false;
    }
    *now = context->now;
    return true;
}

SecureOperationRegistryResult Describe(
    SecurePendingOperationRegistry* registry,
    const void* packet,
    std::size_t packetBytes,
    LegacyPacketDescriptor* descriptor = nullptr) {
    LegacyPacketDescriptor local{};
    return registry->DescribePacket(
        packet,
        packetBytes,
        descriptor == nullptr ? &local : descriptor);
}

void Establish(SecurePendingOperationRegistry* registry) {
    std::uint8_t login[36]{};
    LoginPacket(login);
    Check(
        Describe(registry, login, sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            registry->SetCharacter(505) ==
                SecureOperationRegistryResult::Success,
        "Gear Enhancer result identity setup failed");
}

bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return std::memcmp(
        first.operation.operationId,
        second.operation.operationId,
        sizeof(first.operation.operationId)) == 0;
}

SecureLegacyCommandResult ResultFor(
    const LegacyPacketDescriptor& descriptor,
    SecureLegacyCommandFamily family) {
    SecureLegacyCommandResult result{};
    result.disposition =
        SecureLegacyCommandDisposition::Applied;
    result.commandFamily = family;
    result.resultCode = 1010;
    result.inventoryRevision = 61;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

bool SnapshotHasSlots(
    SecurePendingOperationRegistry* registry,
    const int* expected) {
    const auto snapshot = registry->Snapshot();
    if (!snapshot.hasSelection ||
        snapshot.selectionCount != 3) {
        return false;
    }
    for (std::size_t index = 0; index < 3; ++index) {
        if (snapshot.selectedBagSlots[index] != expected[index]) {
            return false;
        }
    }
    return true;
}

void CheckOriginResultPreservesNewPhysicalSelection() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry);
    const int slots[]{0, 25, 95};
    std::uint8_t selection[16]{};
    for (int slot : slots) {
        SelectionPacket(slot, selection);
        Check(
            Describe(
                &registry,
                selection,
                sizeof(selection)) ==
                SecureOperationRegistryResult::Success,
            "physical selection preservation setup failed");
    }

    std::uint8_t action[92]{};
    OriginActionPacket(action);
    LegacyPacketDescriptor origin{};
    Check(
        Describe(
            &registry,
            action,
            sizeof(action),
            &origin) ==
                SecureOperationRegistryResult::Success &&
            origin.hasOperation,
        "Origin preservation operation was not created");
    Check(
        !registry.Snapshot().hasSelection &&
            !registry.Snapshot().combinePageArmed,
        "Origin commit did not reset an old physical selection");

    for (int slot : slots) {
        SelectionPacket(slot, selection);
        Check(
            Describe(
                &registry,
                selection,
                sizeof(selection)) ==
                SecureOperationRegistryResult::Success,
            "new physical selection setup failed");
    }
    Check(
        registry.Resolve(
            ResultFor(
                origin,
                SecureLegacyCommandFamily::
                    GearMentorEnhanceAttribute)) ==
                SecureOperationRegistryResult::Success &&
            SnapshotHasSlots(&registry, slots),
        "Origin result cleared physical Gear Mentor selection");

    PhysicalActionPacket(action);
    LegacyPacketDescriptor physical{};
    Check(
        Describe(
            &registry,
            action,
            sizeof(action),
            &physical) ==
                SecureOperationRegistryResult::Success &&
            physical.hasOperation &&
            !SameOperation(origin, physical),
        "physical and Origin endpoints shared an operation identity");
}

void CheckNewFamilyResultCodec() {
    const SecureLegacyCommandFamily families[]{
        SecureLegacyCommandFamily::
            GearMentorEnhanceAttribute,
        SecureLegacyCommandFamily::
            GearMentorAddAttribute,
        SecureLegacyCommandFamily::
            GearMentorDeleteAttribute,
    };

    for (const auto family : families) {
        SecureLegacyCommandResult input{};
        input.disposition =
            SecureLegacyCommandDisposition::Applied;
        input.commandFamily = family;
        input.resultCode = 1010;
        input.inventoryRevision = 73;
        for (std::size_t index = 0;
             index < sizeof(input.operationId);
             ++index) {
            input.operationId[index] =
                static_cast<std::uint8_t>(index + 1);
        }

        std::uint8_t encoded[32]{};
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
                decoded.commandFamily == family &&
                decoded.resultCode == input.resultCode &&
                decoded.inventoryRevision ==
                    input.inventoryRevision &&
                std::memcmp(
                    decoded.operationId,
                    input.operationId,
                    sizeof(input.operationId)) == 0,
            "new Gear Enhancer family result did not round-trip");
    }

    SecureLegacyCommandResult unknown{};
    unknown.disposition =
        SecureLegacyCommandDisposition::Rejected;
    unknown.commandFamily =
        static_cast<SecureLegacyCommandFamily>(14);
    unknown.operationId[0] = 1;
    std::uint8_t encoded[32]{};
    Check(
        !TryEncodeSecureLegacyCommandResult(
            unknown,
            encoded,
            sizeof(encoded)),
        "unknown command family encoded successfully");

    unknown.commandFamily =
        SecureLegacyCommandFamily::
            GearMentorEnhanceAttribute;
    Check(
        TryEncodeSecureLegacyCommandResult(
            unknown,
            encoded,
            sizeof(encoded)),
        "valid family setup did not encode");
    encoded[2] = 0;
    encoded[3] = 14;
    SecureLegacyCommandResult decoded{};
    Check(
        !TryDecodeSecureLegacyCommandResult(
            encoded,
            sizeof(encoded),
            &decoded),
        "unknown command family decoded successfully");
}

} // namespace

int RunSecureGearEnhancerResultIdentityTests() {
    Failures = 0;
    CheckOriginResultPreservesNewPhysicalSelection();
    CheckNewFamilyResultCodec();
    return Failures;
}
