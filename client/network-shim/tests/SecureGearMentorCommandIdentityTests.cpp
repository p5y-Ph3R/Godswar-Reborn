#include "SecureGearMentorCommandIdentityTests.h"

#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <initializer_list>

namespace {

using godswar::network::LegacyGearMentorAction;
using godswar::network::LegacyPacketDescriptor;
using godswar::network::SecureLegacyCommandDisposition;
using godswar::network::SecureLegacyCommandFamily;
using godswar::network::SecureLegacyCommandResult;
using godswar::network::SecureOperationRegistryResult;
using godswar::network::SecurePendingOperationRegistry;
using godswar::network::TryDecodeSecureLegacyCommandResult;
using godswar::network::TryEncodeSecureLegacyCommandResult;
using godswar::network::TryReadLegacyGearMentorAction;

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
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] =
            static_cast<std::uint8_t>(
                value >> (index * 8U));
    }
}

void Header(
    std::uint8_t* packet,
    std::uint16_t packetBytes,
    std::uint16_t opcode) {
    Write16(packet, packetBytes);
    Write16(packet + 2, opcode);
}

void LoginPacket(
    const char* account,
    std::uint8_t* packet) {
    std::memset(packet, 0, 36);
    Header(packet, 36, 10000);
    const std::size_t bytes = std::strlen(account);
    std::memcpy(
        packet + 4,
        account,
        bytes < 32 ? bytes : 32);
}

void SelectionPacket(
    int bagSlot,
    bool selected,
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
    packet[13] = 0xA5;
    packet[14] = 0x5A;
    packet[15] = 0xC3;
}

void ActionPacket(
    std::uint32_t npcId,
    std::int32_t subId,
    std::uint8_t* packet) {
    std::memset(packet, 0xCD, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, npcId);
    Write32(packet + 8, 4);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(subId));
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

void Establish(
    SecurePendingOperationRegistry* registry,
    const char* account = "test2",
    int characterId = 505) {
    std::uint8_t login[36]{};
    LoginPacket(account, login);
    Check(
        Describe(registry, login, sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            registry->SetCharacter(characterId) ==
                SecureOperationRegistryResult::Success,
        "secure Gear Mentor identity setup failed");
}

SecureLegacyCommandResult ResultFor(
    const LegacyPacketDescriptor& descriptor,
    SecureLegacyCommandFamily family) {
    SecureLegacyCommandResult result{};
    result.disposition =
        SecureLegacyCommandDisposition::Applied;
    result.commandFamily = family;
    result.inventoryRevision = 41;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

void CheckExactPacketClassification() {
    std::uint8_t packet[92]{};
    LegacyGearMentorAction action =
        LegacyGearMentorAction::InitialMenu;
    std::uint32_t npcId = 0;

    ActionPacket(5067, 8, packet);
    Check(
        TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId) &&
            action ==
                LegacyGearMentorAction::TransformCrystal &&
            npcId == 5067,
        "Sparta Transform packet was not classified");

    ActionPacket(5209, 9, packet);
    Check(
        TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId) &&
            action ==
                LegacyGearMentorAction::CombineGemPieces &&
            npcId == 5209,
        "Athens Combine packet was not classified");

    ActionPacket(5067, -1, packet);
    Check(
        TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId) &&
            action == LegacyGearMentorAction::InitialMenu,
        "Gear Mentor initial-menu packet was not classified");

    ActionPacket(5067, 201, packet);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId),
        "server-only Combine sub-ID 201 was accepted on the wire");
    ActionPacket(5067, 8, packet);
    Header(packet, 91, 10069);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId),
        "wrong declared Transform length was accepted");
    Header(packet, 92, 10070);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId),
        "wrong Transform opcode was accepted");
    Header(packet, 92, 10069);
    Write32(packet + 4, 5140);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId),
        "Origin Enhancer was accepted as physical Gear Mentor");
    Write32(packet + 4, 5067);
    Write32(packet + 8, 118);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId),
        "Origin Enhancer dialog was accepted");
}

void CheckTransformIdentityAndResults() {
    SecurePendingOperationRegistry registry;
    Establish(&registry);
    std::uint8_t action[92]{};
    ActionPacket(5067, 8, action);
    Check(
        Describe(&registry, action, sizeof(action)) ==
            SecureOperationRegistryResult::NoSelection,
        "Transform without a selection did not fail closed");

    std::uint8_t selection[16]{};
    SelectionPacket(31, true, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "Transform selection was rejected");
    SelectionPacket(31, false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "Transform stock clear burst was rejected");

    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    Check(
        Describe(
            &registry,
            action,
            sizeof(action),
            &first) ==
                SecureOperationRegistryResult::Success &&
            first.hasOperation &&
            Describe(
                &registry,
                action,
                sizeof(action),
                &retry) ==
                SecureOperationRegistryResult::Success &&
            retry.hasOperation &&
            std::memcmp(
                first.operation.operationId,
                retry.operation.operationId,
                16) == 0,
        "Transform retry did not retain its operation UUID");

    auto wrong = ResultFor(
        first,
        SecureLegacyCommandFamily::MakeAttributeStone);
    Check(
        registry.Resolve(wrong) ==
                SecureOperationRegistryResult::FamilyConflict &&
            registry.Snapshot().pending == 1,
        "wrong-family Transform result changed state");
    auto applied = ResultFor(
        first,
        SecureLegacyCommandFamily::TransformCrystal);
    Check(
        registry.Resolve(applied) ==
                SecureOperationRegistryResult::Success &&
            registry.Resolve(applied) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 0 &&
            registry.Snapshot().resolved == 1 &&
            !registry.Snapshot().hasSelection,
        "Transform result did not resolve, tombstone, and clear selection");
}

void CheckFamilyScopedSameSlotIdentity() {
    SecurePendingOperationRegistry registry;
    Establish(&registry);
    std::uint8_t selection[16]{};
    SelectionPacket(12, true, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "same-slot family selection failed");

    std::uint8_t make[92]{};
    std::uint8_t transform[92]{};
    ActionPacket(5067, 4, make);
    ActionPacket(5067, 8, transform);
    LegacyPacketDescriptor makeDescriptor{};
    LegacyPacketDescriptor transformDescriptor{};
    Check(
        Describe(
            &registry,
            make,
            sizeof(make),
            &makeDescriptor) ==
                SecureOperationRegistryResult::Success &&
            Describe(
                &registry,
                transform,
                sizeof(transform),
                &transformDescriptor) ==
                SecureOperationRegistryResult::Success &&
            makeDescriptor.hasOperation &&
            transformDescriptor.hasOperation &&
            std::memcmp(
                makeDescriptor.operation.operationId,
                transformDescriptor.operation.operationId,
                16) != 0 &&
            registry.Snapshot().pending == 2,
        "different command families shared a same-slot UUID");
}

void CheckCombineNavigationAndConfirmation() {
    SecurePendingOperationRegistry registry;
    Establish(&registry);
    std::uint8_t selection[16]{};
    SelectionPacket(7, true, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "stale Combine setup selection failed");

    std::uint8_t combine[92]{};
    ActionPacket(5067, 9, combine);
    LegacyPacketDescriptor navigation{};
    Check(
        Describe(
            &registry,
            combine,
            sizeof(combine),
            &navigation) ==
                SecureOperationRegistryResult::Success &&
            !navigation.hasOperation &&
            registry.Snapshot().combinePageArmed &&
            registry.Snapshot().combineNpcId == 5067 &&
            !registry.Snapshot().hasSelection,
        "first action 9 was not bounded navigation");
    Check(
        Describe(&registry, combine, sizeof(combine)) ==
            SecureOperationRegistryResult::NoSelection,
        "armed Combine confirmation without selection did not fail closed");

    SelectionPacket(23, true, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "Combine selection was rejected");
    SelectionPacket(23, false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "Combine stock clear burst was rejected");

    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    Check(
        Describe(
            &registry,
            combine,
            sizeof(combine),
            &first) ==
                SecureOperationRegistryResult::Success &&
            first.hasOperation,
        "armed Combine confirmation was not marked");
    combine[91] ^= 0x5A;
    Check(
        Describe(
            &registry,
            combine,
            sizeof(combine),
            &retry) ==
                SecureOperationRegistryResult::Success &&
            retry.hasOperation &&
            std::memcmp(
                first.operation.operationId,
                retry.operation.operationId,
                16) == 0,
        "Combine scratch-tail retry changed operation UUID");

    auto result = ResultFor(
        first,
        SecureLegacyCommandFamily::CombineGemPieces);
    Check(
        registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().combinePageArmed &&
            !registry.Snapshot().hasSelection,
        "Combine result did not disarm its page and selection");

    LegacyPacketDescriptor nextNavigation{};
    Check(
        Describe(
            &registry,
            combine,
            sizeof(combine),
            &nextNavigation) ==
                SecureOperationRegistryResult::Success &&
            !nextNavigation.hasOperation &&
            registry.Snapshot().combinePageArmed,
        "new Combine action did not return to navigation");

    SelectionPacket(8, true, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "abandoned Combine selection setup failed");
    std::uint8_t enhance[92]{};
    ActionPacket(5067, 2, enhance);
    LegacyPacketDescriptor untracked{};
    Check(
        Describe(
            &registry,
            enhance,
            sizeof(enhance),
            &untracked) ==
                SecureOperationRegistryResult::Success &&
            !untracked.hasOperation &&
            !registry.Snapshot().combinePageArmed &&
            !registry.Snapshot().hasSelection,
        "another Gear Mentor action inherited abandoned Combine state");

    Check(
        Describe(&registry, combine, sizeof(combine)) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().combinePageArmed,
        "Combine page did not re-arm after another operation");
    std::uint8_t initialMenu[92]{};
    ActionPacket(5067, -1, initialMenu);
    Check(
        Describe(
            &registry,
            initialMenu,
            sizeof(initialMenu)) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().combinePageArmed,
        "initial menu did not cancel abandoned Combine page state");
}

void CheckCharacterScopedCombinePage() {
    SecurePendingOperationRegistry registry;
    Establish(&registry, "multi", 101);
    std::uint8_t combine[92]{};
    ActionPacket(5209, 9, combine);
    Check(
        Describe(&registry, combine, sizeof(combine)) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().combinePageArmed,
        "Combine page setup failed");
    Check(
        registry.SetCharacter(202) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().combinePageArmed,
        "character switch inherited Combine page state");

    Check(
        Describe(&registry, combine, sizeof(combine)) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().combinePageArmed,
        "second-character Combine page setup failed");
    Establish(&registry, "other-account", 303);
    Check(
        !registry.Snapshot().combinePageArmed,
        "principal switch inherited Combine page state");
}

void CheckDelayedCombineResultIsolation() {
    SecurePendingOperationRegistry registry;
    Establish(&registry);
    std::uint8_t combine[92]{};
    std::uint8_t selection[16]{};
    std::uint8_t initialMenu[92]{};
    ActionPacket(5067, 9, combine);
    ActionPacket(5067, -1, initialMenu);
    SelectionPacket(2, true, selection);
    LegacyPacketDescriptor oldOperation{};
    Check(
        Describe(&registry, combine, sizeof(combine)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success &&
            Describe(
                &registry,
                combine,
                sizeof(combine),
                &oldOperation) ==
                SecureOperationRegistryResult::Success &&
            oldOperation.hasOperation,
        "old Combine operation setup failed");

    SelectionPacket(3, true, selection);
    Check(
        Describe(
            &registry,
            initialMenu,
            sizeof(initialMenu)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, combine, sizeof(combine)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success,
        "new Combine page setup failed");

    const auto oldResult = ResultFor(
        oldOperation,
        SecureLegacyCommandFamily::CombineGemPieces);
    Check(
        registry.Resolve(oldResult) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().combinePageArmed &&
            registry.Snapshot().hasSelection &&
            registry.Snapshot().selectedBagSlot == 3,
        "delayed old result cleared a newer Combine page");

    LegacyPacketDescriptor newOperation{};
    Check(
        Describe(
            &registry,
            combine,
            sizeof(combine),
            &newOperation) ==
                SecureOperationRegistryResult::Success &&
            newOperation.hasOperation &&
            std::memcmp(
                oldOperation.operation.operationId,
                newOperation.operation.operationId,
                16) != 0,
        "new Combine page did not retain independent identity");
}

void CheckResultFamiliesRoundTrip() {
    for (const auto family : {
             SecureLegacyCommandFamily::TransformCrystal,
             SecureLegacyCommandFamily::CombineGemPieces}) {
        SecureLegacyCommandResult result{};
        result.disposition =
            SecureLegacyCommandDisposition::Replayed;
        result.commandFamily = family;
        result.inventoryRevision = 55;
        result.operationId[0] = 1;
        std::uint8_t encoded[32]{};
        SecureLegacyCommandResult decoded{};
        Check(
            TryEncodeSecureLegacyCommandResult(
                result,
                encoded,
                sizeof(encoded)) &&
                TryDecodeSecureLegacyCommandResult(
                    encoded,
                    sizeof(encoded),
                    &decoded) &&
                decoded.commandFamily == family &&
                decoded.inventoryRevision == 55,
            "Transform/Combine result family did not round trip");
    }
}

} // namespace

int RunSecureGearMentorCommandIdentityTests() {
    Failures = 0;
    CheckExactPacketClassification();
    CheckTransformIdentityAndResults();
    CheckFamilyScopedSameSlotIdentity();
    CheckCombineNavigationAndConfirmation();
    CheckCharacterScopedCombinePage();
    CheckDelayedCombineResultIsolation();
    CheckResultFamiliesRoundTrip();
    return Failures;
}
