#include "SecureWarehouseTestSupport.h"

namespace {

using namespace warehouse_test;

SecureOperationRegistryResult DescribeTransfer(
    SecurePendingOperationRegistry* registry,
    std::uint8_t* packet,
    LegacyPacketDescriptor* descriptor) {
    return registry->DescribePacket(
        packet, LegacyWarehouseTransferPacketBytes, descriptor);
}

SecureOperationRegistryResult DescribeManager(
    SecurePendingOperationRegistry* registry,
    std::uint8_t* packet,
    LegacyPacketDescriptor* descriptor) {
    return registry->DescribePacket(
        packet, LegacyWarehouseManagerPacketBytes, descriptor);
}

LegacyPacketDescriptor CreateTransferPending(
    SecurePendingOperationRegistry* registry) {
    std::uint8_t packet[LegacyWarehouseTransferPacketBytes]{};
    BuildTransferPacket(packet, 7, 1, 3, 1);
    LegacyPacketDescriptor descriptor{};
    if (DescribeTransfer(registry, packet, &descriptor) !=
            SecureOperationRegistryResult::Success) {
        return {};
    }
    return descriptor;
}

LegacyPacketDescriptor CreateExpansionPending(
    SecurePendingOperationRegistry* registry,
    std::uint32_t npcId = LegacyAthensWarehouseManagerNpc) {
    std::uint8_t packet[LegacyWarehouseManagerPacketBytes]{};
    BuildManagerPacket(
        packet, LegacyWarehouseManagerExpandSubId, npcId);
    LegacyPacketDescriptor descriptor{};
    if (DescribeManager(registry, packet, &descriptor) !=
            SecureOperationRegistryResult::Success) {
        return {};
    }
    return descriptor;
}

void CheckAuthorityAndNavigation(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    std::uint8_t transfer[LegacyWarehouseTransferPacketBytes]{};
    BuildTransferPacket(transfer, 7, 1, 3, 1);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        DescribeTransfer(&registry, transfer, &descriptor) ==
            SecureOperationRegistryResult::NoPrincipal,
        "warehouse transfer did not require a principal");

    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(login);
    checks->Require(
        registry.DescribePacket(
            login, sizeof(login), &descriptor) ==
                SecureOperationRegistryResult::Success &&
        DescribeTransfer(&registry, transfer, &descriptor) ==
                SecureOperationRegistryResult::NoCharacter &&
        registry.SetCharacter(940) ==
                SecureOperationRegistryResult::Success,
        "warehouse transfer did not require a character");

    std::uint8_t manager[LegacyWarehouseManagerPacketBytes]{};
    BuildManagerPacket(manager, LegacyWarehouseManagerInitialSubId);
    checks->Require(
        DescribeManager(&registry, manager, &descriptor) ==
                SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 0,
        "warehouse manager navigation received an operation UUID");
}

void CheckTransferIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "warehouse transfer identity setup failed");

    std::uint8_t packet[LegacyWarehouseTransferPacketBytes]{};
    BuildTransferPacket(packet, 7, 1, 3, 1, 0, 0xBEEF);
    LegacyPacketDescriptor first{};
    LegacyPacketDescriptor retry{};
    checks->Require(
        DescribeTransfer(&registry, packet, &first) ==
                SecureOperationRegistryResult::Success &&
        DescribeTransfer(&registry, packet, &retry) ==
                SecureOperationRegistryResult::Success &&
        first.hasOperation &&
        first.operation.opcode == LegacyWarehouseTransferOpcode &&
        first.operation.packetBytes ==
            LegacyWarehouseTransferPacketBytes &&
        SameOperation(first, retry),
        "warehouse transfer retry did not retain one UUID");

    packet[10] ^= 0x55;
    packet[17] ^= 0x33;
    Write16(packet + 18, 0xCAFE);
    LegacyPacketDescriptor scratchVariant{};
    checks->Require(
        DescribeTransfer(&registry, packet, &scratchVariant) ==
                SecureOperationRegistryResult::Success &&
        SameOperation(first, scratchVariant),
        "warehouse native scratch changed transfer identity");

    BuildTransferPacket(packet, 8, 1, 3, 1);
    LegacyPacketDescriptor anotherDestination{};
    checks->Require(
        DescribeTransfer(
            &registry, packet, &anotherDestination) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(first, anotherDestination),
        "warehouse deposit identity ignored its destination slot");

    BuildTransferPacket(packet, 7, 8, -1, 0);
    LegacyPacketDescriptor internalMove{};
    checks->Require(
        DescribeTransfer(&registry, packet, &internalMove) ==
                SecureOperationRegistryResult::Success &&
        !SameOperation(first, internalMove),
        "warehouse operation kind was omitted from identity");
}

void CheckExpansionIdentity(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "warehouse expansion identity setup failed");

    auto athens = CreateExpansionPending(
        &registry, LegacyAthensWarehouseManagerNpc);
    auto sparta = CreateExpansionPending(
        &registry, LegacySpartaWarehouseManagerNpc);
    checks->Require(
        athens.hasOperation && SameOperation(athens, sparta) &&
        registry.Snapshot().pending == 1,
        "warehouse expansion identity changed across capital managers");

    std::uint8_t malformed[LegacyWarehouseManagerPacketBytes]{};
    BuildManagerPacket(malformed, 101);
    LegacyPacketDescriptor descriptor{};
    checks->Require(
        DescribeManager(&registry, malformed, &descriptor) ==
                SecureOperationRegistryResult::InvalidPacket &&
        !descriptor.hasOperation &&
        registry.Snapshot().pending == 1,
        "malformed warehouse manager action allocated operation state");
}

void CheckTransferResults(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "warehouse transfer result setup failed");
    auto descriptor = CreateTransferPending(&registry);
    checks->Require(descriptor.hasOperation,
        "warehouse transfer result setup created no UUID");

    auto wrongFamily = ResultFor(
        descriptor, SecureLegacyCommandFamily::WarehouseExpansion,
        SecureLegacyCommandDisposition::Applied,
        LegacyWarehouseFirstExpansionSuccessResult, 1);
    checks->Require(
        registry.Resolve(wrongFamily) ==
                SecureOperationRegistryResult::FamilyConflict &&
        registry.Snapshot().pending == 1,
        "warehouse transfer accepted another result family");

    const SecureLegacyCommandResult invalid[]{
        ResultFor(descriptor,
            SecureLegacyCommandFamily::WarehouseTransfer,
            SecureLegacyCommandDisposition::Applied, 17, 1),
        ResultFor(descriptor,
            SecureLegacyCommandFamily::WarehouseTransfer,
            SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseDepositedResult, 0),
        ResultFor(descriptor,
            SecureLegacyCommandFamily::WarehouseTransfer,
            SecureLegacyCommandDisposition::Applied,
            LegacyWarehouseEmptySourceResult, 1),
        ResultFor(descriptor,
            SecureLegacyCommandFamily::WarehouseTransfer,
            SecureLegacyCommandDisposition::Conflict,
            LegacyWarehouseRestrictedItemResult, 0),
    };
    for (const auto& result : invalid) {
        checks->Require(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::InvalidPacket &&
            registry.Snapshot().pending == 1,
            "invalid warehouse transfer result retired its UUID");
    }

    struct Valid final {
        SecureLegacyCommandDisposition disposition;
        std::uint32_t code;
        std::uint64_t revision;
    };
    const Valid valid[]{
        {SecureLegacyCommandDisposition::Applied,
            LegacyWarehouseDepositedResult, 1},
        {SecureLegacyCommandDisposition::Replayed,
            LegacyWarehouseSwappedResult, 2},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseEmptySourceResult, 0},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseStackIncompatibleResult, 0},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseConcurrentConflictResult, 0},
        {SecureLegacyCommandDisposition::Conflict,
            LegacyWarehouseConcurrentConflictResult, 0},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseRestrictedItemResult, 0},
    };
    for (const auto& expected : valid) {
        const auto result = ResultFor(
            descriptor, SecureLegacyCommandFamily::WarehouseTransfer,
            expected.disposition, expected.code, expected.revision);
        checks->Require(
            descriptor.hasOperation &&
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success,
            "valid warehouse transfer result did not settle");
        descriptor = CreateTransferPending(&registry);
    }
}

void CheckExpansionResults(Checks* checks) {
    Hooks hooks{};
    SecurePendingOperationRegistry registry(
        &hooks, Random, &hooks, Clock);
    checks->Require(Establish(&registry),
        "warehouse expansion result setup failed");
    auto descriptor = CreateExpansionPending(&registry);

    const SecureLegacyCommandResult invalid[]{
        ResultFor(descriptor,
            SecureLegacyCommandFamily::WarehouseExpansion,
            SecureLegacyCommandDisposition::Applied, 997, 1),
        ResultFor(descriptor,
            SecureLegacyCommandFamily::WarehouseExpansion,
            SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseFirstExpansionSuccessResult, 0),
        ResultFor(descriptor,
            SecureLegacyCommandFamily::WarehouseExpansion,
            SecureLegacyCommandDisposition::Conflict,
            LegacyWarehouseAlreadyMaximumResult, 0),
    };
    for (const auto& result : invalid) {
        checks->Require(
            registry.Resolve(result) ==
                    SecureOperationRegistryResult::InvalidPacket &&
            registry.Snapshot().pending == 1,
            "invalid warehouse expansion result retired its UUID");
    }

    struct Valid final {
        SecureLegacyCommandDisposition disposition;
        std::uint32_t code;
        std::uint64_t revision;
    };
    const Valid valid[]{
        {SecureLegacyCommandDisposition::Applied,
            LegacyWarehouseFirstExpansionSuccessResult, 1},
        {SecureLegacyCommandDisposition::Replayed,
            LegacyWarehouseLastExpansionSuccessResult, 2},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseMissingKeysResultBase + 201, 0},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseMissingKeysResultBase + 908, 0},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseAlreadyMaximumResult, 0},
        {SecureLegacyCommandDisposition::Rejected,
            LegacyWarehouseExpansionFailedResult, 0},
        {SecureLegacyCommandDisposition::Conflict,
            LegacyWarehouseExpansionFailedResult, 0},
    };
    for (const auto& expected : valid) {
        const auto result = ResultFor(
            descriptor, SecureLegacyCommandFamily::WarehouseExpansion,
            expected.disposition, expected.code, expected.revision);
        checks->Require(
            descriptor.hasOperation &&
            registry.Resolve(result) ==
                SecureOperationRegistryResult::Success,
            "valid warehouse expansion result did not settle");
        descriptor = CreateExpansionPending(&registry);
    }
}

void CheckResultCodec(Checks* checks) {
    checks->Require(
        static_cast<std::uint16_t>(
            SecureLegacyCommandFamily::WarehouseTransfer) == 58 &&
        static_cast<std::uint16_t>(
            SecureLegacyCommandFamily::WarehouseExpansion) == 59,
        "warehouse secure family numbers no longer match the server");

    const SecureLegacyCommandFamily families[]{
        SecureLegacyCommandFamily::WarehouseTransfer,
        SecureLegacyCommandFamily::WarehouseExpansion};
    for (const auto family : families) {
        SecureLegacyCommandResult input{};
        input.disposition = SecureLegacyCommandDisposition::Applied;
        input.commandFamily = family;
        input.resultCode = family ==
                SecureLegacyCommandFamily::WarehouseTransfer
            ? LegacyWarehouseStackedResult
            : LegacyWarehouseFirstExpansionSuccessResult + 1;
        input.inventoryRevision = 9;
        for (std::size_t index = 0;
             index < sizeof(input.operationId);
             ++index) {
            input.operationId[index] =
                static_cast<std::uint8_t>(index + 1);
        }
        std::uint8_t encoded[
            SecureLegacyCommandResultPayloadBytes]{};
        SecureLegacyCommandResult decoded{};
        checks->Require(
            TryEncodeSecureLegacyCommandResult(
                input, encoded, sizeof(encoded)) &&
            TryDecodeSecureLegacyCommandResult(
                encoded, sizeof(encoded), &decoded) &&
            decoded.commandFamily == family &&
            decoded.resultCode == input.resultCode &&
            decoded.inventoryRevision == input.inventoryRevision &&
            std::memcmp(
                decoded.operationId,
                input.operationId,
                sizeof(input.operationId)) == 0,
            "warehouse secure result family did not round-trip");
    }
}

} // namespace

int RunSecureWarehouseRegistryTests() {
    Checks checks{};
    CheckAuthorityAndNavigation(&checks);
    CheckTransferIdentity(&checks);
    CheckExpansionIdentity(&checks);
    CheckTransferResults(&checks);
    CheckExpansionResults(&checks);
    CheckResultCodec(&checks);
    return checks.failures;
}
