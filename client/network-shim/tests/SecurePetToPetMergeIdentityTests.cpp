#include "SecurePetToPetMergeIdentityTests.h"

#include "../src/SecureLegacyCommandIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"
#include "../src/SecurePetCommandIdentity.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using namespace godswar::network;

struct Checks final {
    int failures = 0;
    void Require(bool condition, const char* message) {
        if (!condition) {
            std::fprintf(stderr, "FAIL: %s\n", message);
            ++failures;
        }
    }
};

struct Hooks final {
    std::uint8_t seed = 1;
    std::uint64_t now = 90'000;
};

bool Random(
    void* context,
    void* destination,
    std::size_t bytes) noexcept {
    auto* hooks = static_cast<Hooks*>(context);
    if (hooks == nullptr || destination == nullptr || bytes != 16) {
        return false;
    }
    auto* output = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0; index < bytes; ++index) {
        output[index] = static_cast<std::uint8_t>(hooks->seed + index);
    }
    ++hooks->seed;
    return true;
}

bool Clock(void* context, std::uint64_t* now) noexcept {
    if (context == nullptr || now == nullptr) {
        return false;
    }
    *now = static_cast<Hooks*>(context)->now;
    return true;
}

void Write16(std::uint8_t* target, std::uint16_t value) {
    target[0] = static_cast<std::uint8_t>(value);
    target[1] = static_cast<std::uint8_t>(value >> 8U);
}

void Write32(std::uint8_t* target, std::uint32_t value) {
    target[0] = static_cast<std::uint8_t>(value);
    target[1] = static_cast<std::uint8_t>(value >> 8U);
    target[2] = static_cast<std::uint8_t>(value >> 16U);
    target[3] = static_cast<std::uint8_t>(value >> 24U);
}

std::uint32_t Read32(const std::uint8_t* source) {
    return source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

void Header(
    std::uint8_t* packet,
    std::uint16_t bytes,
    std::uint16_t opcode) {
    Write16(packet, bytes);
    Write16(packet + 2, opcode);
}

void BuildMerge(
    std::uint8_t* packet,
    std::uint32_t primary = 17,
    std::uint32_t deputy = 18,
    std::uint32_t material = LegacyMergedSpiritItemId,
    std::uint8_t quantity = 5) {
    std::memset(packet, 0, LegacyPetToPetMergePacketBytes);
    Header(
        packet,
        LegacyPetToPetMergePacketBytes,
        LegacyPetToPetMergeOpcode);
    Write32(packet + 4, primary);
    Write32(packet + 8, deputy);
    Write32(packet + 12, material);
    packet[16] = quantity;
}

void BuildLogin(std::uint8_t* packet) {
    std::memset(packet, 0, 36);
    Header(packet, 36, LegacyLoginGameServerOpcode);
    std::memcpy(packet + 4, "test2", 5);
}

bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return first.hasOperation && second.hasOperation &&
        std::memcmp(
            first.operation.operationId,
            second.operation.operationId,
            sizeof(first.operation.operationId)) == 0;
}

LegacyPetCommandPacketKind Classify(
    std::uint8_t* packet,
    LegacyPetCommandIntent* intent) {
    return ClassifyLegacyPetCommandPacket(
        packet,
        LegacyPetToPetMergePacketBytes,
        intent);
}

void CheckExactIntent(Checks* checks) {
    std::uint8_t packet[LegacyPetToPetMergePacketBytes]{};
    BuildMerge(packet);
    LegacyPetCommandIntent intent{};
    checks->Require(
        Classify(packet, &intent) == LegacyPetCommandPacketKind::Command &&
        intent.family == SecureLegacyCommandFamily::PetToPetMerge &&
        intent.bytes[0] == 1 && intent.bytes[1] == 1 &&
        Read32(intent.bytes + 2) == 17 &&
        Read32(intent.bytes + 6) == 18 &&
        Read32(intent.bytes + 10) == LegacyMergedSpiritItemId &&
        intent.bytes[14] == 5 && intent.bytes[15] == 0,
        "exact native pet-to-pet Merge intent was not classified");

    std::uint8_t restricted[LegacyPetToPetMergePacketBytes]{};
    BuildMerge(restricted, 17, 18, LegacyFusedHarpyiaItemId, 1);
    LegacyPetCommandIntent restrictedIntent{};
    checks->Require(
        Classify(restricted, &restrictedIntent) ==
            LegacyPetCommandPacketKind::Command &&
        !EqualPetCommandIntent(intent, restrictedIntent),
        "restricted Merge material aliased standard intent");

    std::uint8_t changed[LegacyPetToPetMergePacketBytes]{};
    BuildMerge(changed, 19, 20, LegacyMergedSpiritItemId, 4);
    LegacyPetCommandIntent changedIntent{};
    checks->Require(
        Classify(changed, &changedIntent) ==
            LegacyPetCommandPacketKind::Command &&
        !EqualPetCommandIntent(intent, changedIntent),
        "different pet Merge inputs aliased one canonical intent");

    std::uint8_t noMaterial[LegacyPetToPetMergePacketBytes]{};
    BuildMerge(noMaterial, 17, 18, 0, 0);
    LegacyPetCommandIntent noMaterialIntent{};
    checks->Require(
        Classify(noMaterial, &noMaterialIntent) ==
            LegacyPetCommandPacketKind::Command &&
        noMaterialIntent.family ==
            SecureLegacyCommandFamily::PetToPetMerge &&
        Read32(noMaterialIntent.bytes + 10) == 0 &&
        noMaterialIntent.bytes[14] == 0 &&
        !EqualPetCommandIntent(intent, noMaterialIntent),
        "native no-spirit pet Merge was not classified distinctly");
}

void CheckMalformedInputs(Checks* checks) {
    std::uint8_t packet[LegacyPetToPetMergePacketBytes]{};
    LegacyPetCommandIntent intent{};

    BuildMerge(packet, 0, 18);
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted a zero primary ID");
    BuildMerge(packet, 17, 17);
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted the same primary and deputy");
    BuildMerge(packet, 0x8000'0000U, 18);
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted a signed-incompatible pet ID");
    BuildMerge(packet, 17, 18, 12345, 5);
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted an unreviewed material template");
    BuildMerge(packet, 17, 18, LegacyMergedSpiritItemId, 0);
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted a material template with zero quantity");
    BuildMerge(packet, 17, 18, 0, 1);
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted a zero template with nonzero quantity");
    BuildMerge(packet, 17, 18, LegacyMergedSpiritItemId, 6);
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted more than five materials");
    BuildMerge(packet);
    packet[19] = 1;
    checks->Require(
        Classify(packet, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted nonzero tail padding");

    std::uint8_t shortPacket[19]{};
    Header(shortPacket, 19, LegacyPetToPetMergeOpcode);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            shortPacket,
            sizeof(shortPacket),
            &intent) == LegacyPetCommandPacketKind::InvalidMutation,
        "pet Merge accepted a truncated frame");
}

void CheckRegistryLifecycle(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    std::uint8_t login[36]{};
    BuildLogin(login);
    LegacyPacketDescriptor ignored{};
    checks->Require(
        registry.DescribePacket(login, sizeof(login), &ignored) ==
            SecureOperationRegistryResult::Success &&
        registry.SetCharacter(2) ==
            SecureOperationRegistryResult::Success,
        "pet Merge registry setup failed");

    std::uint8_t packet[LegacyPetToPetMergePacketBytes]{};
    BuildMerge(packet);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.DescribePacket(packet, sizeof(packet), &first) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        first.operation.opcode == LegacyPetToPetMergeOpcode &&
        SameOperation(first, retry),
        "pet Merge retry did not reuse its operation UUID");

    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily = SecureLegacyCommandFamily::PetToPetMerge;
    result.inventoryRevision = 1;
    std::memcpy(
        result.operationId,
        first.operation.operationId,
        sizeof(result.operationId));
    LegacyPacketDescriptor next{};
    checks->Require(
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &next) ==
            SecureOperationRegistryResult::Success &&
        next.hasOperation && !SameOperation(first, next),
        "PetToPetMerge result did not settle operation UUID");
}

} // namespace

int RunSecurePetToPetMergeIdentityTests() {
    Checks checks{};
    CheckExactIntent(&checks);
    CheckMalformedInputs(&checks);
    CheckRegistryLifecycle(&checks);
    return checks.failures;
}
