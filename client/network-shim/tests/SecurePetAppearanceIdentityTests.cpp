#include "SecurePetAppearanceIdentityTests.h"

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

void BuildAppearance(
    std::uint8_t* packet,
    std::uint32_t npcId,
    std::int32_t coordinate) {
    std::memset(packet, 0xFF, LegacyPetManagerActionPacketBytes);
    Header(
        packet,
        LegacyPetManagerActionPacketBytes,
        LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyPetManagerDialog);
    Write32(packet + 12, LegacyPetManagerDialog);
    Write32(packet + 16, LegacyPetAppearanceChangeSubId);
    Write32(
        packet + 20,
        static_cast<std::uint32_t>(
            LegacyPetAppearanceConfirmationArgument));
    Write32(
        packet + 20 + LegacyPetAppearanceItemArgumentIndex * 4,
        static_cast<std::uint32_t>(coordinate));
    for (std::size_t index = 0;
         index < LegacyPetManagerScratchArgumentCount;
         ++index) {
        Write32(
            packet + 20 +
                (LegacyPetManagerScratchArgumentFirst + index) * 4,
            0);
    }
}

void BuildAppearanceNavigation(
    std::uint8_t* packet,
    std::uint32_t npcId) {
    std::memset(packet, 0xFF, LegacyPetManagerActionPacketBytes);
    Header(
        packet,
        LegacyPetManagerActionPacketBytes,
        LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyPetManagerDialog);
    Write32(packet + 12, LegacyPetManagerDialog);
    Write32(packet + 16, LegacyPetAppearanceChangeSubId);
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
    const std::int32_t coordinates[]{0, 23, 100, 123, 300, 323};
    const std::uint16_t slots[]{0, 23, 24, 47, 72, 95};
    for (const auto npcId : npcIds) {
        for (std::size_t index = 0;
             index < sizeof(coordinates) / sizeof(coordinates[0]);
             ++index) {
            std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
            BuildAppearance(packet, npcId, coordinates[index]);
            LegacyPetCommandIntent intent{};
            const auto classified = ClassifyLegacyPetCommandPacket(
                packet, sizeof(packet), &intent);
            const std::uint16_t actual = static_cast<std::uint16_t>(
                intent.bytes[2] |
                (static_cast<std::uint16_t>(intent.bytes[3]) << 8U));
            checks->Require(
                classified == LegacyPetCommandPacketKind::Command &&
                intent.family ==
                    SecureLegacyCommandFamily::PetAppearanceChange &&
                intent.bytes[0] == 1 && intent.bytes[1] == 1 &&
                actual == slots[index],
                "exact Magic Jade packet was not classified");
        }
    }

    std::uint8_t navigation[LegacyPetManagerActionPacketBytes]{};
    BuildAppearanceNavigation(navigation, LegacyAthensPetManagerNpc);
    LegacyPetCommandIntent ignored{};
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            navigation, sizeof(navigation), &ignored) ==
            LegacyPetCommandPacketKind::Unrelated,
        "all--1 appearance page navigation was treated as a mutation");

    for (std::size_t index = 0;
         index < LegacyPetManagerScratchArgumentCount;
         ++index) {
        Write32(
            navigation + 20 +
                (LegacyPetManagerScratchArgumentFirst + index) * 4,
            0);
    }
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            navigation, sizeof(navigation), &ignored) ==
            LegacyPetCommandPacketKind::Unrelated,
        "appearance navigation with native scratch was treated as a mutation");

    std::uint8_t staleScratch[LegacyPetManagerActionPacketBytes]{};
    BuildAppearance(staleScratch, LegacyAthensPetManagerNpc, 205);
    Write32(
        staleScratch + 20 +
            LegacyPetManagerScratchArgumentFirst * 4,
        0x7FFF'FFFFU);
    Write32(
        staleScratch + 20 +
            (LegacyPetManagerScratchArgumentFirst + 1) * 4,
        0x8000'0000U);
    Write32(
        staleScratch + 20 +
            (LegacyPetManagerScratchArgumentFirst + 2) * 4,
        12'345U);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            staleScratch, sizeof(staleScratch), &ignored) ==
            LegacyPetCommandPacketKind::Command,
        "stock numeric scratch fields blocked Magic Jade");
}

void CheckMalformedClassifier(Checks* checks) {
    const std::int32_t invalidCoordinates[]{24, 99, 124, 299, 324, 400};
    for (const auto coordinate : invalidCoordinates) {
        std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
        BuildAppearance(packet, LegacyAthensPetManagerNpc, coordinate);
        LegacyPetCommandIntent intent{};
        checks->Require(
            ClassifyLegacyPetCommandPacket(
                packet, sizeof(packet), &intent) ==
                LegacyPetCommandPacketKind::InvalidMutation,
            "out-of-range Magic Jade coordinate was accepted");
    }

    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    LegacyPetCommandIntent intent{};
    BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
    Write32(packet + 24, 0);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Magic Jade packet accepted non--1 padding");

    BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
    Write32(packet + 20, 0xFFFF'FFFFU);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Magic Jade packet accepted a missing confirmation sentinel");

    BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
    Write32(
        packet + 20,
        static_cast<std::uint32_t>(
            LegacyPetAppearanceDescriptionSubId));
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Magic Jade packet treated description row 113 as confirmation");

    BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(
            LegacyPetAppearanceDescriptionSubId));
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Magic Jade packet accepted flattened sub-ID 113");

    BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
    Write32(packet + 12, LegacyPetPointResetDialog);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Magic Jade packet accepted a mismatched dialog echo");

    BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet, sizeof(packet) - 1, &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "truncated Magic Jade mutation was not rejected");

    BuildAppearance(packet, LegacyAthensPetManagerNpc + 1, 205);
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::Unrelated,
        "unrelated NPC was treated as the Pet Manager");
}

void CheckRegistryAndResults(Checks* checks) {
    const std::uint32_t terminalResults[]{
        LegacyPetAppearanceSucceededResultSubId,
        LegacyPetAppearanceMissingJadeResultSubId,
        LegacyPetAppearanceIncompatibleJadeResultSubId,
        LegacyPetAppearanceNoPetResultSubId,
        LegacyPetAppearanceUnboundPetResultSubId,
    };
    for (const auto resultCode : terminalResults) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks, Random, &hooks, Clock);
        checks->Require(Establish(&registry),
            "Magic Jade registry setup failed");
        std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
        BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
        LegacyPacketDescriptor first{};
        LegacyPacketDescriptor retry{};
        registry.DescribePacket(packet, sizeof(packet), &first);
        for (std::size_t index = 0;
             index < LegacyPetManagerScratchArgumentCount;
             ++index) {
            Write32(
                packet + 20 +
                    (LegacyPetManagerScratchArgumentFirst + index) * 4,
                static_cast<std::uint32_t>(9'000 + index));
        }
        registry.DescribePacket(packet, sizeof(packet), &retry);

        SecureLegacyCommandResult result{};
        result.disposition = resultCode ==
                LegacyPetAppearanceSucceededResultSubId
            ? SecureLegacyCommandDisposition::Applied
            : SecureLegacyCommandDisposition::Rejected;
        result.commandFamily =
            SecureLegacyCommandFamily::PetAppearanceChange;
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
            "stock Magic Jade result did not settle its UUID");
    }

    Hooks hooks{};
    SecurePendingOperationRegistry registry(&hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "unknown Magic Jade result setup failed");
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    BuildAppearance(packet, LegacyAthensPetManagerNpc, 205);
    LegacyPacketDescriptor first{};
    registry.DescribePacket(packet, sizeof(packet), &first);
    SecureLegacyCommandResult unknown{};
    unknown.disposition = SecureLegacyCommandDisposition::Rejected;
    unknown.commandFamily =
        SecureLegacyCommandFamily::PetAppearanceChange;
    unknown.resultCode = 141;
    std::memcpy(unknown.operationId, first.operation.operationId, 16);
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.Resolve(unknown) ==
            SecureOperationRegistryResult::InvalidPacket &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        SameOperation(first, retry),
        "unknown Magic Jade result settled the pending UUID");
}

void CheckResultCodec(Checks* checks) {
    SecureLegacyCommandResult source{};
    source.disposition = SecureLegacyCommandDisposition::Applied;
    source.commandFamily =
        SecureLegacyCommandFamily::PetAppearanceChange;
    source.resultCode = LegacyPetAppearanceSucceededResultSubId;
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
            SecureLegacyCommandFamily::PetAppearanceChange &&
        decoded.resultCode == source.resultCode,
        "secure result codec rejected command family 52");
}

} // namespace

int RunSecurePetAppearanceIdentityTests() {
    Checks checks{};
    CheckExactClassifier(&checks);
    CheckMalformedClassifier(&checks);
    CheckRegistryAndResults(&checks);
    CheckResultCodec(&checks);
    return checks.failures;
}
