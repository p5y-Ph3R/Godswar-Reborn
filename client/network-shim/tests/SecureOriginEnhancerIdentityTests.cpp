#include "SecureOriginEnhancerIdentityTests.h"

#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::LegacyGearMentorAction;
using godswar::network::LegacyPacketDescriptor;
using godswar::network::SecureOperationRegistryResult;
using godswar::network::SecurePendingOperationRegistry;
using godswar::network::TryReadLegacyOriginEnhancerNavigation;

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
    packet[13] = 0x57;
    packet[14] = 0x75;
    packet[15] = 0x01;
}

void PhysicalActionPacket(
    std::int32_t action,
    std::uint8_t* packet) {
    std::memset(packet, 0xCD, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, 5067);
    Write32(packet + 8, 4);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(action));
}

void OriginActionPacket(
    std::uint32_t npcId,
    std::int32_t action,
    int gearReference,
    int catalystReference,
    int stoneReference,
    std::uint8_t* packet) {
    std::memset(packet, 0xFF, 92);
    Header(packet, 92, 10069);
    Write32(packet + 4, npcId);
    Write32(packet + 8, 118);
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(action));
    if (gearReference >= 0) {
        Write32(
            packet + 20 + 6 * 4,
            static_cast<std::uint32_t>(gearReference));
    }
    if (catalystReference >= 0) {
        Write32(
            packet + 20 + 7 * 4,
            static_cast<std::uint32_t>(catalystReference));
    }
    if (stoneReference >= 0) {
        Write32(
            packet + 20 + 8 * 4,
            static_cast<std::uint32_t>(stoneReference));
    }
}

struct Hooks final {
    std::uint64_t now = 20'000;
    std::uint8_t randomSeed = 41;
};

bool Random(
    void* contextValue,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* context = static_cast<Hooks*>(contextValue);
    if (context == nullptr ||
        destination == nullptr ||
        destinationBytes != 16) {
        return false;
    }
    auto* bytes = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0; index < 16; ++index) {
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
        "Origin Enhancer identity setup failed");
}

bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return std::memcmp(
        first.operation.operationId,
        second.operation.operationId,
        sizeof(first.operation.operationId)) == 0;
}

bool SnapshotHasSlots(
    SecurePendingOperationRegistry* registry,
    const int* expected,
    std::size_t count) {
    const auto snapshot = registry->Snapshot();
    if (!snapshot.hasSelection ||
        snapshot.selectionCount != count) {
        return false;
    }
    for (std::size_t index = 0; index < count; ++index) {
        if (snapshot.selectedBagSlots[index] != expected[index]) {
            return false;
        }
    }
    return true;
}

void CheckNavigationAndMalformedPackets() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry);
    const int staged[]{1, 26, 72};
    std::uint8_t selection[16]{};
    std::uint8_t packet[92]{};

    PhysicalActionPacket(9, packet);
    Check(
        Describe(&registry, packet, sizeof(packet)) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().combinePageArmed,
        "Origin operation-navigation reset setup failed");
    for (int slot : staged) {
        SelectionPacket(slot, true, selection);
        Check(
            Describe(
                &registry,
                selection,
                sizeof(selection)) ==
                SecureOperationRegistryResult::Success,
            "Origin operation-navigation selection setup failed");
    }

    OriginActionPacket(5140, 2, -1, -1, -1, packet);
    LegacyGearMentorAction navigation =
        LegacyGearMentorAction::InitialMenu;
    std::uint32_t navigationNpc = 0;
    LegacyPacketDescriptor descriptor{};
    Check(
        TryReadLegacyOriginEnhancerNavigation(
            packet,
            sizeof(packet),
            &navigation,
            &navigationNpc) &&
            navigation ==
                LegacyGearMentorAction::EnhanceAttribute &&
            navigationNpc == 5140 &&
            Describe(
                &registry,
                packet,
                sizeof(packet),
                &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            !registry.Snapshot().combinePageArmed &&
            !registry.Snapshot().hasSelection,
        "all-unset Origin operation navigation did not reset state");

    PhysicalActionPacket(2, packet);
    descriptor = {};
    Check(
        Describe(
            &registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::NoSelection &&
            !descriptor.hasOperation,
        "Origin navigation leaked an old triplet to physical Enhance");

    PhysicalActionPacket(9, packet);
    Check(
        Describe(&registry, packet, sizeof(packet)) ==
            SecureOperationRegistryResult::Success,
        "Origin initial-navigation reset setup failed");
    for (int slot : staged) {
        SelectionPacket(slot, true, selection);
        Check(
            Describe(
                &registry,
                selection,
                sizeof(selection)) ==
                SecureOperationRegistryResult::Success,
            "Origin initial-navigation selection setup failed");
    }
    OriginActionPacket(5140, -1, -1, -1, -1, packet);
    navigation = LegacyGearMentorAction::EnhanceAttribute;
    navigationNpc = 0;
    descriptor = {};
    Check(
        TryReadLegacyOriginEnhancerNavigation(
            packet,
            sizeof(packet),
            &navigation,
            &navigationNpc) &&
            navigation ==
                LegacyGearMentorAction::InitialMenu &&
            navigationNpc == 5140 &&
            Describe(
                &registry,
                packet,
                sizeof(packet),
                &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            !registry.Snapshot().combinePageArmed &&
            !registry.Snapshot().hasSelection,
        "Origin initial navigation did not reset state");

    PhysicalActionPacket(9, packet);
    Check(
        Describe(&registry, packet, sizeof(packet)) ==
            SecureOperationRegistryResult::Success,
        "malformed Origin preservation setup failed");
    for (int slot : staged) {
        SelectionPacket(slot, true, selection);
        Check(
            Describe(
                &registry,
                selection,
                sizeof(selection)) ==
                SecureOperationRegistryResult::Success,
            "malformed Origin selection setup failed");
    }

    OriginActionPacket(5140, 2, 100, 125, 195, packet);
    Write32(packet + 20, 0);
    descriptor = {};
    Check(
        Describe(
            &registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            SnapshotHasSlots(&registry, staged, 3) &&
            registry.Snapshot().combinePageArmed,
        "Origin scratch argument was marked or mutated state");

    OriginActionPacket(5140, 2, 100, -1, 195, packet);
    descriptor = {};
    Check(
        Describe(
            &registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            SnapshotHasSlots(&registry, staged, 3) &&
            registry.Snapshot().combinePageArmed,
        "partial Origin inline triplet was marked");

    OriginActionPacket(5140, 2, 99, 125, 195, packet);
    descriptor = {};
    Check(
        Describe(
            &registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            SnapshotHasSlots(&registry, staged, 3) &&
            registry.Snapshot().combinePageArmed,
        "out-of-range Origin bag reference was marked");

    OriginActionPacket(5067, 2, 100, 125, 195, packet);
    descriptor = {};
    Check(
        Describe(
            &registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            SnapshotHasSlots(&registry, staged, 3) &&
            registry.Snapshot().combinePageArmed,
        "physical Mentor NPC was accepted as Origin Enhancer");

    OriginActionPacket(5140, 1, 100, 125, 195, packet);
    descriptor = {};
    Check(
        Describe(
            &registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            SnapshotHasSlots(&registry, staged, 3) &&
            registry.Snapshot().combinePageArmed,
        "unsupported Origin action was marked");

    OriginActionPacket(5140, 2, 100, 125, 195, packet);
    Write32(packet + 8, 4);
    descriptor = {};
    Check(
        Describe(
            &registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            !descriptor.hasOperation &&
            SnapshotHasSlots(&registry, staged, 3) &&
            registry.Snapshot().combinePageArmed,
        "wrong Origin dialog was marked");
}

LegacyPacketDescriptor DescribeOrigin(
    SecurePendingOperationRegistry* registry,
    std::uint32_t npcId,
    std::int32_t action,
    int gearReference,
    int catalystReference,
    int stoneReference) {
    std::uint8_t packet[92]{};
    OriginActionPacket(
        npcId,
        action,
        gearReference,
        catalystReference,
        stoneReference,
        packet);
    LegacyPacketDescriptor descriptor{};
    Check(
        Describe(
            registry,
            packet,
            sizeof(packet),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            descriptor.hasOperation,
        "valid Origin commit lacked an operation ID");
    return descriptor;
}

void CheckOriginIdentityIsolation() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry);

    const auto first = DescribeOrigin(
        &registry,
        5140,
        2,
        100,
        125,
        195);
    Establish(&registry);
    const auto retry = DescribeOrigin(
        &registry,
        5140,
        2,
        100,
        125,
        195);
    Check(
        SameOperation(first, retry),
        "exact Origin reconnect retry did not reuse its UUID");

    const auto reordered = DescribeOrigin(
        &registry,
        5140,
        2,
        100,
        195,
        125);
    Check(
        !SameOperation(first, reordered),
        "reordered Origin roles reused one UUID");

    const auto athens = DescribeOrigin(
        &registry,
        5282,
        2,
        100,
        125,
        195);
    Check(
        !SameOperation(first, athens),
        "Origin endpoint NPC was omitted from identity");

    const auto add = DescribeOrigin(
        &registry,
        5140,
        3,
        100,
        125,
        195);
    const auto remove = DescribeOrigin(
        &registry,
        5140,
        6,
        100,
        125,
        195);
    Check(
        !SameOperation(first, add) &&
            !SameOperation(first, remove) &&
            !SameOperation(add, remove),
        "Origin command family was omitted from identity");
}

} // namespace

int RunSecureOriginEnhancerIdentityTests() {
    Failures = 0;
    CheckNavigationAndMalformedPackets();
    CheckOriginIdentityIsolation();
    return Failures;
}
