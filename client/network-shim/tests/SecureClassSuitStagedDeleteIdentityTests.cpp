#include "SecureClassSuitStagedDeleteIdentityTests.h"

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
    std::uint64_t now = 120'000;
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
    for (std::size_t index = 0; index < destinationBytes; ++index) {
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

void BuildClassSuitNavigation(
    std::uint8_t* packet,
    LegacyClassSuitAction action,
    std::uint32_t npcId = LegacySpartaClassSuitNpc) {
    std::memset(packet, 0xFF, LegacyClassSuitActionPacketBytes);
    Write16(packet, LegacyClassSuitActionPacketBytes);
    Write16(packet + 2, LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyClassSuitDialog);
    Write32(packet + 12, LegacyClassSuitDialog);
    Write32(packet + 16, static_cast<std::uint32_t>(action));
    Write32(
        packet + 20 + LegacyClassSuitScratchArgument * 4,
        0);
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
        login[4 + index] = static_cast<std::uint8_t>(30 + index);
    }
    LegacyPacketDescriptor descriptor{};
    return registry != nullptr &&
        registry->DescribePacket(login, sizeof(login), &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry->SetCharacter(810) ==
            SecureOperationRegistryResult::Success;
}

bool Describe(
    SecurePendingOperationRegistry* registry,
    const std::uint8_t* packet,
    LegacyPacketDescriptor* descriptor) {
    if (registry == nullptr || packet == nullptr || descriptor == nullptr) {
        return false;
    }
    *descriptor = LegacyPacketDescriptor{};
    return registry->DescribePacket(
               packet,
               LegacyClassSuitActionPacketBytes,
               descriptor) == SecureOperationRegistryResult::Success;
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

bool StageAndClear(
    SecurePendingOperationRegistry* registry,
    int gearSlot,
    int waterSlot,
    int selectorStoneSlot) {
    return StageSelection(registry, gearSlot, true) &&
        StageSelection(registry, waterSlot, true) &&
        StageSelection(registry, selectorStoneSlot, true) &&
        StageSelection(registry, gearSlot, false) &&
        StageSelection(registry, waterSlot, false) &&
        StageSelection(registry, selectorStoneSlot, false);
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

bool ResolveDelete(
    SecurePendingOperationRegistry* registry,
    const LegacyPacketDescriptor& descriptor) {
    if (registry == nullptr || !descriptor.hasOperation) {
        return false;
    }
    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily =
        SecureLegacyCommandFamily::ClassSuitDeleteAttribute;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return registry->Resolve(result) ==
        SecureOperationRegistryResult::Success;
}

void CheckStagedDeleteAndRetry(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitNavigation(
        packet, LegacyClassSuitAction::DeleteAttribute);

    LegacyPacketDescriptor page{};
    LegacyPacketDescriptor commit{};
    LegacyPacketDescriptor retry{};
    LegacyPacketDescriptor afterResult{};
    checks->Require(
        Establish(&registry) &&
        Describe(&registry, packet, &page) &&
        !page.hasOperation,
        "first empty Class Suit Delete action was not page navigation");
    checks->Require(
        StageAndClear(&registry, 16, 7, 8) &&
        Describe(&registry, packet, &commit) &&
        commit.hasOperation &&
        registry.Snapshot().pending == 1,
        "ordered three-slot Delete clear did not receive an operation UUID");
    checks->Require(
        Describe(&registry, packet, &retry) &&
        SameOperation(commit, retry) &&
        registry.Snapshot().pending == 1,
        "empty-reference Class Suit Delete retry did not reuse its UUID");
    checks->Require(
        ResolveDelete(&registry, commit) &&
        registry.Snapshot().pending == 0 &&
        Describe(&registry, packet, &afterResult) &&
        !afterResult.hasOperation,
        "settled Delete did not clear its page and staged selection");
}

void CheckClearFailures(Checks* checks) {
    std::uint8_t packet[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitNavigation(
        packet, LegacyClassSuitAction::DeleteAttribute);

    Hooks partialHooks{};
    SecurePendingOperationRegistry partial(
        &partialHooks, Random, &partialHooks, Clock);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        Establish(&partial) &&
        Describe(&partial, packet, &descriptor) &&
        StageSelection(&partial, 16, true) &&
        StageSelection(&partial, 7, true) &&
        StageSelection(&partial, 8, true) &&
        StageSelection(&partial, 16, false) &&
        Describe(&partial, packet, &descriptor) &&
        !descriptor.hasOperation,
        "partial Class Suit Delete clear received an operation UUID");

    Hooks reorderedHooks{};
    SecurePendingOperationRegistry reordered(
        &reorderedHooks, Random, &reorderedHooks, Clock);
    checks->Require(
        Establish(&reordered) &&
        Describe(&reordered, packet, &descriptor) &&
        StageSelection(&reordered, 16, true) &&
        StageSelection(&reordered, 7, true) &&
        StageSelection(&reordered, 8, true) &&
        StageSelection(&reordered, 7, false) &&
        StageSelection(&reordered, 16, false) &&
        StageSelection(&reordered, 8, false) &&
        Describe(&reordered, packet, &descriptor) &&
        !descriptor.hasOperation,
        "reordered Class Suit Delete clear received an operation UUID");

    Hooks expiredHooks{};
    SecurePendingOperationRegistry expired(
        &expiredHooks, Random, &expiredHooks, Clock);
    checks->Require(
        Establish(&expired) &&
        Describe(&expired, packet, &descriptor) &&
        StageAndClear(&expired, 16, 7, 8),
        "expired Class Suit Delete fixture setup failed");
    expiredHooks.now +=
        SecureSelectionClearCorrelationLifetimeMilliseconds;
    checks->Require(
        Describe(&expired, packet, &descriptor) &&
        !descriptor.hasOperation,
        "expired Class Suit Delete clear received an operation UUID");

    Hooks missingSelectorHooks{};
    SecurePendingOperationRegistry missingSelector(
        &missingSelectorHooks,
        Random,
        &missingSelectorHooks,
        Clock);
    checks->Require(
        Establish(&missingSelector) &&
        Describe(&missingSelector, packet, &descriptor) &&
        StageSelection(&missingSelector, 16, true) &&
        StageSelection(&missingSelector, 7, true) &&
        StageSelection(&missingSelector, 16, false) &&
        StageSelection(&missingSelector, 7, false) &&
        Describe(&missingSelector, packet, &descriptor) &&
        !descriptor.hasOperation,
        "two-slot Class Suit Delete clear received an operation UUID without a selector stone");
}

void CheckNpcAndActionIsolation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t sparta[LegacyClassSuitActionPacketBytes]{};
    std::uint8_t athens[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitNavigation(
        sparta, LegacyClassSuitAction::DeleteAttribute);
    BuildClassSuitNavigation(
        athens,
        LegacyClassSuitAction::DeleteAttribute,
        LegacyAthensClassSuitNpc);

    LegacyPacketDescriptor descriptor{};
    LegacyPacketDescriptor athensCommit{};
    LegacyPacketDescriptor athensRetry{};
    checks->Require(
        Establish(&registry) &&
        Describe(&registry, sparta, &descriptor) &&
        StageAndClear(&registry, 16, 7, 8) &&
        Describe(&registry, athens, &descriptor) &&
        !descriptor.hasOperation,
        "a different Class Suit NPC inherited Sparta Delete selections");
    checks->Require(
        StageAndClear(&registry, 18, 9, 10) &&
        Describe(&registry, athens, &athensCommit) &&
        athensCommit.hasOperation,
        "Athens Delete did not establish its own isolated identity");

    std::uint8_t addPage[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitNavigation(
        addPage, LegacyClassSuitAction::AddAttribute);
    checks->Require(
        Describe(&registry, addPage, &descriptor) &&
        !descriptor.hasOperation &&
        Describe(&registry, sparta, &descriptor) &&
        !descriptor.hasOperation,
        "Class Suit Add/Delete pages shared cleared selection state");

    // Re-establish the Athens page and prove a delayed result for the older
    // page cannot clear the newer page generation.
    checks->Require(
        Describe(&registry, athens, &descriptor) &&
        StageAndClear(&registry, 18, 9, 10) &&
        Describe(&registry, athens, &athensRetry) &&
        SameOperation(athensCommit, athensRetry) &&
        ResolveDelete(&registry, athensCommit),
        "Class Suit NPC/action isolation fixture did not settle cleanly");
}

void CheckInitialMenuResetsDelete(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t deletePage[LegacyClassSuitActionPacketBytes]{};
    std::uint8_t initialMenu[LegacyClassSuitActionPacketBytes]{};
    BuildClassSuitNavigation(
        deletePage, LegacyClassSuitAction::DeleteAttribute);
    BuildClassSuitNavigation(
        initialMenu, LegacyClassSuitAction::InitialMenu);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        Establish(&registry) &&
        Describe(&registry, deletePage, &descriptor) &&
        StageAndClear(&registry, 16, 7, 8) &&
        Describe(&registry, initialMenu, &descriptor) &&
        !descriptor.hasOperation &&
        Describe(&registry, deletePage, &descriptor) &&
        !descriptor.hasOperation,
        "Class Suit initial menu did not invalidate staged Delete state");
}

} // namespace

int RunSecureClassSuitStagedDeleteIdentityTests() {
    Checks checks{};
    CheckStagedDeleteAndRetry(&checks);
    CheckClearFailures(&checks);
    CheckNpcAndActionIsolation(&checks);
    CheckInitialMenuResetsDelete(&checks);
    return checks.failures;
}
