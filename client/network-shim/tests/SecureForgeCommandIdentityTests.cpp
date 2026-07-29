#include "SecureForgeCommandIdentityTests.h"

#include "SecureForgeTestSupport.h"

#include "../src/SecureForgeCommandIdentity.h"

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

void CheckExactForgeParsers() {
    std::uint8_t
        selectionPacket[LegacyForgeSelectionPacketBytes]{};
    forge_test::ForgeSelectionPacket(
        95,
        LegacyForgeEquipmentDestination,
        LegacyOrdinaryForgeMode,
        selectionPacket);
    LegacyForgeSelection selection{};
    Check(
        TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket),
            &selection) &&
            selection.bagSlot == 95 &&
            selection.destination ==
                LegacyForgeEquipmentDestination &&
            selection.mode == LegacyOrdinaryForgeMode,
        "Forge selection parser rejected a valid exact packet");
    Check(
        !TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket) - 1,
            &selection),
        "Forge selection parser accepted a truncated packet");

    forge_test::Write16(selectionPacket, 59);
    Check(
        !TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket),
            &selection),
        "Forge selection parser accepted a mismatched header length");

    forge_test::ForgeSelectionPacket(
        0,
        LegacyForgeEquipmentDestination,
        LegacyOrdinaryForgeMode,
        selectionPacket);
    forge_test::Write32(selectionPacket + 4, 4);
    Check(
        !TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket),
            &selection),
        "Forge selection parser accepted page four");
    forge_test::ForgeSelectionPacket(
        0,
        LegacyForgeEquipmentDestination,
        LegacyOrdinaryForgeMode,
        selectionPacket);
    forge_test::Write32(selectionPacket + 8, 24);
    Check(
        !TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket),
            &selection),
        "Forge selection parser accepted slot twenty-four");

    forge_test::ForgeSelectionPacket(
        6,
        LegacyForgeEquipmentDestination,
        LegacyOrdinaryForgeMode,
        selectionPacket);
    forge_test::Write32(selectionPacket + 20, 0);
    Check(
        !TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket),
            &selection),
        "Forge selection parser accepted an empty descriptor");

    forge_test::ForgeSelectionPacket(
        6,
        LegacyForgeOddsIncrementAction,
        LegacyOrdinaryForgeMode,
        selectionPacket,
        0xFF);
    Check(
        TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket),
            &selection) &&
            selection.bagSlot == 6 &&
            selection.destination ==
                LegacyForgeOddsIncrementAction,
        "Forge increment parser trusted scratch descriptor bytes");

    forge_test::ForgeSelectionPacket(
        6,
        LegacyForgeEquipmentDestination,
        1,
        selectionPacket,
        0xFF);
    Check(
        TryReadLegacyForgeSelection(
            selectionPacket,
            sizeof(selectionPacket),
            &selection) &&
            selection.mode == 1,
        "Unsupported Forge mode could not be safely recognized");

    std::uint8_t startPacket[LegacyForgeStartPacketBytes]{};
    forge_test::ForgeStartPacket(0, startPacket, 0x11);
    std::uint32_t mode = 99;
    Check(
        TryReadLegacyForgeStart(
            startPacket,
            sizeof(startPacket),
            &mode) &&
            mode == 0,
        "Forge Start parser rejected a valid exact packet");
    startPacket[39] ^= 0xFF;
    Check(
        TryReadLegacyForgeStart(
            startPacket,
            sizeof(startPacket),
            &mode),
        "Forge Start parser trusted an unrelated tail byte");
    Check(
        !TryReadLegacyForgeStart(
            startPacket,
            sizeof(startPacket) - 1,
            &mode),
        "Forge Start parser accepted a truncated packet");

    std::uint8_t cancel[LegacyForgeCancelPacketBytes]{};
    forge_test::ForgeCancelPacket(cancel);
    Check(
        TryReadLegacyForgeCancel(cancel, sizeof(cancel)) &&
            !TryReadLegacyForgeCancel(
                cancel,
                sizeof(cancel) - 1),
        "Forge Cancel parser did not enforce its exact header");

    std::uint8_t replacement[12]{};
    forge_test::ForgeReplacementPacket(
        LegacyForgeReplacementSelectionOpcode,
        replacement,
        sizeof(replacement));
    Check(
        TryReadLegacyForgeReplacement(
            replacement,
            sizeof(replacement),
            LegacyForgeReplacementSelectionOpcode) &&
            !TryReadLegacyForgeReplacement(
                replacement,
                sizeof(replacement),
                LegacyForgeReplacementActionOpcode),
        "Forge replacement parser accepted the wrong opcode");
}

void CheckCanonicalMultistackIdentity() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    Check(
        forge_test::Establish(&registry),
        "Forge identity setup failed");
    Check(
        forge_test::Stage(
            &registry,
            25,
            LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &registry,
                0,
                LegacyForgePrimaryMaterialDestination) &&
            forge_test::Stage(
                &registry,
                72,
                LegacyForgeOddsDescriptorDestination) &&
            forge_test::Stage(
                &registry,
                72,
                LegacyForgeOddsIncrementAction) &&
            forge_test::Stage(
                &registry,
                7,
                LegacyForgeOddsDescriptorDestination) &&
            forge_test::Stage(
                &registry,
                7,
                LegacyForgeOddsIncrementAction) &&
            forge_test::Stage(
                &registry,
                7,
                LegacyForgeOddsIncrementAction),
        "Forge multistack selection setup failed");

    auto snapshot = registry.Snapshot();
    Check(
        snapshot.hasForgeEquipment &&
            snapshot.forgeEquipmentBagSlot == 25 &&
            snapshot.hasForgePrimaryMaterial &&
            snapshot.forgePrimaryMaterialBagSlot == 0 &&
            snapshot.forgeOddsCount == 2 &&
            snapshot.forgeOddsTotal == 3 &&
            snapshot.forgeOddsFullyLinked &&
            snapshot.forgeOdds[0].bagSlot == 7 &&
            snapshot.forgeOdds[0].quantity == 2 &&
            snapshot.forgeOdds[1].bagSlot == 72 &&
            snapshot.forgeOdds[1].quantity == 1,
        "Forge odds identity was not canonicalized by bag slot");

    Check(
        forge_test::Stage(
            &registry,
            7,
            LegacyForgeOddsDescriptorDestination) &&
            registry.Snapshot().forgeOddsTotal == 3,
        "Repeated Forge descriptor incremented the quantity");

    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor duplicate{};
    Check(
        forge_test::Start(&registry, &first, 0x11) &&
            forge_test::Start(&registry, &duplicate, 0xE7) &&
            forge_test::SameOperation(first, duplicate),
        "Duplicate Forge Start did not reuse its operation UUID");

    std::uint8_t
        mutated[LegacyForgeSelectionPacketBytes]{};
    forge_test::ForgeSelectionPacket(
        25,
        LegacyForgeEquipmentDestination,
        LegacyOrdinaryForgeMode,
        mutated);
    forge_test::Write32(mutated + 20, 42'424);
    Check(
        forge_test::Describe(
            &registry,
            mutated,
            sizeof(mutated)) ==
                SecureOperationRegistryResult::Success &&
            forge_test::Start(&registry, &duplicate, 0x4C) &&
            forge_test::SameOperation(first, duplicate),
        "Untrusted item descriptor changed the Forge UUID");
}

void CheckOddsLinkageAndBound() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    Check(
        forge_test::Establish(&registry) &&
            forge_test::Stage(
                &registry,
                0,
                LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &registry,
                1,
                LegacyForgePrimaryMaterialDestination),
        "Forge bound setup failed");

    Check(
        forge_test::Stage(
            &registry,
            2,
            LegacyForgeOddsIncrementAction) &&
            registry.Snapshot().forgeOddsCount == 0,
        "Unlinked Forge increment created a reservation");
    Check(
        forge_test::Stage(
            &registry,
            3,
            LegacyForgeOddsDescriptorDestination) &&
            forge_test::Stage(
                &registry,
                2,
                LegacyForgeOddsDescriptorDestination),
        "Forge odds descriptors were not staged");

    for (int index = 0; index < 25; ++index) {
        Check(
            forge_test::Stage(
                &registry,
                index % 2 == 0 ? 3 : 2,
                LegacyForgeOddsIncrementAction),
            "Forge bounded increment packet was rejected");
    }
    auto snapshot = registry.Snapshot();
    Check(
        snapshot.forgeOddsTotal == 25 &&
            snapshot.forgeOddsCount == 2 &&
            snapshot.forgeOdds[0].bagSlot == 2 &&
            snapshot.forgeOdds[1].bagSlot == 3,
        "Forge odds did not retain a bounded sorted multistack");
    Check(
        forge_test::Stage(
            &registry,
            2,
            LegacyForgeOddsIncrementAction) &&
            registry.Snapshot().forgeOddsTotal == 25,
        "Forge accepted a twenty-sixth odds crystal");

    LegacyPacketDescriptor operation{};
    Check(
        forge_test::Start(&registry, &operation),
        "Maximum bounded Forge selection did not mint an operation");

    forge_test::Hooks zeroHooks{};
    SecurePendingOperationRegistry zeroOdds(
        &zeroHooks,
        forge_test::Random,
        &zeroHooks,
        forge_test::Clock);
    LegacyPacketDescriptor zeroOperation{};
    Check(
        forge_test::Establish(&zeroOdds) &&
            forge_test::Stage(
                &zeroOdds,
                4,
                LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &zeroOdds,
                5,
                LegacyForgePrimaryMaterialDestination) &&
            forge_test::Start(
                &zeroOdds,
                &zeroOperation),
        "Zero-odds ordinary Forge could not mint an operation");
}

void CheckResetIsolationAndMalformedPackets() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    Check(
        forge_test::Establish(&registry) &&
            forge_test::Stage(
                &registry,
                8,
                LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &registry,
                9,
                LegacyForgePrimaryMaterialDestination),
        "Forge reset setup failed");

    std::uint8_t
        malformed[LegacyForgeSelectionPacketBytes]{};
    forge_test::ForgeSelectionPacket(
        10,
        LegacyForgeEquipmentDestination,
        LegacyOrdinaryForgeMode,
        malformed);
    forge_test::Write32(malformed + 4, 4);
    Check(
        forge_test::Describe(
            &registry,
            malformed,
            sizeof(malformed)) ==
                SecureOperationRegistryResult::InvalidPacket &&
            registry.Snapshot().forgeEquipmentBagSlot == 8 &&
            registry.Snapshot().forgePrimaryMaterialBagSlot == 9,
        "Malformed Forge packet overwrote valid staged roles");

    Check(
        forge_test::Stage(
            &registry,
            8,
            LegacyForgePrimaryMaterialDestination) &&
            registry.Snapshot().forgePrimaryMaterialBagSlot == 9,
        "One bag slot occupied two Forge roles");

    std::uint8_t unsupported[LegacyForgeSelectionPacketBytes]{};
    forge_test::ForgeSelectionPacket(
        8,
        LegacyForgeEquipmentDestination,
        1,
        unsupported);
    Check(
        forge_test::Describe(
            &registry,
            unsupported,
            sizeof(unsupported)) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasForgeEquipment,
        "Unsupported Forge mode retained ordinary staging");

    Check(
        forge_test::Stage(
            &registry,
            8,
            LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &registry,
                9,
                LegacyForgePrimaryMaterialDestination),
        "Unsupported Forge Start setup failed");
    std::uint8_t unsupportedStart[LegacyForgeStartPacketBytes]{};
    forge_test::ForgeStartPacket(2, unsupportedStart);
    LegacyPacketDescriptor unsupportedDescriptor{};
    Check(
        forge_test::Describe(
            &registry,
            unsupportedStart,
            sizeof(unsupportedStart),
            &unsupportedDescriptor) ==
                SecureOperationRegistryResult::Success &&
            !unsupportedDescriptor.hasOperation &&
            !registry.Snapshot().hasForgeEquipment,
        "Unsupported Forge Start minted an operation or retained staging");

    Check(
        forge_test::Stage(
            &registry,
            8,
            LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &registry,
                9,
                LegacyForgePrimaryMaterialDestination),
        "Forge cancel setup failed");
    std::uint8_t cancel[LegacyForgeCancelPacketBytes]{};
    forge_test::ForgeCancelPacket(cancel);
    Check(
        forge_test::Describe(
            &registry,
            cancel,
            sizeof(cancel)) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasForgeEquipment,
        "Forge Cancel did not clear staging");

    Check(
        forge_test::Stage(
            &registry,
            8,
            LegacyForgeEquipmentDestination),
        "Forge replacement setup failed");
    std::uint8_t replacement[12]{};
    forge_test::ForgeReplacementPacket(
        LegacyForgeReplacementActionOpcode,
        replacement,
        sizeof(replacement));
    Check(
        forge_test::Describe(
            &registry,
            replacement,
            sizeof(replacement)) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasForgeEquipment,
        "Forge replacement operation retained ordinary staging");

    Check(
        forge_test::Stage(
            &registry,
            8,
            LegacyForgeEquipmentDestination) &&
            registry.SetCharacter(606) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasForgeEquipment,
        "Character switch retained Forge staging");

    Check(
        forge_test::Stage(
            &registry,
            8,
            LegacyForgeEquipmentDestination),
        "Forge principal-switch setup failed");
    std::uint8_t login[36]{};
    forge_test::LoginPacket("another", login);
    Check(
        forge_test::Describe(
            &registry,
            login,
            sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            !registry.Snapshot().hasForgeEquipment &&
            !registry.Snapshot().hasCharacter,
        "Principal switch retained Forge or character state");
}

void CheckMissingIdentityAndIncompleteStart() {
    forge_test::Hooks hooks{};
    SecurePendingOperationRegistry noPrincipal(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    Check(
        forge_test::Stage(
            &noPrincipal,
            0,
            LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &noPrincipal,
                1,
                LegacyForgePrimaryMaterialDestination),
        "No-principal Forge setup failed");
    std::uint8_t start[LegacyForgeStartPacketBytes]{};
    forge_test::ForgeStartPacket(0, start);
    Check(
        forge_test::Describe(
            &noPrincipal,
            start,
            sizeof(start)) ==
                SecureOperationRegistryResult::NoPrincipal,
        "Forge Start without a principal was accepted");

    SecurePendingOperationRegistry noCharacter(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    std::uint8_t login[36]{};
    forge_test::LoginPacket("test2", login);
    Check(
        forge_test::Describe(
            &noCharacter,
            login,
            sizeof(login)) ==
                SecureOperationRegistryResult::Success &&
            forge_test::Stage(
                &noCharacter,
                0,
                LegacyForgeEquipmentDestination) &&
            forge_test::Stage(
                &noCharacter,
                1,
                LegacyForgePrimaryMaterialDestination) &&
            forge_test::Describe(
                &noCharacter,
                start,
                sizeof(start)) ==
                SecureOperationRegistryResult::NoCharacter,
        "Forge Start without a character was accepted");

    SecurePendingOperationRegistry incomplete(
        &hooks,
        forge_test::Random,
        &hooks,
        forge_test::Clock);
    Check(
        forge_test::Establish(&incomplete) &&
            forge_test::Stage(
                &incomplete,
                0,
                LegacyForgeEquipmentDestination) &&
            forge_test::Describe(
                &incomplete,
                start,
                sizeof(start)) ==
                SecureOperationRegistryResult::NoSelection &&
            !incomplete.Snapshot().hasForgeEquipment,
        "Incomplete Forge Start was accepted or retained staging");
}

} // namespace

int RunSecureForgeCommandIdentityTests() {
    Failures = 0;
    CheckExactForgeParsers();
    CheckCanonicalMultistackIdentity();
    CheckOddsLinkageAndBound();
    CheckResetIsolationAndMalformedPackets();
    CheckMissingIdentityAndIncompleteStart();
    return Failures;
}
