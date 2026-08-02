#include "SecureHolySuitTestSupport.h"

namespace {

using namespace holy_suit_test;

void CheckPrincipalCharacterAndNavigation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        12,
        -1,
        1);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) == SecureOperationRegistryResult::NoPrincipal,
        "Holy Suit commit did not require a principal");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(40, login);
    checks->Require(
        registry.DescribePacket(
            login,
            sizeof(login),
            &descriptor) == SecureOperationRegistryResult::Success &&
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) == SecureOperationRegistryResult::NoCharacter &&
        registry.SetCharacter(810) ==
            SecureOperationRegistryResult::Success,
        "Holy Suit commit did not require a character");

    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::StoreExperience,
        -1,
        -1,
        0,
        LegacySpartaHolySuitNpc,
        true);
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) == SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 0,
        "Holy Suit navigation received an operation UUID");
}

void CheckSemanticIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "Holy Suit identity setup failed");

    LegacyPacketDescriptor sparta{};
    LegacyPacketDescriptor retry{};
    LegacyPacketDescriptor anotherAmount{};
    LegacyPacketDescriptor anotherBox{};
    checks->Require(
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::StoreExperience,
            12,
            -1,
            50'000,
            LegacySpartaHolySuitNpc,
            &sparta) == SecureOperationRegistryResult::Success &&
        sparta.hasOperation &&
        sparta.operation.packetBytes ==
            LegacyHolySuitActionPacketBytes &&
        sparta.operation.opcode == LegacyNpcFunctionActionOpcode &&
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::StoreExperience,
            12,
            -1,
            50'000,
            LegacyAthensHolySuitNpc,
            &retry) == SecureOperationRegistryResult::Success &&
        SameOperation(sparta, retry),
        "Cross-city Holy Suit retry changed operation UUID");

    checks->Require(
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::StoreExperience,
            12,
            -1,
            50'001,
            LegacySpartaHolySuitNpc,
            &anotherAmount) == SecureOperationRegistryResult::Success &&
        !SameOperation(sparta, anotherAmount) &&
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::StoreExperience,
            13,
            -1,
            50'000,
            LegacySpartaHolySuitNpc,
            &anotherBox) == SecureOperationRegistryResult::Success &&
        !SameOperation(sparta, anotherBox),
        "Holy Suit identity aliased amount or box roles");

    LegacyPacketDescriptor transfer{};
    LegacyPacketDescriptor ware{};
    LegacyPacketDescriptor transform{};
    checks->Require(
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::TransferExperience,
            102,
            103,
            0,
            LegacySpartaHolySuitNpc,
            &transfer) == SecureOperationRegistryResult::Success &&
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::ConsumeWare,
            102,
            103,
            0,
            LegacySpartaHolySuitNpc,
            &ware) == SecureOperationRegistryResult::Success &&
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::TransformExperience,
            -1,
            -1,
            5,
            LegacySpartaHolySuitNpc,
            &transform) == SecureOperationRegistryResult::Success &&
        !SameOperation(transfer, ware) &&
        !SameOperation(ware, transform),
        "Distinct Holy Suit families shared an operation UUID");

    checks->Require(
        registry.SetCharacter(811) ==
            SecureOperationRegistryResult::Success,
        "Holy Suit character switch failed");
    LegacyPacketDescriptor anotherCharacter{};
    checks->Require(
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::StoreExperience,
            12,
            -1,
            50'000,
            LegacySpartaHolySuitNpc,
            &anotherCharacter) == SecureOperationRegistryResult::Success &&
        !SameOperation(sparta, anotherCharacter),
        "Two characters shared a Holy Suit operation UUID");
}

void CheckSettlementAndResultCodec(Checks* checks) {
    struct FamilyCase final {
        LegacyHolySuitAction action;
        SecureLegacyCommandFamily family;
        int primary;
        int secondary;
        int amount;
    };
    const FamilyCase families[]{
        {LegacyHolySuitAction::StoreExperience,
         SecureLegacyCommandFamily::HolySuitStoreExperience,
         100, -1, 1},
        {LegacyHolySuitAction::TransferExperience,
         SecureLegacyCommandFamily::HolySuitTransferExperience,
         100, 101, 0},
        {LegacyHolySuitAction::ConsumeWare,
         SecureLegacyCommandFamily::HolySuitConsumeWare,
         100, 101, 0},
        {LegacyHolySuitAction::TransformExperience,
         SecureLegacyCommandFamily::HolySuitTransformExperience,
         -1, -1, 1},
    };

    for (const auto& family : families) {
        Hooks hooks{};
        SecurePendingOperationRegistry registry(
            &hooks, Random, &hooks, Clock);
        LegacyPacketDescriptor pending{};
        checks->Require(
            Establish(&registry) &&
            DescribeHolySuit(
                &registry,
                family.action,
                family.primary,
                family.secondary,
                family.amount,
                LegacySpartaHolySuitNpc,
                &pending) == SecureOperationRegistryResult::Success,
            "Holy Suit settlement setup failed");

        const auto result = ResultFor(pending, family.family);
        checks->Require(
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success &&
            registry.Snapshot().pending == 0 &&
            registry.Snapshot().resolved == 1 &&
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success,
            "Holy Suit result did not settle idempotently");

        auto wrongFamily = result;
        wrongFamily.commandFamily =
            family.family ==
                SecureLegacyCommandFamily::HolySuitStoreExperience
            ? SecureLegacyCommandFamily::HolySuitTransferExperience
            : SecureLegacyCommandFamily::HolySuitStoreExperience;
        checks->Require(
            registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict,
            "Holy Suit tombstone accepted another family");

        std::uint8_t encoded[
            SecureLegacyCommandResultPayloadBytes]{};
        SecureLegacyCommandResult decoded{};
        checks->Require(
            TryEncodeSecureLegacyCommandResult(
                result,
                encoded,
                sizeof(encoded)) &&
            TryDecodeSecureLegacyCommandResult(
                encoded,
                sizeof(encoded),
                &decoded) &&
            decoded.commandFamily == family.family,
            "Holy Suit result family did not round-trip");
    }
}

void CheckInvalidPacketAndCapacity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "Holy Suit capacity setup failed");

    std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
    BuildHolySuitPacket(
        packet,
        LegacyHolySuitAction::TransferExperience,
        100,
        100,
        0);
    LegacyPacketDescriptor descriptor{};
    const auto randomBefore = hooks.randomSeed;
    checks->Require(
        registry.DescribePacket(
            packet,
            sizeof(packet),
            &descriptor) == SecureOperationRegistryResult::InvalidPacket &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 0 &&
        hooks.randomSeed == randomBefore,
        "Invalid Holy Suit lookalike received a UUID");

    bool filled = true;
    for (std::size_t index = 0;
         index < SecurePendingOperationCapacity;
         ++index) {
        filled =
            DescribeHolySuit(
                &registry,
                LegacyHolySuitAction::StoreExperience,
                LegacyHolySuitBagReferenceMinimum,
                -1,
                static_cast<int>(index + 1),
                LegacySpartaHolySuitNpc,
                &descriptor) == SecureOperationRegistryResult::Success &&
            filled;
    }
    checks->Require(
        filled && registry.Snapshot().pending ==
            SecurePendingOperationCapacity,
        "Holy Suit operations did not fill bounded capacity");
    checks->Require(
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::StoreExperience,
            LegacyHolySuitBagReferenceMinimum,
            -1,
            17,
            LegacySpartaHolySuitNpc,
            &descriptor) == SecureOperationRegistryResult::Capacity,
        "Holy Suit operations exceeded bounded capacity");

    hooks.now += SecurePendingOperationLifetimeMilliseconds;
    checks->Require(
        DescribeHolySuit(
            &registry,
            LegacyHolySuitAction::StoreExperience,
            LegacyHolySuitBagReferenceMinimum,
            -1,
            17,
            LegacySpartaHolySuitNpc,
            &descriptor) == SecureOperationRegistryResult::Success &&
        descriptor.hasOperation &&
        registry.Snapshot().pending == 1,
        "Expired Holy Suit operations retained registry capacity");
}

} // namespace

int RunSecureHolySuitRegistryTests() {
    Checks checks{};
    CheckPrincipalCharacterAndNavigation(&checks);
    CheckSemanticIdentity(&checks);
    CheckSettlementAndResultCodec(&checks);
    CheckInvalidPacketAndCapacity(&checks);
    return checks.failures;
}
