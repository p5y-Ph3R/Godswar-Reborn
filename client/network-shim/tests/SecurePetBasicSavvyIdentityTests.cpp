#include "SecurePetBasicSavvyIdentityTests.h"

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

void BuildBasicSavvyReset(
    std::uint8_t* packet,
    std::uint32_t npcId,
    bool nested,
    bool accept = false) {
    std::memset(packet, 0xFF, LegacyPetManagerActionPacketBytes);
    Header(packet, LegacyPetManagerActionPacketBytes,
        LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyPetPointResetDialog);
    Write32(packet + 12, LegacyPetPointResetDialog);
    Write32(packet + 16, nested
        ? LegacyPetBasicSavvyResetMenuSubId
        : LegacyPetBasicSavvyResetActionSubId);
    if (nested) {
        Write32(packet + 20, LegacyPetBasicSavvyResetActionSubId);
    }
    if (accept) {
        Write32(packet + (nested ? 24 : 20), 0);
    }
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

bool Establish(
    SecurePendingOperationRegistry* registry) {
    std::uint8_t login[36]{};
    BuildLogin(login);
    LegacyPacketDescriptor ignored{};
    return registry != nullptr &&
        registry->DescribePacket(login, sizeof(login), &ignored) ==
            SecureOperationRegistryResult::Success &&
        registry->SetCharacter(2) ==
            SecureOperationRegistryResult::Success;
}

void CheckExactShapes(Checks* checks) {
    const std::uint32_t npcIds[]{
        LegacySpartaPetManagerNpc,
        LegacySpartaSourcePetManagerNpc,
        LegacyAthensPetManagerNpc,
    };
    const bool shapes[]{true, false};
    for (const auto npcId : npcIds) {
        for (const bool nested : shapes) {
            std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
            LegacyPetCommandIntent intent{};
            BuildBasicSavvyReset(packet, npcId, nested);
            checks->Require(
                ClassifyLegacyPetCommandPacket(
                    packet, sizeof(packet), &intent) ==
                        LegacyPetCommandPacketKind::Command &&
                intent.family ==
                    SecureLegacyCommandFamily::PetBasicSavvyReset &&
                intent.bytes[0] == 1 && intent.bytes[1] == 1,
                "exact Fairy preview shape was not classified");
            BuildBasicSavvyReset(packet, npcId, nested, true);
            checks->Require(
                ClassifyLegacyPetCommandPacket(
                    packet, sizeof(packet), &intent) ==
                        LegacyPetCommandPacketKind::Command &&
                intent.family ==
                    SecureLegacyCommandFamily::PetBasicSavvyReset &&
                intent.bytes[0] == 1 && intent.bytes[1] == 2,
                "exact Fairy accept shape was not classified");
        }
    }
}

void CheckMalformedShapes(Checks* checks) {
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    LegacyPetCommandIntent intent{};
    BuildBasicSavvyReset(packet, LegacyAthensPetManagerNpc, true);
    Write32(packet + 28, 0);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Fairy preview accepted non--1 padding");

    BuildBasicSavvyReset(packet, LegacyAthensPetManagerNpc, true);
    Write32(packet + 20, LegacyPetGrowthResetActionSubId);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Fairy parent accepted the Phoenix child action");

    BuildBasicSavvyReset(packet, LegacyAthensPetManagerNpc, false);
    Write32(packet + 12, LegacyPetManagerDialog);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Fairy reset accepted a mismatched dialog echo");

    BuildBasicSavvyReset(packet, LegacyAthensPetManagerNpc, false);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet) - 1, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Fairy reset accepted a truncated frame");

    BuildBasicSavvyReset(packet, LegacyAthensPetManagerNpc + 1, false);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::Unrelated,
        "unrelated NPC was treated as the Pet Manager");
}

void CheckRegistryIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(&hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "Fairy reset registry setup failed");
    std::uint8_t preview[LegacyPetManagerActionPacketBytes]{};
    std::uint8_t accept[LegacyPetManagerActionPacketBytes]{};
    BuildBasicSavvyReset(preview, LegacyAthensPetManagerNpc, true);
    BuildBasicSavvyReset(accept, LegacyAthensPetManagerNpc, true, true);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    LegacyPacketDescriptor accepted{};
    checks->Require(
        registry.DescribePacket(preview, sizeof(preview), &first) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(preview, sizeof(preview), &retry) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(accept, sizeof(accept), &accepted) ==
            SecureOperationRegistryResult::Success &&
        SameOperation(first, retry) &&
        !SameOperation(first, accepted) &&
        first.operation.opcode == LegacyNpcFunctionActionOpcode,
        "Fairy preview retry or accept identity was not isolated");
}

void CheckTerminalResults(Checks* checks) {
    const std::uint32_t resultCodes[]{
        LegacyPetBasicSavvyResetLegacyNoPetResultSubId,
        LegacyPetBasicSavvyResetLegacyNoFeatherResultSubId,
        LegacyPetBasicSavvyResetNoFeatherResultSubId,
        LegacyPetBasicSavvyResetNoPetResultSubId,
        LegacyPetBasicSavvyResetNoPreviewResultSubId,
        LegacyPetBasicSavvyResetSucceededResultSubId,
    };
    for (const auto resultCode : resultCodes) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks, Random, &hooks, Clock);
        checks->Require(Establish(&registry),
            "Fairy terminal-result registry setup failed");
        std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
        BuildBasicSavvyReset(packet, LegacyAthensPetManagerNpc, true);
        LegacyPacketDescriptor first{};
        registry.DescribePacket(packet, sizeof(packet), &first);
        SecureLegacyCommandResult result{};
        result.disposition = resultCode ==
                LegacyPetBasicSavvyResetSucceededResultSubId
            ? SecureLegacyCommandDisposition::Applied
            : SecureLegacyCommandDisposition::Rejected;
        result.commandFamily =
            SecureLegacyCommandFamily::PetBasicSavvyReset;
        result.resultCode = resultCode;
        result.inventoryRevision =
            result.disposition == SecureLegacyCommandDisposition::Applied
            ? 1 : 0;
        std::memcpy(result.operationId, first.operation.operationId, 16);
        LegacyPacketDescriptor next{};
        checks->Require(
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
            registry.DescribePacket(packet, sizeof(packet), &next) ==
                SecureOperationRegistryResult::Success &&
            !SameOperation(first, next),
            "Fairy terminal result did not settle its UUID");
    }

    Hooks hooks{};
    SecurePendingOperationRegistry registry(&hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "Fairy unknown-result registry setup failed");
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    BuildBasicSavvyReset(packet, LegacyAthensPetManagerNpc, true);
    LegacyPacketDescriptor first{};
    registry.DescribePacket(packet, sizeof(packet), &first);
    SecureLegacyCommandResult unknown{};
    unknown.disposition = SecureLegacyCommandDisposition::Rejected;
    unknown.commandFamily =
        SecureLegacyCommandFamily::PetBasicSavvyReset;
    unknown.resultCode = 122;
    std::memcpy(unknown.operationId, first.operation.operationId, 16);
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.Resolve(unknown) ==
            SecureOperationRegistryResult::InvalidPacket &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        SameOperation(first, retry),
        "unknown Fairy result settled the pending UUID");
}

void CheckResultCodec(Checks* checks) {
    SecureLegacyCommandResult source{};
    source.disposition = SecureLegacyCommandDisposition::Applied;
    source.commandFamily =
        SecureLegacyCommandFamily::PetBasicSavvyReset;
    source.resultCode = LegacyPetBasicSavvyResetSucceededResultSubId;
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
        decoded.commandFamily ==
            SecureLegacyCommandFamily::PetBasicSavvyReset &&
        decoded.resultCode == source.resultCode,
        "secure result codec rejected command family 51");
}

} // namespace

int RunSecurePetBasicSavvyIdentityTests() {
    Checks checks{};
    CheckExactShapes(&checks);
    CheckMalformedShapes(&checks);
    CheckRegistryIdentity(&checks);
    CheckTerminalResults(&checks);
    CheckResultCodec(&checks);
    return checks.failures;
}
