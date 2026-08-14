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

void BuildPetSkillUnlearn(
    std::uint8_t* packet,
    std::uint32_t npcId,
    std::int32_t subId,
    bool nested = true) {
    std::memset(packet, 0xFF, LegacyPetManagerActionPacketBytes);
    Header(
        packet,
        static_cast<std::uint16_t>(
            LegacyPetManagerActionPacketBytes),
        LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyPetManagerDialog);
    Write32(packet + 12, LegacyPetManagerDialog);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(
            nested ? LegacyPetSkillUnlearnMenuSubId : subId));
    if (nested) {
        Write32(packet + 20, static_cast<std::uint32_t>(subId));
    }
}

void BuildPetGrowthReset(
    std::uint8_t* packet,
    std::uint32_t npcId,
    bool nested = true,
    bool accept = false) {
    std::memset(packet, 0xFF, LegacyPetManagerActionPacketBytes);
    Header(packet, LegacyPetManagerActionPacketBytes,
        LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyPetPointResetDialog);
    Write32(packet + 12, LegacyPetPointResetDialog);
    Write32(packet + 16, nested
        ? LegacyPetGrowthResetMenuSubId
        : LegacyPetGrowthResetActionSubId);
    if (nested) {
        Write32(packet + 20, LegacyPetGrowthResetActionSubId);
    }
    if (accept) {
        Write32(packet + (nested ? 24 : 20), 0);
    }
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

bool EstablishPetRegistry(
    SecurePendingOperationRegistry* registry);

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

void CheckSkillUnlearnClassifier(Checks* checks) {
    const std::uint32_t npcIds[]{
        LegacySpartaPetManagerNpc,
        LegacySpartaSourcePetManagerNpc,
        LegacyAthensPetManagerNpc,
    };
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    for (const std::uint32_t npcId : npcIds) {
        const std::int32_t subIds[]{
            106, 107, 108, 109, 110, 111,
            114, 115, 116, 117, 118, 119,
        };
        for (std::size_t slot = 0;
             slot < sizeof(subIds) / sizeof(subIds[0]);
             ++slot) {
            const std::int32_t subId = subIds[slot];
            BuildPetSkillUnlearn(packet, npcId, subId);
            LegacyPetCommandIntent intent{};
            checks->Require(
                ClassifyLegacyPetCommandPacket(
                    packet,
                    sizeof(packet),
                    &intent) == LegacyPetCommandPacketKind::Command &&
                intent.family ==
                    SecureLegacyCommandFamily::PetSkillUnlearn &&
                intent.bytes[0] == 1 &&
                intent.bytes[1] == 1 &&
                intent.bytes[2] == static_cast<std::uint8_t>(slot),
                "canonical pet skill-unlearn packet was not classified");
        }
    }

    BuildPetSkillUnlearn(
        packet,
        LegacySpartaPetManagerNpc,
        LegacyPetSkillUnlearnFirstSubId);
    Write32(packet + 20 + 7 * 4, 0);
    LegacyPetCommandIntent intent{};
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet,
            sizeof(packet),
            &intent) == LegacyPetCommandPacketKind::InvalidMutation,
        "pet skill-unlearn accepted a non--1 argument");

    BuildPetSkillUnlearn(
        packet,
        LegacyAthensPetManagerNpc,
        LegacyPetSkillUnlearnLastSubId);
    Write32(packet + 12, LegacyPetManagerDialog + 1);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet,
            sizeof(packet),
            &intent) == LegacyPetCommandPacketKind::InvalidMutation,
        "pet skill-unlearn accepted a mismatched dialog echo");

    BuildPetSkillUnlearn(
        packet,
        LegacyAthensPetManagerNpc + 1,
        LegacyPetSkillUnlearnFirstSubId);
    checks->Require(
        ClassifyLegacyPetCommandPacket(
            packet,
            sizeof(packet),
            &intent) == LegacyPetCommandPacketKind::Unrelated,
        "unrelated NPC was treated as Pet Manager skill removal");

    const std::int32_t unrelatedSubIds[]{105, 124};
    for (const std::int32_t subId : unrelatedSubIds) {
        BuildPetSkillUnlearn(
            packet,
            LegacyAthensPetManagerNpc,
            subId,
            false);
        checks->Require(
            ClassifyLegacyPetCommandPacket(
                packet,
                sizeof(packet),
                &intent) == LegacyPetCommandPacketKind::Unrelated,
            "Pet Manager non-skill action was treated as skill removal");
    }
}

void CheckGrowthReset(Checks* checks) {
    const std::uint32_t npcIds[]{
        LegacySpartaPetManagerNpc,
        LegacySpartaSourcePetManagerNpc,
        LegacyAthensPetManagerNpc,
    };
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    const bool shapes[]{true, false};
    for (const auto npcId : npcIds) {
        for (const bool nested : shapes) {
            BuildPetGrowthReset(packet, npcId, nested);
            LegacyPetCommandIntent intent{};
            checks->Require(
                ClassifyLegacyPetCommandPacket(packet, sizeof(packet),
                    &intent) == LegacyPetCommandPacketKind::Command &&
                intent.family ==
                    SecureLegacyCommandFamily::PetGrowthReset &&
                intent.bytes[0] == 1 && intent.bytes[1] == 1,
                "canonical Phoenix Growth reset was not classified");
            BuildPetGrowthReset(packet, npcId, nested, true);
            checks->Require(
                ClassifyLegacyPetCommandPacket(packet, sizeof(packet),
                    &intent) == LegacyPetCommandPacketKind::Command &&
                intent.family ==
                    SecureLegacyCommandFamily::PetGrowthReset &&
                intent.bytes[0] == 1 && intent.bytes[1] == 2,
                "canonical Phoenix Growth OK was not classified");
        }
    }

    BuildPetGrowthReset(packet, LegacyAthensPetManagerNpc);
    Write32(packet + 28, 0);
    LegacyPetCommandIntent intent{};
    checks->Require(
        ClassifyLegacyPetCommandPacket(packet, sizeof(packet), &intent) ==
            LegacyPetCommandPacketKind::InvalidMutation,
        "Phoenix Growth reset accepted non--1 padding");

    Hooks hooks{};
    SecurePendingOperationRegistry registry(&hooks, Random, &hooks, Clock);
    checks->Require(EstablishPetRegistry(&registry),
        "Phoenix Growth registry setup failed");
    BuildPetGrowthReset(packet, LegacyAthensPetManagerNpc);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.DescribePacket(packet, sizeof(packet), &first) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &retry) ==
            SecureOperationRegistryResult::Success &&
        Same(first, retry),
        "Phoenix Growth retry did not reuse its UUID");

    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily = SecureLegacyCommandFamily::PetGrowthReset;
    result.resultCode = LegacyPetGrowthResetSucceededResultSubId;
    result.inventoryRevision = 1;
    std::memcpy(result.operationId, first.operation.operationId, 16);
    LegacyPacketDescriptor next{};
    checks->Require(
        registry.Resolve(result) == SecureOperationRegistryResult::Success &&
        registry.DescribePacket(packet, sizeof(packet), &next) ==
            SecureOperationRegistryResult::Success &&
        !Same(first, next),
        "Phoenix Growth result did not settle its UUID");

    std::uint8_t encoded[SecureLegacyCommandResultPayloadBytes]{};
    SecureLegacyCommandResult decoded{};
    checks->Require(
        TryEncodeSecureLegacyCommandResult(result, encoded, sizeof(encoded)) &&
        TryDecodeSecureLegacyCommandResult(encoded, sizeof(encoded), &decoded) &&
        decoded.commandFamily == SecureLegacyCommandFamily::PetGrowthReset,
        "secure result codec rejected command family 47");
}

bool EstablishPetRegistry(
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

void CheckSkillUnlearnResultSettlement(Checks* checks) {
    const std::uint32_t terminalResults[]{
        LegacyPetSkillUnlearnNoPetResultSubId,
        LegacyPetSkillUnlearnNoPotionResultSubId,
        LegacyPetSkillUnlearnEmptySlotResultSubId,
        LegacyPetSkillUnlearnSucceededResultSubId,
    };
    for (const std::uint32_t resultCode : terminalResults) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        checks->Require(
            EstablishPetRegistry(&registry),
            "pet skill-unlearn registry setup failed");
        std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
        BuildPetSkillUnlearn(
            packet,
            LegacySpartaPetManagerNpc,
            LegacyPetSkillUnlearnFirstSubId);
        LegacyPacketDescriptor first{};
        checks->Require(
            registry.DescribePacket(
                packet,
                sizeof(packet),
                &first) == SecureOperationRegistryResult::Success &&
            first.hasOperation,
            "pet skill-unlearn did not receive an operation UUID");

        SecureLegacyCommandResult result{};
        result.disposition =
            resultCode == LegacyPetSkillUnlearnSucceededResultSubId
            ? SecureLegacyCommandDisposition::Applied
            : SecureLegacyCommandDisposition::Rejected;
        result.commandFamily =
            SecureLegacyCommandFamily::PetSkillUnlearn;
        result.resultCode = resultCode;
        result.inventoryRevision =
            result.disposition == SecureLegacyCommandDisposition::Applied
            ? 1
            : 0;
        std::memcpy(
            result.operationId,
            first.operation.operationId,
            sizeof(result.operationId));
        LegacyPacketDescriptor next{};
        checks->Require(
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
            registry.DescribePacket(
                packet,
                sizeof(packet),
                &next) == SecureOperationRegistryResult::Success &&
            next.hasOperation &&
            !Same(first, next),
            "stock pet skill-unlearn result did not settle its UUID");
    }

    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        EstablishPetRegistry(&registry),
        "unknown-result registry setup failed");
    std::uint8_t packet[LegacyPetManagerActionPacketBytes]{};
    BuildPetSkillUnlearn(
        packet,
        LegacyAthensPetManagerNpc,
        LegacyPetSkillUnlearnLastSubId);
    LegacyPacketDescriptor first{};
    registry.DescribePacket(packet, sizeof(packet), &first);
    SecureLegacyCommandResult unknown{};
    unknown.disposition = SecureLegacyCommandDisposition::Rejected;
    unknown.commandFamily = SecureLegacyCommandFamily::PetSkillUnlearn;
    unknown.resultCode = 1064;
    std::memcpy(
        unknown.operationId,
        first.operation.operationId,
        sizeof(unknown.operationId));
    LegacyPacketDescriptor retry{};
    checks->Require(
        registry.Resolve(unknown) ==
            SecureOperationRegistryResult::InvalidPacket &&
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &retry) == SecureOperationRegistryResult::Success &&
        Same(first, retry),
        "unknown pet skill-unlearn result settled the pending UUID");
}

void CheckSkillUnlearnResultCodec(Checks* checks) {
    SecureLegacyCommandResult source{};
    source.disposition = SecureLegacyCommandDisposition::Rejected;
    source.commandFamily = SecureLegacyCommandFamily::PetSkillUnlearn;
    source.resultCode = LegacyPetSkillUnlearnNoPotionResultSubId;
    for (std::size_t index = 0;
         index < sizeof(source.operationId);
         ++index) {
        source.operationId[index] =
            static_cast<std::uint8_t>(index + 1);
    }
    std::uint8_t encoded[SecureLegacyCommandResultPayloadBytes]{};
    SecureLegacyCommandResult decoded{};
    checks->Require(
        TryEncodeSecureLegacyCommandResult(
            source,
            encoded,
            sizeof(encoded)) &&
        TryDecodeSecureLegacyCommandResult(
            encoded,
            sizeof(encoded),
            &decoded) &&
        decoded.commandFamily ==
            SecureLegacyCommandFamily::PetSkillUnlearn &&
        decoded.resultCode == source.resultCode,
        "secure result codec rejected command family 46");
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
    CheckSkillUnlearnClassifier(&checks);
    CheckGrowthReset(&checks);
    CheckRegistry(&checks);
    CheckSkillUnlearnResultSettlement(&checks);
    CheckSkillUnlearnResultCodec(&checks);
    return checks.failures;
}
