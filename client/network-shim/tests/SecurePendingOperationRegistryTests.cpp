#include "SecurePendingOperationRegistryTests.h"

#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::LegacyPacketDescriptor;
using godswar::network::SecureLegacyCommandDisposition;
using godswar::network::SecureLegacyCommandFamily;
using godswar::network::SecureLegacyCommandResult;
using godswar::network::SecureOperationRegistryResult;
using godswar::network::SecurePendingOperationCapacity;
using godswar::network::SecurePendingOperationLifetimeMilliseconds;
using godswar::network::SecurePendingOperationRegistry;
using godswar::network::TryReadLegacyEnterMainCharacterId;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void Write16(
    std::uint8_t* destination,
    std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
}

void Write32(
    std::uint8_t* destination,
    std::uint32_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
    destination[2] =
        static_cast<std::uint8_t>(value >> 16U);
    destination[3] =
        static_cast<std::uint8_t>(value >> 24U);
}

struct Hooks final {
    std::uint64_t now = 1'000;
    std::uint8_t randomSeed = 1;
    bool failRandom = false;
    bool failClock = false;
};

bool Random(
    void* contextValue,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* context = static_cast<Hooks*>(contextValue);
    if (context == nullptr ||
        destination == nullptr ||
        destinationBytes != 16 ||
        context->failRandom) {
        return false;
    }
    auto* bytes = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0;
         index < destinationBytes;
         ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            context->randomSeed + index);
    }
    ++context->randomSeed;
    return true;
}

bool Clock(
    void* contextValue,
    std::uint64_t* now) noexcept {
    auto* context = static_cast<Hooks*>(contextValue);
    if (context == nullptr ||
        now == nullptr ||
        context->failClock) {
        return false;
    }
    *now = context->now;
    return true;
}

void Header(
    std::uint8_t* packet,
    std::uint16_t bytes,
    std::uint16_t opcode) {
    Write16(packet, bytes);
    Write16(packet + 2, opcode);
}

void LoginPacket(
    const char* username,
    std::uint8_t* packet,
    std::size_t packetBytes) {
    std::memset(packet, 0, packetBytes);
    Header(
        packet,
        static_cast<std::uint16_t>(packetBytes),
        10000);
    const std::size_t usernameBytes = std::strlen(username);
    std::memcpy(
        packet + 4,
        username,
        usernameBytes < 32 ? usernameBytes : 32);
}

void SelectionPacket(
    int bagSlot,
    bool selected,
    std::uint8_t scratch,
    std::uint8_t* packet) {
    std::memset(packet, 0, 16);
    Header(packet, 16, 10193);
    Write32(
        packet + 4,
        static_cast<std::uint32_t>(bagSlot / 24));
    Write32(
        packet + 8,
        static_cast<std::uint32_t>(bagSlot % 24));
    packet[12] = selected ? 1 : 0;
    packet[13] = scratch;
    packet[14] = static_cast<std::uint8_t>(scratch + 1);
    packet[15] = static_cast<std::uint8_t>(scratch + 2);
}

void FinalPacket(
    std::uint32_t npcId,
    std::uint8_t* packet) {
    std::memset(packet, 0xCD, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, npcId);
    Write32(packet + 8, 4);
    Write32(packet + 16, 4);
}

SecureOperationRegistryResult Describe(
    SecurePendingOperationRegistry* registry,
    const void* packet,
    std::size_t packetBytes,
    LegacyPacketDescriptor* descriptor = nullptr) {
    LegacyPacketDescriptor local{};
    return registry->DescribePacket(
        packet,
        packetBytes,
        descriptor == nullptr ? &local : descriptor);
}

void EstablishSelection(
    SecurePendingOperationRegistry* registry,
    const char* username,
    int bagSlot,
    int characterId = 101) {
    std::uint8_t login[36]{};
    LoginPacket(username, login, sizeof(login));
    std::uint8_t selection[16]{};
    SelectionPacket(bagSlot, true, 0x91, selection);
    Check(
        Describe(registry, login, sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            registry->SetCharacter(characterId) ==
                SecureOperationRegistryResult::Success &&
            Describe(registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success,
        "principal, character, and selection setup failed");
}

void CheckEnterMainCharacterIdentity() {
    std::uint8_t message[
        sizeof(void*) + 0x0658]{};
    auto* packet = message + sizeof(void*);
    Header(packet, 0x0658, 0x2723);
    Write32(packet + 4, 734);

    int characterId = 0;
    Check(
        TryReadLegacyEnterMainCharacterId(
            message,
            &characterId) &&
            characterId == 734,
        "authenticated EnterMain character identity was not read");

    Header(packet, 0x0657, 0x2723);
    Check(
        !TryReadLegacyEnterMainCharacterId(
            message,
            &characterId),
        "wrong-length EnterMain changed character identity");
    Header(packet, 0x0658, 0x2723);
    Write32(packet + 4, 0);
    Check(
        !TryReadLegacyEnterMainCharacterId(
            message,
            &characterId),
        "zero EnterMain character identity was accepted");
}

void CheckIdentityAndReconnect() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    std::uint8_t final[92]{};
    FinalPacket(5067, final);
    LegacyPacketDescriptor descriptor{};
    Check(
        Describe(
            &registry,
            final,
            sizeof(final),
            &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "valuable command was accepted without a principal");

    EstablishSelection(&registry, "test2", 27);
    std::uint8_t clear[16]{};
    SelectionPacket(27, false, 0xE4, clear);
    Check(
        Describe(&registry, clear, sizeof(clear)) ==
            SecureOperationRegistryResult::Success,
        "native clear sequence was rejected");

    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retried{};
    LegacyPacketDescriptor changedRequest{};
    std::uint8_t differentRequest[92]{};
    std::memcpy(
        differentRequest,
        final,
        sizeof(differentRequest));
    differentRequest[91] ^= 0x5A;
    Check(
        Describe(&registry, final, sizeof(final), &first) ==
                SecureOperationRegistryResult::Success &&
            first.hasOperation &&
            Describe(&registry, final, sizeof(final), &retried) ==
                SecureOperationRegistryResult::Success &&
            retried.hasOperation &&
            Describe(
                &registry,
                differentRequest,
                sizeof(differentRequest),
                &changedRequest) ==
                SecureOperationRegistryResult::Success &&
            std::memcmp(
                first.operation.operationId,
                retried.operation.operationId,
                16) == 0 &&
            std::memcmp(
                first.operation.operationId,
                changedRequest.operation.operationId,
                16) == 0 &&
            registry.Snapshot().pending == 1 &&
            (first.operation.operationId[6] & 0xF0U) == 0x40U &&
            (first.operation.operationId[8] & 0xC0U) == 0x80U,
        "unresolved same-slot action did not retain one UUID");

    SecureLegacyCommandResult conflict{};
    conflict.disposition =
        SecureLegacyCommandDisposition::Conflict;
    conflict.commandFamily =
        static_cast<SecureLegacyCommandFamily>(99);
    std::memcpy(
        conflict.operationId,
        first.operation.operationId,
        16);
    Check(
        registry.Resolve(conflict) ==
                SecureOperationRegistryResult::FamilyConflict &&
            registry.Snapshot().pending == 1,
        "family-conflicting result changed registry state");

    conflict.commandFamily =
        SecureLegacyCommandFamily::MakeAttributeStone;
    conflict.disposition =
        SecureLegacyCommandDisposition::Applied;
    conflict.inventoryRevision = 42;
    Check(
        registry.Resolve(conflict) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 0 &&
            registry.Snapshot().resolved == 1 &&
            registry.Resolve(conflict) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().resolved == 1,
        "terminal result did not resolve pending operation");

    conflict.commandFamily =
        static_cast<SecureLegacyCommandFamily>(99);
    Check(
        registry.Resolve(conflict) ==
                SecureOperationRegistryResult::FamilyConflict &&
            registry.Snapshot().resolved == 1,
        "family-conflicting duplicate result was accepted");
    conflict.commandFamily =
        SecureLegacyCommandFamily::MakeAttributeStone;

    LegacyPacketDescriptor fresh{};
    SelectionPacket(27, true, 0x71, clear);
    Check(
        Describe(&registry, clear, sizeof(clear)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, final, sizeof(final), &fresh) ==
                SecureOperationRegistryResult::Success &&
            std::memcmp(
                fresh.operation.operationId,
                first.operation.operationId,
                16) != 0,
        "new selection after a terminal result did not create a fresh operation");

    SecureLegacyCommandResult unknown = conflict;
    unknown.operationId[0] ^= 0x7F;
    Check(
        registry.Resolve(unknown) ==
            SecureOperationRegistryResult::UnknownOperation,
        "unknown result was accepted");
}

void CheckPrincipalAndExpiry() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    EstablishSelection(&registry, "account-a", 3);
    std::uint8_t final[92]{};
    FinalPacket(5209, final);
    LegacyPacketDescriptor original{};
    Check(
        Describe(&registry, final, sizeof(final), &original) ==
            SecureOperationRegistryResult::Success,
        "original pending operation was not created");

    std::uint8_t login[36]{};
    LoginPacket("account-b", login, sizeof(login));
    Check(
        Describe(&registry, login, sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, final, sizeof(final)) ==
                SecureOperationRegistryResult::NoCharacter,
        "account switch inherited another principal's character");

    EstablishSelection(&registry, "account-b", 3);
    LegacyPacketDescriptor otherAccount{};
    Check(
        Describe(
            &registry,
            final,
            sizeof(final),
            &otherAccount) ==
                SecureOperationRegistryResult::Success &&
            std::memcmp(
                original.operation.operationId,
                otherAccount.operation.operationId,
                16) != 0,
        "account switch inherited another principal's UUID");

    hooks.now +=
        SecurePendingOperationLifetimeMilliseconds + 1;
    LegacyPacketDescriptor afterExpiry{};
    Check(
        Describe(
            &registry,
            final,
            sizeof(final),
            &afterExpiry) ==
                SecureOperationRegistryResult::Success &&
            std::memcmp(
                otherAccount.operation.operationId,
                afterExpiry.operation.operationId,
                16) != 0,
        "expired pending operation was reused");
}

void CheckCharacterScopedReconnect() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    EstablishSelection(
        &registry,
        "multi-character",
        11,
        101);
    std::uint8_t final[92]{};
    FinalPacket(5067, final);
    LegacyPacketDescriptor characterA{};
    Check(
        Describe(
            &registry,
            final,
            sizeof(final),
            &characterA) ==
                SecureOperationRegistryResult::Success &&
            characterA.hasOperation,
        "character A pending operation was not created");

    Check(
        registry.SetCharacter(202) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().hasCharacter &&
            registry.Snapshot().characterId == 202 &&
            !registry.Snapshot().hasSelection,
        "character transition did not clear ephemeral selection");
    std::uint8_t selection[16]{};
    SelectionPacket(11, true, 0x34, selection);
    LegacyPacketDescriptor characterB{};
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success &&
            Describe(
                &registry,
                final,
                sizeof(final),
                &characterB) ==
                SecureOperationRegistryResult::Success &&
            std::memcmp(
                characterA.operation.operationId,
                characterB.operation.operationId,
                16) != 0,
        "character B reused character A's pending UUID");

    Check(
        registry.SetCharacter(101) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasSelection,
        "return to character A retained character B selection");
    SelectionPacket(11, true, 0x56, selection);
    LegacyPacketDescriptor characterARetry{};
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success &&
            Describe(
                &registry,
                final,
                sizeof(final),
                &characterARetry) ==
                SecureOperationRegistryResult::Success &&
            std::memcmp(
                characterA.operation.operationId,
                characterARetry.operation.operationId,
                16) == 0 &&
            registry.Snapshot().pending == 2,
        "lost-result A/B/A retry did not recover character A's UUID");
}

void CheckCapacityAndFailures() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    EstablishSelection(&registry, "capacity", 0);
    std::uint8_t selection[16]{};
    std::uint8_t final[92]{};
    for (std::size_t index = 0;
         index < SecurePendingOperationCapacity;
         ++index) {
        SelectionPacket(
            static_cast<int>(index),
            true,
            static_cast<std::uint8_t>(index),
            selection);
        FinalPacket(
            index % 2 == 0 ? 5067 : 5209,
            final);
        Check(
            Describe(&registry, selection, sizeof(selection)) ==
                    SecureOperationRegistryResult::Success &&
                Describe(&registry, final, sizeof(final)) ==
                    SecureOperationRegistryResult::Success,
            "registry rejected an in-capacity operation");
    }
    SelectionPacket(47, true, 0, selection);
    FinalPacket(5067, final);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, final, sizeof(final)) ==
                SecureOperationRegistryResult::Capacity &&
            registry.Snapshot().pending ==
                SecurePendingOperationCapacity,
        "fixed registry capacity was not enforced");

    registry.Clear();
    EstablishSelection(&registry, "failures", 1);
    hooks.failRandom = true;
    FinalPacket(5067, final);
    Check(
        Describe(&registry, final, sizeof(final)) ==
            SecureOperationRegistryResult::RandomFailure,
        "CSPRNG failure did not fail closed");
    hooks.failRandom = false;
    hooks.failClock = true;
    Check(
        Describe(&registry, final, sizeof(final)) ==
            SecureOperationRegistryResult::ClockFailure,
        "clock failure did not fail closed");

    std::uint8_t malformed[16]{};
    Header(malformed, 15, 10193);
    Check(
        Describe(&registry, malformed, sizeof(malformed)) ==
            SecureOperationRegistryResult::InvalidPacket,
        "mismatched legacy length was accepted");
}

void CheckResolvedTombstoneExpiry() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    EstablishSelection(&registry, "resolved-expiry", 9);
    std::uint8_t final[92]{};
    FinalPacket(5067, final);
    LegacyPacketDescriptor descriptor{};
    Check(
        Describe(
            &registry,
            final,
            sizeof(final),
            &descriptor) ==
            SecureOperationRegistryResult::Success,
        "resolved-expiry operation setup failed");

    SecureLegacyCommandResult result{};
    result.disposition =
        SecureLegacyCommandDisposition::Applied;
    result.commandFamily =
        SecureLegacyCommandFamily::MakeAttributeStone;
    result.inventoryRevision = 9;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        16);
    Check(
        registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success,
        "fresh resolved tombstone did not absorb duplicate");
    hooks.now +=
        SecurePendingOperationLifetimeMilliseconds + 1;
    Check(
        registry.Resolve(result) ==
                SecureOperationRegistryResult::UnknownOperation &&
            registry.Snapshot().resolved == 0,
        "expired resolved tombstone remained authoritative");
}

} // namespace

int RunSecurePendingOperationRegistryTests() {
    Failures = 0;
    CheckEnterMainCharacterIdentity();
    CheckIdentityAndReconnect();
    CheckPrincipalAndExpiry();
    CheckCharacterScopedReconnect();
    CheckCapacityAndFailures();
    CheckResolvedTombstoneExpiry();
    return Failures;
}
