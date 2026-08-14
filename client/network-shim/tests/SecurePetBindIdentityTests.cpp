#include "SecurePetBindIdentityTests.h"

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
    std::uint64_t now = 95'000;
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
    std::memcpy(packet + 4, "test2", 5);
}

void BuildPetManagerAction(
    std::uint8_t* packet,
    std::uint32_t npcId,
    std::int32_t subId) {
    std::memset(packet, 0xFF, LegacyPetManagerActionPacketBytes);
    Header(
        packet,
        LegacyPetManagerActionPacketBytes,
        LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyPetManagerDialog);
    Write32(packet + 12, LegacyPetManagerDialog);
    Write32(packet + 16, static_cast<std::uint32_t>(subId));
}

void BuildBind(std::uint8_t* packet, std::uint32_t npcId) {
    BuildPetManagerAction(packet, npcId, LegacyPetBindMenuSubId);
    Write32(
        packet + 20,
        static_cast<std::uint32_t>(LegacyPetBindActionSubId));
}

bool Establish(SecurePendingOperationRegistry* registry) {
    std::uint8_t login[36]{};
    BuildLogin(login);
    LegacyPacketDescriptor ignored{};
    return registry != nullptr &&
        registry->DescribePacket(login, sizeof(login), &ignored) ==
            SecureOperationRegistryResult::Success &&
        registry->SetCharacter(2) ==
            SecureOperationRegistryResult::Success;
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
    const std::uint32_t npcIds[]{
        LegacySpartaPetManagerNpc,
        LegacySpartaSourcePetManagerNpc,
        LegacyAthensPetManagerNpc,
    };
    for (const auto npcId : npcIds) {
        std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
        BuildBind(packet, npcId);
        LegacyPetCommandIntent intent{};
        checks->Require(
            ClassifyLegacyPetCommandPacket(
                packet, sizeof(packet), &intent) ==
                LegacyPetCommandPacketKind::Command &&
            intent.family == SecureLegacyCommandFamily::PetBind &&
            intent.bytes[0] == 1 && intent.bytes[1] == 1,
            "exact nested Pet Bind packet was not classified");
    }

    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    LegacyPetCommandIntent intent{};
    BuildPetManagerAction(
        packet, LegacyAthensPetManagerNpc, LegacyPetBindMenuSubId);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::Unrelated,
        "Pet Bind page navigation was treated as a mutation");

    BuildPetManagerAction(
        packet, LegacyAthensPetManagerNpc, LegacyPetBindActionSubId);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "unsupported flattened Pet Bind action was admitted");
}

void CheckMalformedClassifier(Checks* checks) {
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    LegacyPetCommandIntent intent{};

    BuildBind(packet, LegacyAthensPetManagerNpc);
    Write32(packet + 24, 0);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Pet Bind accepted non--1 padding");

    BuildBind(packet, LegacyAthensPetManagerNpc);
    Write32(packet + 20, LegacyPetBindActionSubId + 1);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Pet Bind accepted an unknown nested child");

    BuildBind(packet, LegacyAthensPetManagerNpc);
    Write32(packet + 12, LegacyPetPointResetDialog);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Pet Bind accepted a mismatched dialog echo");

    BuildBind(packet, LegacyAthensPetManagerNpc);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet, sizeof(packet) - 1, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "truncated Pet Bind mutation was not rejected");

    BuildBind(packet, LegacyAthensPetManagerNpc + 1);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::Unrelated,
        "unrelated NPC was treated as the Pet Manager");
}

void CheckRegistryAndResults(Checks* checks) {
    const std::uint32_t terminalResults[]{
        LegacyPetBindAlreadyBoundResultSubId,
        LegacyPetBindSucceededResultSubId,
        LegacyPetBindNoPetResultSubId,
    };
    for (const auto resultCode : terminalResults) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks, Random, &hooks, Clock);
        checks->Require(Establish(&registry),
            "Pet Bind registry setup failed");
        std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
        BuildBind(packet, LegacyAthensPetManagerNpc);
        LegacyPacketDescriptor first{};
        LegacyPacketDescriptor retry{};
        registry.DescribePacket(packet, sizeof(packet), &first);
        registry.DescribePacket(packet, sizeof(packet), &retry);

        SecureLegacyCommandResult result{};
        result.disposition = resultCode ==
                LegacyPetBindSucceededResultSubId
            ? SecureLegacyCommandDisposition::Applied
            : SecureLegacyCommandDisposition::Rejected;
        result.commandFamily = SecureLegacyCommandFamily::PetBind;
        result.resultCode = resultCode;
        result.inventoryRevision =
            result.disposition == SecureLegacyCommandDisposition::Applied
            ? 1 : 0;
        std::memcpy(result.operationId, first.operation.operationId, 16);
        LegacyPacketDescriptor next{};
        checks->Require(
            SameOperation(first, retry) &&
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
            registry.DescribePacket(packet, sizeof(packet), &next) ==
                SecureOperationRegistryResult::Success &&
            !SameOperation(first, next),
            "stock Pet Bind result did not settle its UUID");
    }

    Hooks hooks{};
    SecurePendingOperationRegistry registry(&hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "unknown Pet Bind result setup failed");
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    BuildBind(packet, LegacyAthensPetManagerNpc);
    LegacyPacketDescriptor first{};
    registry.DescribePacket(packet, sizeof(packet), &first);
    SecureLegacyCommandResult unknown{};
    unknown.disposition = SecureLegacyCommandDisposition::Rejected;
    unknown.commandFamily = SecureLegacyCommandFamily::PetBind;
    unknown.resultCode = 1074;
    std::memcpy(unknown.operationId, first.operation.operationId, 16);
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.Resolve(unknown) ==
            SecureOperationRegistryResult::InvalidPacket &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        SameOperation(first, retry),
        "unknown Pet Bind result settled the pending UUID");
}

void CheckResultCodec(Checks* checks) {
    SecureLegacyCommandResult source{};
    source.disposition = SecureLegacyCommandDisposition::Applied;
    source.commandFamily = SecureLegacyCommandFamily::PetBind;
    source.resultCode = LegacyPetBindSucceededResultSubId;
    source.inventoryRevision = 1;
    for (std::size_t index = 0; index < sizeof(source.operationId); ++index) {
        source.operationId[index] = static_cast<std::uint8_t>(index + 1);
    }
    std::uint8_t encoded[SecureLegacyCommandResultPayloadBytes]{};
    SecureLegacyCommandResult decoded{};
    checks->Require(
        TryEncodeSecureLegacyCommandResult(
            source, encoded, sizeof(encoded)) &&
        TryDecodeSecureLegacyCommandResult(
            encoded, sizeof(encoded), &decoded) &&
        decoded.commandFamily == SecureLegacyCommandFamily::PetBind &&
        decoded.resultCode == source.resultCode,
        "secure result codec rejected command family 53");
}

} // namespace

int RunSecurePetBindIdentityTests() {
    Checks checks{};
    CheckExactClassifier(&checks);
    CheckMalformedClassifier(&checks);
    CheckRegistryAndResults(&checks);
    CheckResultCodec(&checks);
    return checks.failures;
}
