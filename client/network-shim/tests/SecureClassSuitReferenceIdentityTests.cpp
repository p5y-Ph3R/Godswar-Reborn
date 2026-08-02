#include "SecureClassSuitReferenceIdentityTests.h"

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

void BuildClassSuitPacket(
    std::uint8_t* packet,
    LegacyClassSuitAction action,
    int gearReference,
    int materialReference,
    int thirdItemReference = -1) {
    std::memset(packet, 0xFF, LegacyClassSuitActionPacketBytes);
    Write16(packet, LegacyClassSuitActionPacketBytes);
    Write16(packet + 2, LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, LegacySpartaClassSuitNpc);
    Write32(packet + 8, LegacyClassSuitDialog);
    Write32(packet + 12, LegacyClassSuitDialog);
    Write32(packet + 16, static_cast<std::uint32_t>(action));
    Write32(
        packet + 20 + LegacyClassSuitScratchArgument * 4,
        0);
    if (gearReference >= 0) {
        Write32(
            packet + 20 + LegacyClassSuitGearArgument * 4,
            static_cast<std::uint32_t>(gearReference));
    }
    if (materialReference >= 0) {
        Write32(
            packet + 20 + LegacyClassSuitInsigniaArgument * 4,
            static_cast<std::uint32_t>(materialReference));
    }
    if (thirdItemReference >= 0) {
        Write32(
            packet + 20 + LegacyClassSuitThirdItemArgument * 4,
            static_cast<std::uint32_t>(thirdItemReference));
    }
}

bool Establish(SecurePendingOperationRegistry* registry) {
    constexpr std::size_t LoginBytes =
        4 + SecurePrincipalFingerprintBytes;
    std::uint8_t login[LoginBytes]{};
    Write16(login, static_cast<std::uint16_t>(LoginBytes));
    Write16(login + 2, LegacyLoginGameServerOpcode);
    for (std::size_t index = 0;
         index < SecurePrincipalFingerprintBytes;
         ++index) {
        login[4 + index] = static_cast<std::uint8_t>(20 + index);
    }
    LegacyPacketDescriptor descriptor{};
    return registry != nullptr &&
        registry->DescribePacket(login, sizeof(login), &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
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

bool StageSelection(
    SecurePendingOperationRegistry* registry,
    int bagSlot,
    bool selected) {
    std::uint8_t packet[16]{};
    Write16(packet, sizeof(packet));
    Write16(packet + 2, LegacyGearSelectionOpcode);
    Write32(
        packet + 4,
        static_cast<std::uint32_t>(bagSlot / 24));
    Write32(
        packet + 8,
        static_cast<std::uint32_t>(bagSlot % 24));
    packet[12] = selected ? 1 : 0;
    LegacyPacketDescriptor descriptor{};
    return registry != nullptr &&
        registry->DescribePacket(
            packet, sizeof(packet), &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation;
}

bool ResolveExchange(
    SecurePendingOperationRegistry* registry,
    const LegacyPacketDescriptor& descriptor) {
    if (registry == nullptr || !descriptor.hasOperation) {
        return false;
    }
    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily =
        SecureLegacyCommandFamily::ClassSuitExchangeTierI;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return registry->Resolve(result) ==
        SecureOperationRegistryResult::Success;
}

bool SnapshotHasSlots(
    SecurePendingOperationRegistry* registry,
    int first,
    int second) {
    if (registry == nullptr) {
        return false;
    }
    const auto snapshot = registry->Snapshot();
    return snapshot.hasSelection &&
        snapshot.selectionCount == 2 &&
        snapshot.selectedBagSlots[0] == first &&
        snapshot.selectedBagSlots[1] == second;
}

void CheckDirectSlots(Checks* checks) {
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    LegacyClassSuitCommand command{};
    BuildClassSuitPacket(
        packet, LegacyClassSuitAction::ExchangeTierI, 7, 3);
    checks->Require(
        ClassifyLegacyClassSuitPacket(
            packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::Commit &&
        command.gearReference == 7 &&
        command.secondaryBagSlot == 3 &&
        command.tertiaryBagSlot == -1,
        "Live direct-slot Class Suit frame did not parse");

    BuildClassSuitPacket(
        packet, LegacyClassSuitAction::AddAttribute, 7, 3, 4);
    checks->Require(
        ClassifyLegacyClassSuitPacket(
            packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::Commit &&
        command.gearReference == 7 &&
        command.secondaryBagSlot == 3 &&
        command.tertiaryBagSlot == 4,
        "Direct-slot Class Suit third item did not parse");
}

void CheckEquivalentIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t direct[LegacyClassSuitActionPacketBytes]{};
    std::uint8_t encoded[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitPacket(
        direct, LegacyClassSuitAction::ExchangeTierI, 7, 3);
    BuildClassSuitPacket(
        encoded, LegacyClassSuitAction::ExchangeTierI, 107, 103);
    LegacyPacketDescriptor directDescriptor{};
    LegacyPacketDescriptor encodedDescriptor{};
    checks->Require(
        Establish(&registry) &&
        registry.DescribePacket(
            direct, sizeof(direct), &directDescriptor) ==
                SecureOperationRegistryResult::Success &&
        registry.DescribePacket(
            encoded, sizeof(encoded), &encodedDescriptor) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(directDescriptor, encodedDescriptor) &&
        registry.Snapshot().pending == 1,
        "Direct and encoded Class Suit refs did not share one UUID");
}

void CheckEquippedWeaponIdentity(Checks* checks) {
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    LegacyClassSuitCommand command{};
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        LegacyClassSuitEquippedWeaponReference,
        103);
    checks->Require(
        ClassifyLegacyClassSuitPacket(
            packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::Commit &&
        command.gearReference ==
            LegacyClassSuitEquippedWeaponReference &&
        command.secondaryBagSlot == 3,
        "Equipped weapon did not preserve its Class Suit identity");

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
        "Equipped-weapon conversion did not receive a UUID");

    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::AddAttribute,
        LegacyClassSuitEquippedWeaponReference,
        103,
        104);
    checks->Require(
        ClassifyLegacyClassSuitPacket(
            packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::InvalidMutation,
        "Equipped weapon escaped the bag-only attribute workflow");
}

void CheckSelectionCleanup(Checks* checks) {
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitPacket(
        packet, LegacyClassSuitAction::ExchangeTierI, 7, 3);

    Hooks exactHooks{};
    SecurePendingOperationRegistry exact(
        &exactHooks, Random, &exactHooks, Clock);
    LegacyPacketDescriptor exactOperation{};
    checks->Require(
        Establish(&exact) &&
        StageSelection(&exact, 7, true) &&
        StageSelection(&exact, 3, true) &&
        exact.DescribePacket(
            packet, sizeof(packet), &exactOperation) ==
                SecureOperationRegistryResult::Success &&
        ResolveExchange(&exact, exactOperation) &&
        !exact.Snapshot().hasSelection,
        "Settled Class Suit operation did not clear exact staging");

    Hooks clearedHooks{};
    SecurePendingOperationRegistry cleared(
        &clearedHooks, Random, &clearedHooks, Clock);
    LegacyPacketDescriptor clearedOperation{};
    checks->Require(
        Establish(&cleared) &&
        StageSelection(&cleared, 7, true) &&
        StageSelection(&cleared, 3, true) &&
        StageSelection(&cleared, 7, false) &&
        StageSelection(&cleared, 3, false) &&
        SnapshotHasSlots(&cleared, 7, 3) &&
        cleared.DescribePacket(
            packet, sizeof(packet), &clearedOperation) ==
                SecureOperationRegistryResult::Success &&
        ResolveExchange(&cleared, clearedOperation) &&
        !cleared.Snapshot().hasSelection,
        "Class Suit did not clear its exact native clear snapshot");

    Hooks mismatchHooks{};
    SecurePendingOperationRegistry mismatch(
        &mismatchHooks, Random, &mismatchHooks, Clock);
    LegacyPacketDescriptor mismatchOperation{};
    checks->Require(
        Establish(&mismatch) &&
        StageSelection(&mismatch, 8, true) &&
        StageSelection(&mismatch, 3, true) &&
        mismatch.DescribePacket(
            packet, sizeof(packet), &mismatchOperation) ==
                SecureOperationRegistryResult::Success &&
        mismatchOperation.hasOperation &&
        ResolveExchange(&mismatch, mismatchOperation) &&
        SnapshotHasSlots(&mismatch, 8, 3),
        "Class Suit blocked or cleared mismatched staged selections");

    Hooks equippedHooks{};
    SecurePendingOperationRegistry equipped(
        &equippedHooks, Random, &equippedHooks, Clock);
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        LegacyClassSuitEquippedWeaponReference,
        103);
    LegacyPacketDescriptor equippedOperation{};
    checks->Require(
        Establish(&equipped) &&
        StageSelection(&equipped, 3, true) &&
        equipped.DescribePacket(
            packet, sizeof(packet), &equippedOperation) ==
                SecureOperationRegistryResult::Success &&
        ResolveExchange(&equipped, equippedOperation) &&
        !equipped.Snapshot().hasSelection,
        "Equipped Class Suit identity did not omit 205 during cleanup");
}

void CheckBoundsAndCollisions(Checks* checks) {
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    LegacyClassSuitCommand command{};
    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        LegacyClassSuitBagSlotMinimum,
        LegacyClassSuitBagSlotMaximum);
    checks->Require(
        ClassifyLegacyClassSuitPacket(
            packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::Commit &&
        command.gearReference == LegacyClassSuitBagSlotMinimum &&
        command.secondaryBagSlot == LegacyClassSuitBagSlotMaximum,
        "Direct Class Suit slot bounds were rejected");

    BuildClassSuitPacket(
        packet,
        LegacyClassSuitAction::ExchangeTierI,
        LegacyClassSuitBagReferenceMaximum,
        LegacyClassSuitBagReferenceMinimum);
    checks->Require(
        ClassifyLegacyClassSuitPacket(
            packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::Commit &&
        command.gearReference == LegacyClassSuitBagSlotMaximum &&
        command.secondaryBagSlot == LegacyClassSuitBagSlotMinimum,
        "Encoded Class Suit slot bounds were rejected");

    const int invalidGearReferences[]{96, 99, 196, 204, 206};
    for (const int invalidReference : invalidGearReferences) {
        BuildClassSuitPacket(
            packet,
            LegacyClassSuitAction::ExchangeTierI,
            invalidReference,
            3);
        checks->Require(
            ClassifyLegacyClassSuitPacket(
                packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::InvalidMutation,
            "Out-of-range Class Suit gear reference was accepted");
    }

    const int invalidMaterials[]{96, 99, 196, 205};
    for (const int invalidReference : invalidMaterials) {
        BuildClassSuitPacket(
            packet,
            LegacyClassSuitAction::ExchangeTierI,
            7,
            invalidReference);
        checks->Require(
            ClassifyLegacyClassSuitPacket(
                packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::InvalidMutation,
            "Out-of-range Class Suit material reference was accepted");
    }

    struct Collision final {
        LegacyClassSuitAction action;
        int gear;
        int material;
        int third;
    };
    const Collision collisions[]{
        {LegacyClassSuitAction::ExchangeTierI, 7, 107, -1},
        {LegacyClassSuitAction::ExchangeTierI, 107, 7, -1},
        {LegacyClassSuitAction::AddAttribute, 7, 3, 103},
        {LegacyClassSuitAction::AddAttribute, 7, 107, 3},
    };
    for (const auto& collision : collisions) {
        BuildClassSuitPacket(
            packet,
            collision.action,
            collision.gear,
            collision.material,
            collision.third);
        checks->Require(
            ClassifyLegacyClassSuitPacket(
                packet, sizeof(packet), &command) ==
                LegacyClassSuitPacketKind::InvalidMutation,
            "Class Suit accepted a collision after normalization");
    }
}

} // namespace

int RunSecureClassSuitReferenceIdentityTests() {
    Checks checks{};
    CheckDirectSlots(&checks);
    CheckEquivalentIdentity(&checks);
    CheckEquippedWeaponIdentity(&checks);
    CheckSelectionCleanup(&checks);
    CheckBoundsAndCollisions(&checks);
    return checks.failures;
}
