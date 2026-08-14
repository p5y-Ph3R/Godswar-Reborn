#include "SecurePetManagerUtilityIdentityTests.h"

#include "../src/SecureLegacyCommandIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"
#include "../src/SecurePetManagerUtilityIdentity.h"

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
    std::uint8_t seed = 81;
    std::uint64_t now = 800'000;
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
    std::memcpy(packet + 4, "utility", 7);
}

void BuildUtility(
    std::uint8_t* packet,
    std::int32_t subId,
    std::int32_t argumentZero,
    bool scratch = true) {
    std::memset(packet, 0xFF, LegacyPetManagerActionPacketBytes);
    Header(
        packet,
        LegacyPetManagerActionPacketBytes,
        LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, LegacyAthensPetManagerNpc);
    Write32(packet + 8, LegacyPetManagerDialog);
    Write32(packet + 12, LegacyPetManagerDialog);
    Write32(packet + 16, static_cast<std::uint32_t>(subId));
    Write32(packet + 20, static_cast<std::uint32_t>(argumentZero));
    if (scratch) {
        Write32(packet + 20 + 10 * 4, 0);
        Write32(packet + 20 + 11 * 4, 0x7FFF'FFFFU);
        Write32(packet + 20 + 12 * 4, 205U);
    }
}

void BuildBagActivation(
    std::uint8_t* packet,
    std::uint16_t page,
    std::uint16_t slot) {
    std::memset(packet, 0, LegacyBagItemActivationPacketBytes);
    Header(
        packet,
        LegacyBagItemActivationPacketBytes,
        LegacyBagItemActivationOpcode);
    Write16(packet + 12, page);
    Write16(packet + 14, slot);
}

bool Establish(SecurePendingOperationRegistry* registry) {
    std::uint8_t login[36]{};
    BuildLogin(login);
    LegacyPacketDescriptor ignored{};
    return registry != nullptr &&
        registry->DescribePacket(login, sizeof(login), &ignored) ==
            SecureOperationRegistryResult::Success &&
        registry->SetCharacter(17) ==
            SecureOperationRegistryResult::Success;
}

SecureLegacyCommandResult Result(
    const LegacyPacketDescriptor& descriptor,
    std::uint32_t code,
    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::PetManagerUtility) {
    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily = family;
    result.resultCode = code;
    result.inventoryRevision = 2;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

void CheckClassifier(Checks* checks) {
    struct Shape final {
        std::int32_t subId;
        std::int32_t argumentZero;
        LegacyPetManagerUtilityOperation operation;
    };
    const Shape shapes[]{
        {4, 104, LegacyPetManagerUtilityOperation::CheckGrowth},
        {5, 105, LegacyPetManagerUtilityOperation::Seal},
        {9, -1, LegacyPetManagerUtilityOperation::ClaimPetCall},
        {10, -1, LegacyPetManagerUtilityOperation::ClaimMerge},
        {11, 0, LegacyPetManagerUtilityOperation::ChangeGender},
    };
    for (const auto& shape : shapes) {
        std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
        BuildUtility(packet, shape.subId, shape.argumentZero);
        LegacyPetCommandIntent intent{};
        checks->Require(
            ClassifyLegacyPetCommandPacket(
                packet, sizeof(packet), &intent) ==
                    LegacyPetCommandPacketKind::Command &&
            intent.family ==
                SecureLegacyCommandFamily::PetManagerUtility &&
            intent.bytes[0] == 1 &&
            intent.bytes[1] ==
                static_cast<std::uint8_t>(shape.operation),
            "exact utility action with native scratch was not classified");
    }

    const std::int32_t navigationSubIds[]{4, 5, 11};
    for (const auto subId : navigationSubIds) {
        std::uint8_t navigation[LegacyPetManagerActionPacketBytes]{};
        BuildUtility(navigation, subId, -1);
        LegacyPetCommandIntent ignored{};
        checks->Require(
            ClassifyLegacyPetCommandPacket(
                navigation, sizeof(navigation), &ignored) ==
                LegacyPetCommandPacketKind::Unrelated,
            "utility navigation was classified as a mutation");
    }

    std::uint8_t malformed[LegacyPetManagerActionPacketBytes]{};
    BuildUtility(malformed, 5, 105);
    Write32(malformed + 20 + 6 * 4, 0);
    LegacyPetCommandIntent ignored{};
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            malformed, sizeof(malformed), &ignored) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "utility action trusted a non-scratch padding argument");
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            malformed, sizeof(malformed) - 4, &ignored) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "truncated utility mutation did not fail closed");
}

void CheckRegistryAndUnsealAlias(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry), "utility registry setup failed");

    std::uint8_t utility[LegacyPetManagerActionPacketBytes]{};
    BuildUtility(utility, 10, -1);
    LegacyPacketDescriptor utilityDescriptor{};
    checks->Require(
        registry.DescribePacket(
            utility, sizeof(utility), &utilityDescriptor) ==
                SecureOperationRegistryResult::Success &&
        utilityDescriptor.hasOperation,
        "utility command did not receive an operation UUID");
    const auto utilityResult = Result(
        utilityDescriptor,
        LegacyPetMergeClaimedResult);
    checks->Require(
        registry.Resolve(utilityResult) ==
            SecureOperationRegistryResult::Success &&
        registry.Resolve(utilityResult) ==
            SecureOperationRegistryResult::Success,
        "family55 result did not settle and tombstone idempotently");

    std::uint8_t activation[LegacyBagItemActivationPacketBytes]{};
    BuildBagActivation(activation, 2, 3);
    LegacyPacketDescriptor activationDescriptor{};
    checks->Require(
        registry.DescribePacket(
            activation, sizeof(activation), &activationDescriptor) ==
                SecureOperationRegistryResult::Success &&
        activationDescriptor.hasOperation,
        "10051 activation did not receive an operation UUID");
    const auto wrongFamily55 = Result(
        activationDescriptor,
        LegacyPetCallClaimedResult);
    checks->Require(
        registry.Resolve(wrongFamily55) ==
            SecureOperationRegistryResult::FamilyConflict,
        "family26 intent accepted a non-Unseal family55 result");
    const auto unsealed = Result(
        activationDescriptor,
        LegacyPetUnsealedResult);
    checks->Require(
        registry.Resolve(unsealed) ==
            SecureOperationRegistryResult::Success &&
        registry.Resolve(unsealed) ==
            SecureOperationRegistryResult::Success,
        "10051 UUID did not settle exact family55 Unseal result");
}

} // namespace

int RunSecurePetManagerUtilityIdentityTests() {
    Checks checks{};
    CheckClassifier(&checks);
    CheckRegistryAndUnsealAlias(&checks);
    return checks.failures;
}
