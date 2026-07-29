#include "SecureGearMentorDecomposeIdentityTests.h"

#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

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
    std::int32_t action,
    std::uint8_t* packet) {
    std::memset(packet, 0xCD, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, npcId);
    Write32(packet + 8, 4);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(action));
}

struct Hooks final {
    std::uint64_t now = 10'000;
    std::uint8_t randomSeed = 1;
};

bool Random(
    void* contextValue,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* context =
        static_cast<Hooks*>(contextValue);
    if (context == nullptr ||
        destination == nullptr ||
        destinationBytes != 16) {
        return false;
    }
    auto* bytes = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0; index < 16; ++index) {
        bytes[index] =
            static_cast<std::uint8_t>(
                context->randomSeed + index);
    }
    ++context->randomSeed;
    return true;
}

bool Clock(
    void* contextValue,
    std::uint64_t* now) noexcept {
    auto* context =
        static_cast<Hooks*>(contextValue);
    if (context == nullptr || now == nullptr) {
        return false;
    }
    *now = context->now;
    return true;
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
        "Decompose identity setup failed");
}

bool Stage(
    SecurePendingOperationRegistry* registry,
    const int* slots,
    std::size_t count,
    bool clear) {
    std::uint8_t packet[16]{};
    for (std::size_t index = 0; index < count; ++index) {
        SelectionPacket(slots[index], true, packet);
        if (Describe(registry, packet, sizeof(packet)) !=
            SecureOperationRegistryResult::Success) {
            return false;
        }
    }
    if (clear) {
        for (std::size_t index = 0; index < count; ++index) {
            SelectionPacket(slots[index], false, packet);
            if (Describe(registry, packet, sizeof(packet)) !=
                SecureOperationRegistryResult::Success) {
                return false;
            }
        }
    }
    return true;
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

bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return std::memcmp(
        first.operation.operationId,
        second.operation.operationId,
        16) == 0;
}

void CheckExactActionClassification() {
    std::uint8_t packet[92]{};
    ActionPacket(
        5067,
        static_cast<std::int32_t>(
            LegacyGearMentorAction::DecomposeGear),
        packet);
    LegacyGearMentorAction action =
        LegacyGearMentorAction::InitialMenu;
    std::uint32_t npcId = 0;
    Check(
        TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId) &&
            action ==
                LegacyGearMentorAction::DecomposeGear &&
            npcId == 5067,
        "exact Decompose final action was not classified");

    Write32(packet + 8, 5);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId),
        "wrong-dialog Decompose packet was classified");
    Write32(packet + 8, 4);
    Write32(packet + 4, 9999);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet),
            &action,
            &npcId),
        "unknown-NPC Decompose packet was classified");
    Write32(packet + 4, 5067);
    Check(
        !TryReadLegacyGearMentorAction(
            packet,
            sizeof(packet) - 1,
            &action,
            &npcId),
        "short Decompose packet was classified");
}

void CheckOneToThreeSlotClearBursts() {
    for (std::size_t count = 1; count <= 3; ++count) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        Establish(&registry);
        const int slots[] = {70, 3, 48};
        Check(
            Stage(&registry, slots, count, true),
            "stock Decompose select/clear burst failed");
        const auto staged = registry.Snapshot();
        bool exact =
            staged.hasSelection &&
            staged.selectionCount == count;
        for (std::size_t index = 0;
             exact && index < count;
             ++index) {
            exact =
                staged.selectedBagSlots[index] ==
                slots[index];
        }
        Check(
            exact,
            "Decompose clear burst lost ordered selection");

        std::uint8_t final[92]{};
        ActionPacket(5067, 1, final);
        LegacyPacketDescriptor operation{};
        LegacyPacketDescriptor immediateRetry{};
        Check(
            Describe(
                &registry,
                final,
                sizeof(final),
                &operation) ==
                    SecureOperationRegistryResult::Success &&
                operation.hasOperation &&
            Describe(
                &registry,
                final,
                sizeof(final),
                &immediateRetry) ==
                    SecureOperationRegistryResult::Success &&
                SameOperation(operation, immediateRetry),
            "Decompose immediate retry changed its UUID");

        const auto result = ResultFor(
            operation,
            SecureLegacyCommandFamily::DecomposeGear);
        Check(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success &&
                registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success &&
                registry.Snapshot().pending == 0 &&
                registry.Snapshot().resolved == 1,
            "Decompose result/tombstone did not resolve");
    }
}

void CheckCanonicalOrderingAndReconnectReuse() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry, "ordered", 707);
    std::uint8_t final[92]{};
    std::uint8_t menu[92]{};
    ActionPacket(5067, 1, final);
    ActionPacket(5067, -1, menu);

    const int firstOrder[] = {8, 33, 71};
    LegacyPacketDescriptor first{};
    Check(
        Stage(&registry, firstOrder, 3, true) &&
            Describe(
                &registry,
                final,
                sizeof(final),
                &first) ==
                SecureOperationRegistryResult::Success,
        "first ordered Decompose identity failed");

    const int secondOrder[] = {33, 8, 71};
    LegacyPacketDescriptor reordered{};
    Check(
        Describe(&registry, menu, sizeof(menu)) ==
                SecureOperationRegistryResult::Success &&
            Stage(&registry, secondOrder, 3, true) &&
            Describe(
                &registry,
                final,
                sizeof(final),
                &reordered) ==
                SecureOperationRegistryResult::Success &&
            !SameOperation(first, reordered),
        "ordered Decompose key treated a permutation as equal");

    std::uint8_t login[36]{};
    LoginPacket("ordered", login);
    LegacyPacketDescriptor retry{};
    Check(
        Describe(&registry, login, sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            registry.SetCharacter(707) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, menu, sizeof(menu)) ==
                SecureOperationRegistryResult::Success &&
            Stage(&registry, firstOrder, 3, true) &&
            Describe(
                &registry,
                final,
                sizeof(final),
                &retry) ==
                SecureOperationRegistryResult::Success &&
            SameOperation(first, retry),
        "same principal/character/NPC/order reconnect changed UUID");

    ActionPacket(5209, 1, final);
    LegacyPacketDescriptor otherNpc{};
    Check(
        Describe(&registry, menu, sizeof(menu)) ==
                SecureOperationRegistryResult::Success &&
            Stage(&registry, firstOrder, 3, false) &&
            Describe(
                &registry,
                final,
                sizeof(final),
                &otherNpc) ==
                SecureOperationRegistryResult::Success &&
            !SameOperation(first, otherNpc),
        "Decompose identity omitted NPC from canonical key");
}

void CheckPartialClearFailsClosed() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry, "clear-safety", 808);
    const int slots[] = {2, 25, 76};
    std::uint8_t selection[16]{};
    std::uint8_t final[92]{};
    std::uint8_t menu[92]{};
    ActionPacket(5067, 1, final);
    ActionPacket(5067, -1, menu);

    Check(
        Stage(&registry, slots, 3, false),
        "partial-clear setup failed");
    SelectionPacket(slots[0], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, final, sizeof(final)) ==
                SecureOperationRegistryResult::NoSelection,
        "partial clear became a shorter Decompose identity");

    Check(
        Describe(&registry, menu, sizeof(menu)) ==
                SecureOperationRegistryResult::Success &&
            Stage(&registry, slots, 3, false),
        "out-of-order clear setup failed");
    SelectionPacket(slots[1], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
                SecureOperationRegistryResult::Success &&
            Describe(&registry, final, sizeof(final)) ==
                SecureOperationRegistryResult::NoSelection,
        "out-of-order clear authorized Decompose");

    Check(
        Describe(&registry, menu, sizeof(menu)) ==
                SecureOperationRegistryResult::Success &&
            Stage(&registry, slots, 3, false),
        "staggered clear-burst setup failed");
    SelectionPacket(slots[0], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "first staggered clear failed");
    hooks.now += 400;
    SelectionPacket(slots[1], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "second staggered clear failed");
    hooks.now += 400;
    SelectionPacket(slots[2], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "final staggered clear failed");
    hooks.now += 300;
    LegacyPacketDescriptor withinFinalClearWindow{};
    Check(
        Describe(
            &registry,
            final,
            sizeof(final),
            &withinFinalClearWindow) ==
                SecureOperationRegistryResult::Success &&
            withinFinalClearWindow.hasOperation,
        "final-clear window inherited first-clear expiry");
    hooks.now += 700;
    Check(
        Describe(&registry, final, sizeof(final)) ==
            SecureOperationRegistryResult::NoSelection,
        "Decompose snapshot survived one second after final clear");
}

void CheckCombineAndTerminalIsolation() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry, "isolation", 909);
    std::uint8_t action[92]{};
    const int decomposeSlot[] = {11};
    ActionPacket(5067, 1, action);
    LegacyPacketDescriptor decompose{};
    Check(
        Stage(&registry, decomposeSlot, 1, true) &&
            Describe(
                &registry,
                action,
                sizeof(action),
                &decompose) ==
                SecureOperationRegistryResult::Success,
        "Decompose isolation identity failed");

    ActionPacket(5067, 9, action);
    Check(
        Describe(&registry, action, sizeof(action)) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().combinePageArmed,
        "Combine page did not arm after Decompose");
    const int combineSlot[] = {42};
    LegacyPacketDescriptor combine{};
    Check(
        Stage(&registry, combineSlot, 1, true) &&
            Describe(
                &registry,
                action,
                sizeof(action),
                &combine) ==
                SecureOperationRegistryResult::Success &&
            combine.hasOperation,
        "Combine confirmation failed after Decompose");

    auto wrongFamily = ResultFor(
        decompose,
        SecureLegacyCommandFamily::CombineGemPieces);
    Check(
        registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict &&
            registry.Snapshot().pending == 2,
        "wrong-family result consumed Decompose identity");

    const auto exact = ResultFor(
        decompose,
        SecureLegacyCommandFamily::DecomposeGear);
    Check(
        registry.Resolve(exact) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 1 &&
            registry.Snapshot().combinePageArmed &&
            registry.Snapshot().selectionCount == 1 &&
            registry.Snapshot().selectedBagSlot == 42,
        "Decompose result cleared newer Combine state");

    const auto combineResult = ResultFor(
        combine,
        SecureLegacyCommandFamily::CombineGemPieces);
    Check(
        registry.Resolve(combineResult) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().combinePageArmed,
        "Combine result no longer cleared its exact page");
}

void CheckFamilyNineResultRoundTrip() {
    SecureLegacyCommandResult result{};
    result.disposition =
        SecureLegacyCommandDisposition::Replayed;
    result.commandFamily =
        SecureLegacyCommandFamily::DecomposeGear;
    result.resultCode = 1005;
    result.inventoryRevision = 99;
    result.operationId[0] = 0xA5;
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
            decoded.commandFamily ==
                SecureLegacyCommandFamily::DecomposeGear &&
            decoded.resultCode == 1005 &&
            decoded.inventoryRevision == 99,
        "Decompose family-9 result did not round trip");
}

} // namespace

int RunSecureGearMentorDecomposeIdentityTests() {
    Failures = 0;
    CheckExactActionClassification();
    CheckOneToThreeSlotClearBursts();
    CheckCanonicalOrderingAndReconnectReuse();
    CheckPartialClearFailsClosed();
    CheckCombineAndTerminalIsolation();
    CheckFamilyNineResultRoundTrip();
    return Failures;
}
