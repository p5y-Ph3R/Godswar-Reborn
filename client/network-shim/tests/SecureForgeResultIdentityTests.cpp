#include "SecureForgeResultIdentityTests.h"

#include "SecureForgeTestSupport.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using namespace godswar::network;
namespace forge_test = godswar::network::forge_test;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

bool PrepareOperation(
    SecurePendingOperationRegistry* registry,
    int equipmentSlot,
    int primarySlot,
    LegacyPacketDescriptor* descriptor) {
    return forge_test::Stage(
               registry,
               equipmentSlot,
               LegacyForgeEquipmentDestination) &&
        forge_test::Stage(
            registry,
            primarySlot,
            LegacyForgePrimaryMaterialDestination) &&
        forge_test::Start(registry, descriptor);
}

void CheckFamilyThreeResultCodec() {
    const SecureLegacyCommandDisposition dispositions[]{
        SecureLegacyCommandDisposition::Applied,
        SecureLegacyCommandDisposition::Replayed,
        SecureLegacyCommandDisposition::Rejected,
        SecureLegacyCommandDisposition::Conflict,
    };
    for (const auto disposition : dispositions) {
        SecureLegacyCommandResult input{};
        input.disposition = disposition;
        input.commandFamily =
            SecureLegacyCommandFamily::EquipmentForge;
        input.resultCode =
            disposition ==
                SecureLegacyCommandDisposition::Applied
            ? 2U
            : 8U;
        input.inventoryRevision =
            disposition ==
                SecureLegacyCommandDisposition::Applied
            ? 91U
            : 0U;
        for (std::size_t index = 0;
             index < sizeof(input.operationId);
             ++index) {
            input.operationId[index] =
                static_cast<std::uint8_t>(index + 1);
        }

        std::uint8_t encoded[
            SecureLegacyCommandResultPayloadBytes]{};
        SecureLegacyCommandResult decoded{};
        Check(
            TryEncodeSecureLegacyCommandResult(
                input,
                encoded,
                sizeof(encoded)) &&
                TryDecodeSecureLegacyCommandResult(
                    encoded,
                    sizeof(encoded),
                    &decoded) &&
                decoded.disposition == disposition &&
                decoded.commandFamily ==
                    SecureLegacyCommandFamily::
                        EquipmentForge &&
                decoded.resultCode == input.resultCode &&
                decoded.inventoryRevision ==
                    input.inventoryRevision &&
                std::memcmp(
                    decoded.operationId,
                    input.operationId,
                    sizeof(input.operationId)) == 0,
            "Forge family-three result did not round-trip");
    }

    SecureLegacyCommandResult invalid{};
    invalid.disposition =
        SecureLegacyCommandDisposition::Applied;
    invalid.commandFamily =
        SecureLegacyCommandFamily::EquipmentForge;
    invalid.resultCode = 1;
    invalid.inventoryRevision = 0;
    invalid.operationId[0] = 1;
    std::uint8_t encoded[
        SecureLegacyCommandResultPayloadBytes]{};
    Check(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Applied Forge result encoded without an inventory revision");

    invalid.disposition =
        SecureLegacyCommandDisposition::Rejected;
    invalid.commandFamily =
        static_cast<SecureLegacyCommandFamily>(19);
    Check(
        !TryEncodeSecureLegacyCommandResult(
            invalid,
            encoded,
            sizeof(encoded)),
        "Unknown command family encoded as a Forge result");
}

void CheckTerminalSettlementAndFreshAttempt() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    LegacyPacketDescriptor first{};
    Check(
        forge_test::Establish(&registry) &&
            PrepareOperation(&registry, 10, 11, &first),
        "Forge settlement setup failed");

    auto applied = forge_test::ResultFor(first);
    Check(
        registry.Resolve(applied) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 0 &&
            registry.Snapshot().resolved == 1 &&
            !registry.Snapshot().hasForgeEquipment,
        "Applied Forge result did not settle and clear staging");
    Check(
        registry.Resolve(applied) ==
            SecureOperationRegistryResult::Success,
        "Duplicate Forge result did not match its tombstone");

    auto wrongFamily = applied;
    wrongFamily.commandFamily =
        SecureLegacyCommandFamily::MakeAttributeStone;
    Check(
        registry.Resolve(wrongFamily) ==
            SecureOperationRegistryResult::FamilyConflict,
        "Forge tombstone accepted a different command family");

    LegacyPacketDescriptor fresh{};
    Check(
        PrepareOperation(&registry, 10, 11, &fresh) &&
            !forge_test::SameOperation(first, fresh),
        "A settled Forge recipe reused its old operation UUID");
}

void CheckAllTerminalDispositions() {
    struct Case final {
        SecureLegacyCommandDisposition disposition;
        std::uint32_t resultCode;
        std::uint64_t revision;
    };
    const Case cases[]{
        {SecureLegacyCommandDisposition::Applied, 1, 101},
        {SecureLegacyCommandDisposition::Applied, 2, 102},
        {SecureLegacyCommandDisposition::Replayed, 2, 0},
        {SecureLegacyCommandDisposition::Rejected, 8, 0},
        {SecureLegacyCommandDisposition::Conflict, 0, 0},
    };

    for (std::size_t index = 0;
         index < sizeof(cases) / sizeof(cases[0]);
         ++index) {
        forge_test::Hooks hooks{};
        hooks.randomSeed =
            static_cast<std::uint8_t>(70 + index);
        SecurePendingOperationRegistry registry(
            &hooks,
            forge_test::Random,
            &hooks,
            forge_test::Clock);
        LegacyPacketDescriptor operation{};
        Check(
            forge_test::Establish(&registry) &&
                PrepareOperation(
                    &registry,
                    20,
                    21,
                    &operation),
            "Forge terminal-disposition setup failed");
        const auto& current = cases[index];
        auto result = forge_test::ResultFor(
            operation,
            current.disposition,
            current.resultCode,
            current.revision);
        Check(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::Success &&
                registry.Snapshot().pending == 0 &&
                !registry.Snapshot().hasForgeEquipment,
            "Forge terminal disposition did not settle");
    }
}

void CheckOlderResultPreservesChangedRecipe() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor changed{};
    Check(
        forge_test::Establish(&registry) &&
            PrepareOperation(&registry, 30, 31, &first) &&
            forge_test::Stage(
                &registry,
                32,
                LegacyForgeEquipmentDestination) &&
            forge_test::Start(&registry, &changed) &&
            !forge_test::SameOperation(first, changed) &&
            registry.Snapshot().pending == 2,
        "Changed Forge recipe did not create an independent UUID");

    Check(
        registry.Resolve(forge_test::ResultFor(first)) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 1 &&
            registry.Snapshot().hasForgeEquipment &&
            registry.Snapshot().forgeEquipmentBagSlot == 32,
        "Older Forge result cleared a changed active recipe");
    Check(
        registry.Resolve(forge_test::ResultFor(changed)) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 0 &&
            !registry.Snapshot().hasForgeEquipment,
        "Changed Forge result did not clear its matching recipe");
}

void CheckCancelReselectAndLostResponseRetry() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    LegacyPacketDescriptor original{};
    Check(
        forge_test::Establish(&registry) &&
            PrepareOperation(
                &registry,
                40,
                41,
                &original),
        "Forge lost-response setup failed");

    std::uint8_t cancel[LegacyForgeCancelPacketBytes]{};
    forge_test::ForgeCancelPacket(cancel);
    Check(
        forge_test::Describe(
            &registry,
            cancel,
            sizeof(cancel)) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasForgeEquipment &&
            registry.Snapshot().pending == 1,
        "Forge Cancel erased an unresolved operation");

    LegacyPacketDescriptor retried{};
    Check(
        PrepareOperation(
            &registry,
            40,
            41,
            &retried) &&
            forge_test::SameOperation(original, retried),
        "Reselected uncertain Forge attempt did not reuse its UUID");

    auto replayed = forge_test::ResultFor(
        original,
        SecureLegacyCommandDisposition::Replayed,
        1,
        0);
    Check(
        registry.Resolve(replayed) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasForgeEquipment &&
            registry.Snapshot().pending == 0,
        "Replayed Forge result did not settle matching reselection");
}

void CheckFamilyConflictAndExpiry() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    LegacyPacketDescriptor operation{};
    Check(
        forge_test::Establish(&registry) &&
            PrepareOperation(
                &registry,
                50,
                51,
                &operation),
        "Forge conflict setup failed");

    auto wrong = forge_test::ResultFor(operation);
    wrong.commandFamily =
        SecureLegacyCommandFamily::DecomposeGear;
    Check(
        registry.Resolve(wrong) ==
                SecureOperationRegistryResult::FamilyConflict &&
            registry.Snapshot().pending == 1,
        "Wrong-family Forge result consumed the operation");
    Check(
        registry.Resolve(forge_test::ResultFor(operation)) ==
            SecureOperationRegistryResult::Success,
        "Forge operation did not settle after a family conflict");

    LegacyPacketDescriptor expiring{};
    Check(
        PrepareOperation(
            &registry,
            52,
            53,
            &expiring),
        "Expiring Forge operation setup failed");
    hooks.now +=
        SecurePendingOperationLifetimeMilliseconds + 1;
    Check(
        registry.Snapshot().pending == 0 &&
            registry.Resolve(
                forge_test::ResultFor(expiring)) ==
                SecureOperationRegistryResult::UnknownOperation,
        "Expired Forge operation was still resolvable");
}

void CheckPrincipalAndCharacterIsolation() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    LegacyPacketDescriptor first{};
    Check(
        forge_test::Establish(
            &registry,
            "account-a",
            700) &&
            PrepareOperation(
                &registry,
                60,
                61,
                &first),
        "Forge principal-isolation setup failed");

    std::uint8_t login[36]{};
    forge_test::LoginPacket("account-b", login);
    LegacyPacketDescriptor second{};
    Check(
        forge_test::Describe(
            &registry,
            login,
            sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            registry.SetCharacter(700) ==
                SecureOperationRegistryResult::Success &&
            PrepareOperation(
                &registry,
                60,
                61,
                &second) &&
            !forge_test::SameOperation(first, second),
        "Different principal reused a Forge operation UUID");

    Check(
        registry.SetCharacter(701) ==
                SecureOperationRegistryResult::Success &&
            PrepareOperation(
                &registry,
                60,
                61,
                &first) &&
            !forge_test::SameOperation(first, second),
        "Different character reused a Forge operation UUID");
}

} // namespace

int RunSecureForgeResultIdentityTests() {
    Failures = 0;
    CheckFamilyThreeResultCodec();
    CheckTerminalSettlementAndFreshAttempt();
    CheckAllTerminalDispositions();
    CheckOlderResultPreservesChangedRecipe();
    CheckCancelReselectAndLostResponseRetry();
    CheckFamilyConflictAndExpiry();
    CheckPrincipalAndCharacterIsolation();
    return Failures;
}
