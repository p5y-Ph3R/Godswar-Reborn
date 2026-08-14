#include "SecurePetRebirthIdentityTests.h"

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

void BuildRebirth(
    std::uint8_t* packet,
    std::uint32_t material = LegacyRebirthSpiritItemId,
    std::uint8_t quantity =
        LegacyMaximumPetAlterMaterialQuantity) {
    std::memset(packet, 0, LegacyPetRebirthPacketBytes);
    Header(
        packet,
        LegacyPetRebirthPacketBytes,
        LegacyPetRebirthOpcode);
    Write32(packet + 4, material);
    packet[8] = quantity;
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
    const std::uint8_t* packet,
    std::size_t bytes,
    LegacyPetCommandIntent* intent) {
    return ClassifyLegacyPetCommandPacket(packet, bytes, intent);
}

void CheckExactIntent(Checks* checks) {
    std::uint8_t standard[LegacyPetRebirthPacketBytes]{};
    BuildRebirth(standard);
    LegacyPetCommandIntent standardIntent{};
    checks->Require(
        Classify(standard, sizeof(standard), &standardIntent) ==
            LegacyPetCommandPacketKind::Command &&
        standardIntent.family ==
            SecureLegacyCommandFamily::PetRebirth &&
        standardIntent.bytes[0] == 1 &&
        standardIntent.bytes[1] == 1 &&
        Read32(standardIntent.bytes + 2) ==
            LegacyRebirthSpiritItemId &&
        standardIntent.bytes[6] ==
            LegacyMaximumPetAlterMaterialQuantity,
        "exact native rebirth intent was not classified");

    std::uint8_t restricted[LegacyPetRebirthPacketBytes]{};
    BuildRebirth(restricted, LegacyRebornHarpyiaItemId);
    LegacyPetCommandIntent restrictedIntent{};
    checks->Require(
        Classify(restricted, sizeof(restricted), &restrictedIntent) ==
            LegacyPetCommandPacketKind::Command &&
        !EqualPetCommandIntent(standardIntent, restrictedIntent),
        "restricted rebirth material aliased standard intent");

    for (std::uint8_t quantity = 1;
         quantity <= LegacyMaximumPetAlterMaterialQuantity;
         ++quantity) {
        BuildRebirth(standard, LegacyRebirthSpiritItemId, quantity);
        checks->Require(
            Classify(standard, sizeof(standard), &standardIntent) ==
                LegacyPetCommandPacketKind::Command &&
            standardIntent.bytes[6] == quantity,
            "rebirth rejected a stock one-through-five quantity");
    }
    const std::uint32_t zeroMaterials[] = {
        0,
        LegacyRebirthSpiritItemId,
        LegacyRebornHarpyiaItemId};
    for (const auto material : zeroMaterials) {
        BuildRebirth(standard, material, 0);
        checks->Require(
            Classify(standard, sizeof(standard), &standardIntent) ==
                LegacyPetCommandPacketKind::Command &&
            Read32(standardIntent.bytes + 2) == material &&
            standardIntent.bytes[6] == 0,
            "rebirth rejected a native zero-count modal state");
    }
}

void CheckMalformedInputs(Checks* checks) {
    std::uint8_t packet[LegacyPetRebirthPacketBytes]{};
    LegacyPetCommandIntent intent{};
    BuildRebirth(packet, 12345);
    checks->Require(
        Classify(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "rebirth accepted an unreviewed material template");
    BuildRebirth(packet, 0, 1);
    checks->Require(
        Classify(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "rebirth accepted a positive count without a material");
    BuildRebirth(packet, LegacyRebirthSpiritItemId, 6);
    checks->Require(
        Classify(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "rebirth accepted more than five materials");
    BuildRebirth(packet);
    packet[9] = 1;
    checks->Require(
        Classify(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "rebirth accepted nonzero reserved bytes");
    BuildRebirth(packet);
    checks->Require(
        Classify(packet, sizeof(packet) - 1, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "rebirth accepted a truncated frame");
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
        "rebirth registry setup failed");

    std::uint8_t packet[LegacyPetRebirthPacketBytes]{};
    BuildRebirth(packet);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.DescribePacket(packet, sizeof(packet), &first) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        first.operation.opcode == LegacyPetRebirthOpcode &&
        SameOperation(first, retry),
        "rebirth retry did not reuse its operation UUID");

    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily = SecureLegacyCommandFamily::PetRebirth;
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
        "rebirth result did not settle its operation UUID");
}

} // namespace

int RunSecurePetRebirthIdentityTests() {
    Checks checks{};
    CheckExactIntent(&checks);
    CheckMalformedInputs(&checks);
    CheckRegistryLifecycle(&checks);
    return checks.failures;
}
