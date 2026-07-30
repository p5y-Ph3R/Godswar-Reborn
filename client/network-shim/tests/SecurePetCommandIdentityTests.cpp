#include "SecurePetCommandIdentityTests.h"

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

void Write32(std::uint8_t* target, std::uint32_t value) {
    target[0] = static_cast<std::uint8_t>(value);
    target[1] = static_cast<std::uint8_t>(value >> 8U);
    target[2] = static_cast<std::uint8_t>(value >> 16U);
    target[3] = static_cast<std::uint8_t>(value >> 24U);
}

void Header(
    std::uint8_t* packet,
    std::uint16_t bytes,
    std::uint16_t opcode) {
    Write16(packet, bytes);
    Write16(packet + 2, opcode);
}

void BuildLogin(std::uint8_t* packet) {
    std::memset(packet, 0, 36);
    Header(packet, 36, LegacyLoginGameServerOpcode);
    std::memcpy(packet + 4, "test2", 5);
}

void BuildActivation(
    std::uint8_t* packet,
    std::uint16_t slot,
    std::uint32_t untrustedHint) {
    std::memset(
        packet,
        0,
        LegacyBagItemActivationPacketBytes);
    Header(
        packet,
        LegacyBagItemActivationPacketBytes,
        LegacyBagItemActivationOpcode);
    Write16(packet + 12, static_cast<std::uint16_t>(slot / 24));
    Write16(packet + 14, static_cast<std::uint16_t>(slot % 24));
    Write32(packet + 72, untrustedHint);
}

void BuildPet(
    std::uint8_t* packet,
    std::uint16_t opcode,
    std::uint32_t petId) {
    std::memset(packet, 0, LegacyPetCommandPacketBytes);
    Header(packet, LegacyPetCommandPacketBytes, opcode);
    Write32(packet + 4, petId);
}

bool Same(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return first.hasOperation && second.hasOperation &&
        std::memcmp(
            first.operation.operationId,
            second.operation.operationId,
            16) == 0;
}

void CheckClassifier(Checks* checks) {
    std::uint8_t first[LegacyBagItemActivationPacketBytes]{};
    std::uint8_t second[LegacyBagItemActivationPacketBytes]{};
    BuildActivation(first, 31, 10001);
    BuildActivation(second, 31, 0xFFFF'FFFFU);
    LegacyPetCommandIntent firstIntent{};
    LegacyPetCommandIntent secondIntent{};
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            first,
            sizeof(first),
            &firstIntent) ==
                LegacyPetCommandPacketKind::Command &&
        ClassifyLegacyPetCommandPacket(
            second,
            sizeof(second),
            &secondIntent) ==
                LegacyPetCommandPacketKind::Command &&
        EqualPetCommandIntent(firstIntent, secondIntent),
        "bag activation trusted its client item hint");

    BuildPet(first, LegacyPetLevelUpgradeOpcode, 0);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            first,
            LegacyPetCommandPacketBytes,
            &firstIntent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "zero pet identity was accepted");
}

void CheckRegistry(Checks* checks) {
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
        "pet registry principal setup failed");

    std::uint8_t packet[LegacyBagItemActivationPacketBytes]{};
    BuildActivation(packet, 31, 10001);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.DescribePacket(packet, sizeof(packet), &first) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        Same(first, retry),
        "pending pet retry did not reuse its UUID");

    SecureLegacyCommandResult result{};
    result.disposition =
        SecureLegacyCommandDisposition::Applied;
    result.commandFamily =
        SecureLegacyCommandFamily::BagItemActivation;
    result.inventoryRevision = 1;
    std::memcpy(
        result.operationId,
        first.operation.operationId,
        16);
    LegacyPacketDescriptor next{};
    checks->Require(
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &next) ==
            SecureOperationRegistryResult::Success &&
        !Same(first, next),
        "resolved pet action suppressed a later deliberate action");
}

} // namespace

int RunSecurePetCommandIdentityTests() {
    Checks checks{};
    CheckClassifier(&checks);
    CheckRegistry(&checks);
    return checks.failures;
}
