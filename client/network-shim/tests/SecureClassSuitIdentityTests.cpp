#include "SecureClassSuitIdentityTests.h"

#include "../src/SecureClassSuitCommandIdentity.h"
#include "../src/SecureLegacyCommandIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"

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
    std::uint8_t randomSeed = 1;
    std::uint64_t now = 90'000;
};

void Write16(std::uint8_t* destination, std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] = static_cast<std::uint8_t>(value >> 8U);
}

void Write32(std::uint8_t* destination, std::uint32_t value) {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] = static_cast<std::uint8_t>(
            value >> (index * 8U));
    }
}

bool Random(
    void* context,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* hooks = static_cast<Hooks*>(context);
    auto* output = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0;
         index < destinationBytes;
         ++index) {
        output[index] = static_cast<std::uint8_t>(
            hooks->randomSeed + index);
    }
    ++hooks->randomSeed;
    return true;
}

bool Clock(
    void* context,
    std::uint64_t* unixMilliseconds) noexcept {
    if (context == nullptr || unixMilliseconds == nullptr) {
        return false;
    }
    *unixMilliseconds = static_cast<Hooks*>(context)->now;
    return true;
}

void BuildLoginPacket(
    std::uint8_t* packet,
    std::uint8_t seed = 20) {
    constexpr std::size_t PacketBytes =
        4 + SecurePrincipalFingerprintBytes;
    std::memset(packet, 0, PacketBytes);
    Write16(packet, static_cast<std::uint16_t>(PacketBytes));
    Write16(packet + 2, LegacyLoginGameServerOpcode);
    for (std::size_t index = 0;
         index < SecurePrincipalFingerprintBytes;
         ++index) {
        packet[4 + index] = static_cast<std::uint8_t>(seed + index);
    }
}

bool RequiresSecondaryItem(LegacyClassSuitAction action) {
    return action != LegacyClassSuitAction::ConvertToCommon;
}

bool RequiresTertiaryItem(LegacyClassSuitAction action) {
    return action == LegacyClassSuitAction::AddAttribute;
}

void BuildClassSuitPacket(
    std::uint8_t* packet,
    LegacyClassSuitAction action,
    int gearReference,
    int insigniaReference,
    std::uint32_t npcId = LegacySpartaClassSuitNpc,
    int scratch = 0,
    int thirdItemReference = -1) {
    std::memset(packet, 0xFF, LegacyClassSuitActionPacketBytes);
    Write16(packet, LegacyClassSuitActionPacketBytes);
    Write16(packet + 2, LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyClassSuitDialog);
    Write32(packet + 12, LegacyClassSuitDialog);
    Write32(packet + 16, static_cast<std::uint32_t>(action));
    Write32(
        packet + 20 + LegacyClassSuitScratchArgument * 4,
        static_cast<std::uint32_t>(scratch));
    if (gearReference >= 0) {
        Write32(
            packet + 20 + LegacyClassSuitGearArgument * 4,
            static_cast<std::uint32_t>(gearReference));
    }
    if (insigniaReference >= 0) {
        Write32(
            packet + 20 + LegacyClassSuitInsigniaArgument * 4,
            static_cast<std::uint32_t>(insigniaReference));
    }
    if (thirdItemReference >= 0) {
        Write32(
            packet + 20 + LegacyClassSuitThirdItemArgument * 4,
            static_cast<std::uint32_t>(thirdItemReference));
    }
}

bool Establish(
    SecurePendingOperationRegistry* registry,
    bool setCharacter = true) {
    std::uint8_t login[4 + SecurePrincipalFingerprintBytes]{};
    BuildLoginPacket(login);
    LegacyPacketDescriptor descriptor{};
    if (registry == nullptr ||
        registry->DescribePacket(login, sizeof(login), &descriptor) !=
            SecureOperationRegistryResult::Success ||
        descriptor.hasOperation) {
        return false;
    }
    return !setCharacter ||
        registry->SetCharacter(810) ==
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

void CheckCanonicalPackets(Checks* checks) {
    struct Case final {
        LegacyClassSuitAction action;
        SecureLegacyCommandFamily family;
    };
    const Case cases[]{
        {LegacyClassSuitAction::ExchangeTierI,
            SecureLegacyCommandFamily::ClassSuitExchangeTierI},
        {LegacyClassSuitAction::AddAttribute,
            SecureLegacyCommandFamily::ClassSuitAddAttribute},
        {LegacyClassSuitAction::DeleteAttribute,
            SecureLegacyCommandFamily::ClassSuitDeleteAttribute},
        {LegacyClassSuitAction::ConvertToCommon,
            SecureLegacyCommandFamily::ClassSuitConvertToCommon},
        {LegacyClassSuitAction::UpgradeTierII,
            SecureLegacyCommandFamily::ClassSuitUpgradeTierII},
        {LegacyClassSuitAction::UpgradeTierIII,
            SecureLegacyCommandFamily::ClassSuitUpgradeTierIII},
        {LegacyClassSuitAction::UpgradeTierIV,
            SecureLegacyCommandFamily::ClassSuitUpgradeTierIV},
    };
    const std::uint32_t cities[]{
        LegacySpartaClassSuitNpc,
        LegacyAthensClassSuitNpc,
    };

    for (const auto city : cities) {
        for (const auto& expected : cases) {
            std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
            BuildClassSuitPacket(
                packet,
                expected.action,
                112,
                RequiresSecondaryItem(expected.action) ? 123 : -1,
                city,
                -1,
                RequiresTertiaryItem(expected.action) ? 134 : -1);
            LegacyClassSuitCommand command{};
            checks->Require(
                ClassifyLegacyClassSuitPacket(
                    packet, sizeof(packet), &command) ==
                    LegacyClassSuitPacketKind::Commit &&
                TryReadLegacyClassSuitCommand(
                    packet, sizeof(packet), &command) &&
                command.action == expected.action &&
                command.npcId == city &&
                command.gearBagSlot == 12 &&
                command.secondaryBagSlot ==
                    (RequiresSecondaryItem(expected.action) ? 23 : -1) &&
                command.tertiaryBagSlot ==
                    (RequiresTertiaryItem(expected.action) ? 34 : -1),
                "Canonical Class Suit commit did not parse");

            Hooks hooks{};
            SecurePendingOperationRegistry registry(
                &hooks, Random, &hooks, Clock);
            LegacyPacketDescriptor descriptor{};
            checks->Require(
                Establish(&registry) &&
                registry.DescribePacket(
                    packet, sizeof(packet), &descriptor) ==
                    SecureOperationRegistryResult::Success &&
                descriptor.hasOperation,
                "Class Suit commit did not receive a UUID");

            SecureLegacyCommandResult result{};
            result.disposition = SecureLegacyCommandDisposition::Applied;
            result.commandFamily = expected.family;
            result.inventoryRevision = 1;
            std::memcpy(
                result.operationId,
                descriptor.operation.operationId,
                sizeof(result.operationId));
            std::uint8_t encoded[SecureLegacyCommandResultPayloadBytes]{};
            SecureLegacyCommandResult decoded{};
            checks->Require(
                TryEncodeSecureLegacyCommandResult(
                    result, encoded, sizeof(encoded)) &&
                TryDecodeSecureLegacyCommandResult(
                    encoded, sizeof(encoded), &decoded) &&
                decoded.commandFamily == expected.family &&
                registry.Resolve(decoded) ==
                    SecureOperationRegistryResult::Success,
                "Class Suit command family did not settle its UUID");
        }
    }
}

void CheckNavigationAndForeignPackets(Checks* checks) {
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    LegacyClassSuitCommand command{};

    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        -1,
        -1,
        LegacySpartaClassSuitNpc,
        -1);
    checks->Require(
        ClassifyLegacyClassSuitPacket(packet, sizeof(packet), &command) ==
            LegacyClassSuitPacketKind::UnrelatedOrNavigation,
        "All--1 Class Suit navigation received an identity");
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::UpgradeTierII,
        -1,
        -1);
    checks->Require(
        ClassifyLegacyClassSuitPacket(packet, sizeof(packet), &command) ==
            LegacyClassSuitPacketKind::UnrelatedOrNavigation,
        "Scratch-zero Class Suit navigation received an identity");

    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        112,
        123,
        5066);
    checks->Require(
        ClassifyLegacyClassSuitPacket(packet, sizeof(packet), &command) ==
            LegacyClassSuitPacketKind::UnrelatedOrNavigation,
        "Class Suit parser claimed a foreign NPC");
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        112,
        123);
    Write32(packet + 8, LegacyClassSuitDialog + 1);
    checks->Require(
        ClassifyLegacyClassSuitPacket(packet, sizeof(packet), &command) ==
            LegacyClassSuitPacketKind::UnrelatedOrNavigation,
        "Class Suit parser claimed a foreign dialog");
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        112,
        123);
    Write16(packet + 2, 10068);
    checks->Require(
        ClassifyLegacyClassSuitPacket(packet, sizeof(packet), &command) ==
            LegacyClassSuitPacketKind::UnrelatedOrNavigation,
        "Class Suit parser claimed a foreign opcode");

    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        112,
        123);
    Write32(packet + 16, 107);
    checks->Require(
        ClassifyLegacyClassSuitPacket(packet, sizeof(packet), &command) ==
            LegacyClassSuitPacketKind::UnrelatedOrNavigation,
        "Unresolved fifth-attribute action received an identity");
    Write32(packet + 16, 103);
    checks->Require(
        ClassifyLegacyClassSuitPacket(packet, sizeof(packet), &command) ==
            LegacyClassSuitPacketKind::UnrelatedOrNavigation,
        "Class Suit guideline action received an identity");
}

void CheckMalformedPackets(Checks* checks) {
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    LegacyClassSuitCommand command{};
    auto requireInvalid = [&](const char* message) {
        checks->Require(
            ClassifyLegacyClassSuitPacket(
                packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::InvalidMutation &&
            !TryReadLegacyClassSuitCommand(
                packet, sizeof(packet), &command),
            message);
    };

    BuildClassSuitPacket(packet, LegacyClassSuitAction::ExchangeTierI, 112, 123);
    Write16(packet, LegacyClassSuitActionPacketBytes - 1);
    requireInvalid("Class Suit accepted a declared-length mismatch");
    BuildClassSuitPacket(packet, LegacyClassSuitAction::ExchangeTierI, 112, 123);
    Write32(packet + 12, LegacyClassSuitDialog + 1);
    requireInvalid("Class Suit accepted a mismatched duplicate dialog");
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        112,
        123,
        LegacySpartaClassSuitNpc,
        1);
    requireInvalid("Class Suit accepted a non-stock scratch value");
    BuildClassSuitPacket(packet, LegacyClassSuitAction::ExchangeTierI, 112, 123);
    Write32(packet + 20 + 1 * 4, 0);
    requireInvalid("Class Suit accepted an extra argument");
    BuildClassSuitPacket(packet, LegacyClassSuitAction::ExchangeTierI, 99, 123);
    requireInvalid("Class Suit accepted a low bag reference");
    BuildClassSuitPacket(packet, LegacyClassSuitAction::UpgradeTierIV, 112, 196);
    requireInvalid("Class Suit accepted a high bag reference");
    BuildClassSuitPacket(packet, LegacyClassSuitAction::UpgradeTierII, 112, 112);
    requireInvalid("Class Suit accepted duplicate bag references");
    BuildClassSuitPacket(packet, LegacyClassSuitAction::ConvertToCommon, 112, 123);
    requireInvalid("Convert-to-common accepted an insignia argument");
    BuildClassSuitPacket(packet, LegacyClassSuitAction::AddAttribute, 112, 123);
    requireInvalid("Add-attribute accepted a missing class stone");
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::DeleteAttribute,
        112,
        123,
        LegacySpartaClassSuitNpc,
        0,
        134);
    requireInvalid("Delete-attribute accepted a third item");
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::AddAttribute,
        112,
        123,
        LegacySpartaClassSuitNpc,
        0,
        123);
    requireInvalid("Add-attribute accepted duplicate material references");

    BuildClassSuitPacket(packet, LegacyClassSuitAction::ExchangeTierI, 112, 123);
    checks->Require(
        ClassifyLegacyClassSuitPacket(
            packet,
            LegacyClassSuitActionPacketBytes - 1,
            &command) == LegacyClassSuitPacketKind::InvalidMutation,
        "Truncated Class Suit endpoint did not fail closed");
    checks->Require(
        !TryReadLegacyClassSuitCommand(packet, sizeof(packet), nullptr),
        "Class Suit parser accepted a null output command");
}

void CheckRegistryIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(
        Establish(&registry),
        "Class Suit registry fixture did not establish identity");

    std::uint8_t sparta[LegacyClassSuitActionPacketBytes]{};
    std::uint8_t athens[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitPacket(
        sparta,
        LegacyClassSuitAction::UpgradeTierIII,
        112,
        123,
        LegacySpartaClassSuitNpc);
    BuildClassSuitPacket(
        athens,
        LegacyClassSuitAction::UpgradeTierIII,
        112,
        123,
        LegacyAthensClassSuitNpc);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor sameEndpointRetry{};
    LegacyPacketDescriptor otherEndpoint{};
    checks->Require(
        registry.DescribePacket(sparta, sizeof(sparta), &first) ==
            SecureOperationRegistryResult::Success &&
        registry.DescribePacket(
            sparta, sizeof(sparta), &sameEndpointRetry) ==
            SecureOperationRegistryResult::Success &&
        SameOperation(first, sameEndpointRetry) &&
        registry.Snapshot().pending == 1,
        "Same-endpoint Class Suit retry did not reuse its UUID");
    checks->Require(
        registry.DescribePacket(athens, sizeof(athens), &otherEndpoint) ==
            SecureOperationRegistryResult::Success &&
        !SameOperation(first, otherEndpoint) &&
        registry.Snapshot().pending == 2,
        "Different Class Suit NPC endpoint reused a pending UUID");

    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily =
        SecureLegacyCommandFamily::ClassSuitUpgradeTierIII;
    std::memcpy(
        result.operationId,
        first.operation.operationId,
        sizeof(result.operationId));
    checks->Require(
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success &&
        registry.Snapshot().pending == 1,
        "Class Suit result did not settle its UUID");

    std::memcpy(
        result.operationId,
        otherEndpoint.operation.operationId,
        sizeof(result.operationId));
    checks->Require(
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success &&
        registry.Snapshot().pending == 0,
        "Other-endpoint Class Suit result did not settle its UUID");

    SecurePendingOperationRegistry missing;
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        missing.DescribePacket(sparta, sizeof(sparta), &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "Class Suit commit without principal was not rejected");
    Hooks principalHooks{};
    SecurePendingOperationRegistry principalOnly(
        &principalHooks, Random, &principalHooks, Clock);
    checks->Require(
        Establish(&principalOnly, false) &&
        principalOnly.DescribePacket(sparta, sizeof(sparta), &descriptor) ==
            SecureOperationRegistryResult::NoCharacter,
        "Class Suit commit without character was not rejected");

    BuildClassSuitPacket(
        sparta,
        LegacyClassSuitAction::ExchangeTierI,
        -1,
        -1,
        LegacySpartaClassSuitNpc,
        -1);
    descriptor = LegacyPacketDescriptor{};
    checks->Require(
        missing.DescribePacket(sparta, sizeof(sparta), &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation,
        "Class Suit navigation required an operation identity");

    BuildClassSuitPacket(sparta, LegacyClassSuitAction::ExchangeTierI, 112, 123);
    Write32(sparta + 20 + 1 * 4, 0);
    checks->Require(
        registry.DescribePacket(sparta, sizeof(sparta), &descriptor) ==
            SecureOperationRegistryResult::InvalidPacket,
        "Malformed Class Suit mutation did not fail registry parsing");
}

} // namespace

int RunSecureClassSuitIdentityTests() {
    Checks checks{};
    CheckCanonicalPackets(&checks);
    CheckNavigationAndForeignPackets(&checks);
    CheckMalformedPackets(&checks);
    CheckRegistryIdentity(&checks);
    return checks.failures;
}
