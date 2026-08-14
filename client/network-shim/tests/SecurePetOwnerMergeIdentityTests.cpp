#include "SecurePetOwnerMergeIdentityTests.h"

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
        output[index] =
            static_cast<std::uint8_t>(hooks->seed + index);
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

void Header(
    std::uint8_t* packet,
    std::uint16_t bytes,
    std::uint16_t opcode) {
    Write16(packet, bytes);
    Write16(packet + 2, opcode);
}

void BuildOwnerMerge(std::uint8_t* packet) {
    std::memset(packet, 0, LegacyPetOwnerMergePacketBytes);
    Header(
        packet,
        LegacyPetOwnerMergePacketBytes,
        LegacyPetOwnerMergeOpcode);
}

void BuildLogin(std::uint8_t* packet) {
    std::memset(packet, 0, 36);
    Header(packet, 36, LegacyLoginGameServerOpcode);
    std::memcpy(packet + 4, "test2", 5);
}

void BuildActivation(std::uint8_t* packet) {
    std::memset(packet, 0, LegacyBagItemActivationPacketBytes);
    Header(
        packet,
        LegacyBagItemActivationPacketBytes,
        LegacyBagItemActivationOpcode);
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

void CheckExactClassifier(Checks* checks) {
    std::uint8_t packet[LegacyPetOwnerMergePacketBytes]{};
    BuildOwnerMerge(packet);
    LegacyPetCommandIntent mergeIntent{};
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet,
            sizeof(packet),
            &mergeIntent) == LegacyPetCommandPacketKind::Command &&
        mergeIntent.family ==
            SecureLegacyCommandFamily::PetOwnerMergeToggle &&
        mergeIntent.bytes[0] == 1 &&
        mergeIntent.bytes[1] == 1,
        "exact native owner-merge request was not classified");

    std::uint8_t activation[LegacyBagItemActivationPacketBytes]{};
    BuildActivation(activation);
    LegacyPetCommandIntent activationIntent{};
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            activation,
            sizeof(activation),
            &activationIntent) == LegacyPetCommandPacketKind::Command &&
        !EqualPetCommandIntent(mergeIntent, activationIntent),
        "owner merge aliased opcode-10051 canonical intent");

    std::uint8_t oversized[8]{};
    Header(oversized, sizeof(oversized), LegacyPetOwnerMergeOpcode);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            oversized,
            sizeof(oversized),
            &mergeIntent) == LegacyPetCommandPacketKind::InvalidMutation,
        "owner merge accepted an eight-byte frame");

    std::uint8_t trailingByte[5]{};
    Header(trailingByte, sizeof(trailingByte), LegacyPetOwnerMergeOpcode);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            trailingByte,
            sizeof(trailingByte),
            &mergeIntent) == LegacyPetCommandPacketKind::InvalidMutation,
        "owner merge accepted a trailing payload byte");
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
        "owner-merge registry setup failed");

    std::uint8_t packet[LegacyPetOwnerMergePacketBytes]{};
    BuildOwnerMerge(packet);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.DescribePacket(packet, sizeof(packet), &first) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        SameOperation(first, retry),
        "owner-merge retry did not reuse its operation UUID");

    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily =
        SecureLegacyCommandFamily::PetOwnerMergeToggle;
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
        next.hasOperation &&
        !SameOperation(first, next),
        "PetOwnerMergeToggle result did not settle owner-merge UUID");
}

} // namespace

int RunSecurePetOwnerMergeIdentityTests() {
    Checks checks{};
    CheckExactClassifier(&checks);
    CheckRegistryLifecycle(&checks);
    return checks.failures;
}
