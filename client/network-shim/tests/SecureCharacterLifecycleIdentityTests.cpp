#include "SecureCharacterLifecycleIdentityTests.h"

#include "../src/SecureCharacterLifecycleIdentity.h"
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
    std::uint64_t now = 50'000;
};

bool Random(
    void* context,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* hooks = static_cast<Hooks*>(context);
    if (hooks == nullptr ||
        destination == nullptr ||
        destinationBytes != 16) {
        return false;
    }
    auto* output =
        static_cast<std::uint8_t*>(destination);
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
    *unixMilliseconds =
        static_cast<Hooks*>(context)->now;
    return true;
}

void Write16(
    std::uint8_t* destination,
    std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
}

void Header(
    std::uint8_t* packet,
    std::uint16_t packetBytes,
    std::uint16_t opcode) {
    Write16(packet, packetBytes);
    Write16(packet + 2, opcode);
}

void WriteFixedName(
    std::uint8_t* destination,
    const char* value) {
    const std::size_t length = std::strlen(value);
    std::memcpy(
        destination,
        value,
        length < 32 ? length : 32);
}

void BuildLogin(
    const char* username,
    std::uint8_t* packet) {
    std::memset(packet, 0, 36);
    Header(packet, 36, LegacyLoginGameServerOpcode);
    WriteFixedName(packet + 4, username);
}

void BuildCreate(
    const char* name,
    std::uint8_t* packet,
    std::uint8_t scratch = 0,
    std::uint8_t profession = 0) {
    std::memset(
        packet,
        0,
        LegacyCreateCharacterPacketBytes);
    Header(
        packet,
        LegacyCreateCharacterPacketBytes,
        LegacyCreateCharacterOpcode);
    WriteFixedName(packet + 4, name);
    packet[36] = 0;
    packet[37] = 1;
    packet[38] = profession;
    packet[39] = 7;
    packet[40] = 2;
    packet[41] = 3;
    std::memset(packet + 42, scratch, 32);
    packet[74] = 1;
    std::memset(packet + 75, scratch, 5);
}

void BuildDelete(
    const char* username,
    const char* characterName,
    std::uint8_t* packet) {
    std::memset(
        packet,
        0,
        LegacyDeleteCharacterPacketBytes);
    Header(
        packet,
        LegacyDeleteCharacterPacketBytes,
        LegacyDeleteCharacterOpcode);
    WriteFixedName(packet + 4, username);
    WriteFixedName(packet + 36, characterName);
}

bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return first.hasOperation &&
        second.hasOperation &&
        std::memcmp(
            first.operation.operationId,
            second.operation.operationId,
            sizeof(first.operation.operationId)) == 0;
}

bool EstablishPrincipal(
    SecurePendingOperationRegistry* registry,
    const char* username) {
    std::uint8_t login[36]{};
    BuildLogin(username, login);
    LegacyPacketDescriptor descriptor{};
    return registry->DescribePacket(
               login,
               sizeof(login),
               &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation;
}

SecureLegacyCommandResult ResultFor(
    const LegacyPacketDescriptor& descriptor,
    SecureLegacyCommandFamily family) {
    SecureLegacyCommandResult result{};
    result.disposition =
        SecureLegacyCommandDisposition::Applied;
    result.commandFamily = family;
    result.inventoryRevision = 1;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

void CheckPacketClassification(Checks* checks) {
    std::uint8_t create[
        LegacyCreateCharacterPacketBytes]{};
    std::uint8_t compatible[
        LegacyCreateCharacterPacketBytes]{};
    BuildCreate("Jolo", create, 0x11);
    BuildCreate("Jolo", compatible, 0xD7);

    LegacyCharacterLifecycleIntent first{};
    LegacyCharacterLifecycleIntent second{};
    checks->Require(
        ClassifyLegacyCharacterLifecyclePacket(
            create,
            sizeof(create),
            &first) ==
                LegacyCharacterLifecyclePacketKind::Command &&
        first.family ==
            SecureLegacyCommandFamily::CharacterCreate &&
        ClassifyLegacyCharacterLifecyclePacket(
            compatible,
            sizeof(compatible),
            &second) ==
                LegacyCharacterLifecyclePacketKind::Command &&
        EqualCharacterLifecycleIntent(first, second),
        "CreateRole scratch bytes changed canonical intent");

    BuildCreate("Jolo", compatible, 0x11, 2);
    ClassifyLegacyCharacterLifecyclePacket(
        compatible,
        sizeof(compatible),
        &second);
    checks->Require(
        !EqualCharacterLifecycleIntent(first, second),
        "CreateRole profession was absent from canonical intent");

    std::uint8_t deleteFirst[
        LegacyDeleteCharacterPacketBytes]{};
    std::uint8_t deleteSecond[
        LegacyDeleteCharacterPacketBytes]{};
    BuildDelete("test2", "Jolo", deleteFirst);
    BuildDelete("untrusted-alias", "Jolo", deleteSecond);
    checks->Require(
        ClassifyLegacyCharacterLifecyclePacket(
            deleteFirst,
            sizeof(deleteFirst),
            &first) ==
                LegacyCharacterLifecyclePacketKind::Command &&
        first.family ==
            SecureLegacyCommandFamily::CharacterDelete &&
        ClassifyLegacyCharacterLifecyclePacket(
            deleteSecond,
            sizeof(deleteSecond),
            &second) ==
                LegacyCharacterLifecyclePacketKind::Command &&
        EqualCharacterLifecycleIntent(first, second),
        "DeleteRole trusted the client account-name field");
}

void CheckMalformedPackets(Checks* checks) {
    std::uint8_t create[
        LegacyCreateCharacterPacketBytes]{};
    LegacyCharacterLifecycleIntent intent{};
    BuildCreate("", create);
    checks->Require(
        ClassifyLegacyCharacterLifecyclePacket(
            create,
            sizeof(create),
            &intent) ==
            LegacyCharacterLifecyclePacketKind::InvalidMutation,
        "blank CreateRole name was accepted");

    BuildCreate("Jolo", create);
    create[36] = 2;
    checks->Require(
        ClassifyLegacyCharacterLifecyclePacket(
            create,
            sizeof(create),
            &intent) ==
            LegacyCharacterLifecyclePacketKind::InvalidMutation,
        "invalid CreateRole gender was accepted");

    BuildCreate("Jolo", create);
    create[74] = 4;
    checks->Require(
        ClassifyLegacyCharacterLifecyclePacket(
            create,
            sizeof(create),
            &intent) ==
            LegacyCharacterLifecyclePacketKind::InvalidMutation,
        "invalid CreateRole faith was accepted");

    BuildCreate("Jolo", create);
    Header(create, 79, LegacyCreateCharacterOpcode);
    checks->Require(
        ClassifyLegacyCharacterLifecyclePacket(
            create,
            79,
            &intent) ==
            LegacyCharacterLifecyclePacketKind::InvalidMutation,
        "short CreateRole frame was not rejected");

    Header(create, 80, 10005);
    checks->Require(
        ClassifyLegacyCharacterLifecyclePacket(
            create,
            sizeof(create),
            &intent) ==
            LegacyCharacterLifecyclePacketKind::Unrelated,
        "unrelated lifecycle-adjacent opcode was claimed");
}

void CheckRegistryIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    std::uint8_t create[
        LegacyCreateCharacterPacketBytes]{};
    BuildCreate("Jolo", create, 0x11);
    LegacyPacketDescriptor first{};
    checks->Require(
        registry.DescribePacket(
            create,
            sizeof(create),
            &first) ==
            SecureOperationRegistryResult::NoPrincipal,
        "CreateRole did not require a login principal");
    checks->Require(
        EstablishPrincipal(&registry, "test2"),
        "lifecycle principal setup failed");
    checks->Require(
        registry.DescribePacket(
            create,
            sizeof(create),
            &first) ==
                SecureOperationRegistryResult::Success &&
        first.hasOperation &&
        first.operation.opcode ==
            LegacyCreateCharacterOpcode &&
        first.operation.packetBytes ==
            LegacyCreateCharacterPacketBytes,
        "CreateRole did not receive an operation marker");

    BuildCreate("Jolo", create, 0xE2);
    LegacyPacketDescriptor retried{};
    checks->Require(
        registry.DescribePacket(
            create,
            sizeof(create),
            &retried) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(first, retried),
        "equivalent CreateRole retry changed UUID");

    std::uint8_t deletion[
        LegacyDeleteCharacterPacketBytes]{};
    BuildDelete("test2", "Jolo", deletion);
    LegacyPacketDescriptor deleteFirst{};
    checks->Require(
        registry.DescribePacket(
            deletion,
            sizeof(deletion),
            &deleteFirst) ==
                SecureOperationRegistryResult::Success &&
        deleteFirst.hasOperation,
        "DeleteRole did not receive an operation marker");
    BuildDelete("ignored-client-name", "Jolo", deletion);
    LegacyPacketDescriptor deleteRetry{};
    checks->Require(
        registry.DescribePacket(
            deletion,
            sizeof(deletion),
            &deleteRetry) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(deleteFirst, deleteRetry),
        "equivalent DeleteRole retry changed UUID");

    BuildDelete("test2", "Another", deletion);
    LegacyPacketDescriptor anotherDelete{};
    checks->Require(
        registry.DescribePacket(
            deletion,
            sizeof(deletion),
            &anotherDelete) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(deleteFirst, anotherDelete),
        "different character deletes shared a UUID");

    checks->Require(
        EstablishPrincipal(&registry, "account7"),
        "lifecycle principal switch failed");
    BuildCreate("Jolo", create, 0x11);
    LegacyPacketDescriptor otherPrincipal{};
    checks->Require(
        registry.DescribePacket(
            create,
            sizeof(create),
            &otherPrincipal) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(first, otherPrincipal),
        "different principals shared a lifecycle UUID");
}

void CheckResolvedLifecycleTransitions(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        EstablishPrincipal(&registry, "test2"),
        "resolved lifecycle principal setup failed");

    std::uint8_t create[
        LegacyCreateCharacterPacketBytes]{};
    BuildCreate("Jolo", create, 1);
    LegacyPacketDescriptor first{};
    registry.DescribePacket(
        create,
        sizeof(create),
        &first);
    const auto result = ResultFor(
        first,
        SecureLegacyCommandFamily::CharacterCreate);
    checks->Require(
        registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
        registry.Snapshot().pending == 0 &&
        registry.Snapshot().resolved == 1,
        "CreateRole result did not resolve pending identity");

    BuildCreate("Jolo", create, 2);
    LegacyPacketDescriptor afterResult{};
    checks->Require(
        registry.DescribePacket(
            create,
            sizeof(create),
            &afterResult) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(first, afterResult) &&
        registry.Resolve(result) ==
            SecureOperationRegistryResult::Success,
        "resolved CreateRole intent reused its terminal UUID");

    const auto secondCreateResult = ResultFor(
        afterResult,
        SecureLegacyCommandFamily::CharacterCreate);
    checks->Require(
        registry.Resolve(secondCreateResult) ==
            SecureOperationRegistryResult::Success,
        "second CreateRole result did not resolve");

    std::uint8_t deletion[
        LegacyDeleteCharacterPacketBytes]{};
    BuildDelete("test2", "Jolo", deletion);
    LegacyPacketDescriptor deleted{};
    checks->Require(
        registry.DescribePacket(
            deletion,
            sizeof(deletion),
            &deleted) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(afterResult, deleted),
        "intervening DeleteRole reused a CreateRole UUID");
    const auto deleteResult = ResultFor(
        deleted,
        SecureLegacyCommandFamily::CharacterDelete);
    checks->Require(
        registry.Resolve(deleteResult) ==
            SecureOperationRegistryResult::Success,
        "DeleteRole transition did not resolve");

    BuildCreate("Jolo", create, 3);
    LegacyPacketDescriptor recreated{};
    checks->Require(
        registry.DescribePacket(
            create,
            sizeof(create),
            &recreated) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(first, recreated) &&
        !SameOperation(afterResult, recreated) &&
        !SameOperation(deleted, recreated),
        "create-delete-create transitions reused a terminal UUID");

    auto wrongFamily = result;
    wrongFamily.commandFamily =
        SecureLegacyCommandFamily::CharacterDelete;
    checks->Require(
        registry.Resolve(wrongFamily) ==
            SecureOperationRegistryResult::FamilyConflict,
        "lifecycle result accepted the wrong command family");

    hooks.now +=
        SecurePendingOperationLifetimeMilliseconds + 1;
    LegacyPacketDescriptor expired{};
    checks->Require(
        registry.DescribePacket(
            create,
            sizeof(create),
            &expired) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(recreated, expired),
        "expired pending lifecycle operation reused its UUID");
}

void CheckResultCodec(Checks* checks) {
    const SecureLegacyCommandFamily families[]{
        SecureLegacyCommandFamily::CharacterCreate,
        SecureLegacyCommandFamily::CharacterDelete};
    for (const auto family : families) {
        SecureLegacyCommandResult input{};
        input.disposition =
            SecureLegacyCommandDisposition::Applied;
        input.commandFamily = family;
        input.inventoryRevision = 17;
        input.operationId[0] = 1;
        std::uint8_t payload[
            SecureLegacyCommandResultPayloadBytes]{};
        SecureLegacyCommandResult decoded{};
        checks->Require(
            TryEncodeSecureLegacyCommandResult(
                input,
                payload,
                sizeof(payload)) &&
            TryDecodeSecureLegacyCommandResult(
                payload,
                sizeof(payload),
                &decoded) &&
            decoded.commandFamily == family &&
            decoded.inventoryRevision == 17,
            "lifecycle result frame did not round trip");
    }
}

void CheckCapacity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    checks->Require(
        EstablishPrincipal(&registry, "test2"),
        "lifecycle capacity principal setup failed");

    std::uint8_t create[
        LegacyCreateCharacterPacketBytes]{};
    char name[16]{};
    LegacyPacketDescriptor descriptor{};
    bool filled = true;
    for (std::size_t index = 0;
         index < SecurePendingOperationCapacity;
         ++index) {
        std::snprintf(
            name,
            sizeof(name),
            "Hero%u",
            static_cast<unsigned>(index));
        BuildCreate(name, create);
        filled = filled &&
            registry.DescribePacket(
                create,
                sizeof(create),
                &descriptor) ==
                SecureOperationRegistryResult::Success;
    }
    BuildCreate("Overflow", create);
    checks->Require(
        filled &&
        registry.Snapshot().pending ==
            SecurePendingOperationCapacity &&
        registry.DescribePacket(
            create,
            sizeof(create),
            &descriptor) ==
            SecureOperationRegistryResult::Capacity,
        "lifecycle operation registry exceeded its bound");
}

} // namespace

int RunSecureCharacterLifecycleIdentityTests() {
    Checks checks{};
    CheckPacketClassification(&checks);
    CheckMalformedPackets(&checks);
    CheckRegistryIdentity(&checks);
    CheckResolvedLifecycleTransitions(&checks);
    CheckResultCodec(&checks);
    CheckCapacity(&checks);
    return checks.failures;
}
