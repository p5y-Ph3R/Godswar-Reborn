#include "SecureGearMentorAttributeIdentityTests.h"

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
using godswar::network::SecurePendingOperationRegistry;

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
    packet[14] = 0x96;
    packet[15] = 0x01;
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
        "Gear Mentor attribute identity setup failed");
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
    result.resultCode = 1010;
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
        sizeof(first.operation.operationId)) == 0;
}

void CheckEachPhysicalFamily() {
    struct Case final {
        std::int32_t action;
        SecureLegacyCommandFamily family;
    };
    const Case cases[]{
        {2, SecureLegacyCommandFamily::
                GearMentorEnhanceAttribute},
        {3, SecureLegacyCommandFamily::
                GearMentorAddAttribute},
        {6, SecureLegacyCommandFamily::
                GearMentorDeleteAttribute},
    };
    const int slots[]{0, 25, 95};

    for (const auto& test : cases) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        Establish(&registry);
        Check(
            Stage(&registry, slots, 3, true),
            "physical attribute triplet staging failed");

        std::uint8_t action[92]{};
        ActionPacket(5067, test.action, action);
        LegacyPacketDescriptor descriptor{};
        Check(
            Describe(
                &registry,
                action,
                sizeof(action),
                &descriptor) ==
                    SecureOperationRegistryResult::Success &&
                descriptor.hasOperation,
            "physical attribute action lacked an operation ID");
        Check(
            registry.Resolve(
                ResultFor(descriptor, test.family)) ==
                SecureOperationRegistryResult::Success,
            "physical attribute family did not settle");
    }
}

void CheckExactlyThreeSelectionsRequired() {
    const int slots[]{2, 27, 71};
    for (std::size_t count = 0; count < 3; ++count) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        Establish(&registry);
        Check(
            Stage(&registry, slots, count, false),
            "partial physical attribute staging failed");

        std::uint8_t action[92]{};
        ActionPacket(5067, 2, action);
        LegacyPacketDescriptor descriptor{};
        Check(
            Describe(
                &registry,
                action,
                sizeof(action),
                &descriptor) ==
                    SecureOperationRegistryResult::NoSelection &&
                !descriptor.hasOperation,
            "physical attribute action accepted fewer than three slots");
    }
}

void CheckClearBurstFailures() {
    const int slots[]{4, 29, 74};
    std::uint8_t selection[16]{};
    std::uint8_t action[92]{};
    ActionPacket(5067, 2, action);

    {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        Establish(&registry);
        Check(
            Stage(&registry, slots, 3, false),
            "partial-clear setup failed");
        for (std::size_t index = 0; index < 2; ++index) {
            SelectionPacket(slots[index], false, selection);
            Check(
                Describe(
                    &registry,
                    selection,
                    sizeof(selection)) ==
                    SecureOperationRegistryResult::Success,
                "partial clear packet failed");
        }
        LegacyPacketDescriptor descriptor{};
        Check(
            Describe(
                &registry,
                action,
                sizeof(action),
                &descriptor) ==
                    SecureOperationRegistryResult::NoSelection &&
                !descriptor.hasOperation,
            "partial clear burst produced an operation ID");
    }

    {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        Establish(&registry);
        Check(
            Stage(&registry, slots, 3, false),
            "reordered-clear setup failed");
        const int reordered[]{slots[1], slots[0], slots[2]};
        for (int slot : reordered) {
            SelectionPacket(slot, false, selection);
            Check(
                Describe(
                    &registry,
                    selection,
                    sizeof(selection)) ==
                    SecureOperationRegistryResult::Success,
                "reordered clear packet failed");
        }
        LegacyPacketDescriptor descriptor{};
        Check(
            Describe(
                &registry,
                action,
                sizeof(action),
                &descriptor) ==
                    SecureOperationRegistryResult::NoSelection &&
                !descriptor.hasOperation,
            "reordered clear burst produced an operation ID");
    }

    {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks,
            Random,
            &hooks,
            Clock);
        Establish(&registry);
        Check(
            Stage(&registry, slots, 3, true),
            "expired-clear setup failed");
        hooks.now += 1'000;
        LegacyPacketDescriptor descriptor{};
        Check(
            Describe(
                &registry,
                action,
                sizeof(action),
                &descriptor) ==
                    SecureOperationRegistryResult::NoSelection &&
                !descriptor.hasOperation,
            "expired clear burst produced an operation ID");
    }
}

void CheckFinalClearStartsFreshWindow() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry);
    const int slots[]{5, 30, 75};
    Check(
        Stage(&registry, slots, 3, false),
        "fresh-window staging failed");

    std::uint8_t selection[16]{};
    SelectionPacket(slots[0], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "fresh-window first clear failed");
    hooks.now += 450;
    SelectionPacket(slots[1], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "fresh-window second clear failed");
    hooks.now += 450;
    SelectionPacket(slots[2], false, selection);
    Check(
        Describe(&registry, selection, sizeof(selection)) ==
            SecureOperationRegistryResult::Success,
        "fresh-window final clear failed");
    hooks.now += 999;

    std::uint8_t action[92]{};
    ActionPacket(5067, 3, action);
    LegacyPacketDescriptor descriptor{};
    Check(
        Describe(
            &registry,
            action,
            sizeof(action),
            &descriptor) ==
                SecureOperationRegistryResult::Success &&
            descriptor.hasOperation,
        "final clear did not start a fresh one-second window");
}

void CheckRetryAndFamilyIsolation() {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        Random,
        &hooks,
        Clock);
    Establish(&registry);
    const int slots[]{6, 31, 76};
    std::uint8_t action[92]{};

    Check(
        Stage(&registry, slots, 3, true),
        "retry first triplet staging failed");
    ActionPacket(5067, 2, action);
    LegacyPacketDescriptor first{};
    Check(
        Describe(&registry, action, sizeof(action), &first) ==
                SecureOperationRegistryResult::Success &&
            first.hasOperation,
        "retry first operation was not created");

    Establish(&registry);
    Check(
        Stage(&registry, slots, 3, true),
        "retry reconnect triplet staging failed");
    LegacyPacketDescriptor retry{};
    Check(
        Describe(&registry, action, sizeof(action), &retry) ==
                SecureOperationRegistryResult::Success &&
            retry.hasOperation &&
            SameOperation(first, retry),
        "exact reconnect retry did not reuse its UUID");

    Check(
        Stage(&registry, slots, 3, true),
        "cross-family triplet staging failed");
    ActionPacket(5067, 3, action);
    LegacyPacketDescriptor add{};
    Check(
        Describe(&registry, action, sizeof(action), &add) ==
                SecureOperationRegistryResult::Success &&
            add.hasOperation &&
            !SameOperation(first, add),
        "Enhance and Add reused one operation UUID");

    Check(
        Stage(&registry, slots, 3, true),
        "Delete-family triplet staging failed");
    ActionPacket(5067, 6, action);
    LegacyPacketDescriptor remove{};
    Check(
        Describe(&registry, action, sizeof(action), &remove) ==
                SecureOperationRegistryResult::Success &&
            remove.hasOperation &&
            !SameOperation(first, remove) &&
            !SameOperation(add, remove),
        "Delete was not isolated from Enhance and Add");

    Establish(&registry, "test2", 606);
    Check(
        Stage(&registry, slots, 3, true),
        "character-isolation triplet staging failed");
    ActionPacket(5067, 2, action);
    LegacyPacketDescriptor otherCharacter{};
    Check(
        Describe(
            &registry,
            action,
            sizeof(action),
            &otherCharacter) ==
                SecureOperationRegistryResult::Success &&
            otherCharacter.hasOperation &&
            !SameOperation(first, otherCharacter),
        "character was omitted from attribute identity");

    Establish(&registry, "another", 505);
    Check(
        Stage(&registry, slots, 3, true),
        "principal-isolation triplet staging failed");
    LegacyPacketDescriptor otherPrincipal{};
    Check(
        Describe(
            &registry,
            action,
            sizeof(action),
            &otherPrincipal) ==
                SecureOperationRegistryResult::Success &&
            otherPrincipal.hasOperation &&
            !SameOperation(first, otherPrincipal),
        "principal was omitted from attribute identity");

    auto conflict = ResultFor(
        first,
        SecureLegacyCommandFamily::GearMentorAddAttribute);
    Check(
        registry.Resolve(conflict) ==
            SecureOperationRegistryResult::FamilyConflict,
        "wrong-family terminal result settled Enhance");
    conflict.commandFamily =
        SecureLegacyCommandFamily::
            GearMentorEnhanceAttribute;
    Check(
        registry.Resolve(conflict) ==
            SecureOperationRegistryResult::Success,
        "correct-family terminal result did not settle Enhance");
}

} // namespace

int RunSecureGearMentorAttributeIdentityTests() {
    Failures = 0;
    CheckEachPhysicalFamily();
    CheckExactlyThreeSelectionsRequired();
    CheckClearBurstFailures();
    CheckFinalClearStartsFreshWindow();
    CheckRetryAndFamilyIsolation();
    return Failures;
}
