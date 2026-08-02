#pragma once

#include "../src/SecureHolySuitCommandIdentity.h"
#include "../src/SecureLegacyCommandIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace holy_suit_test {

using namespace godswar::network;

inline constexpr std::size_t LoginPacketBytes =
    4 + SecurePrincipalFingerprintBytes;

struct Checks final {
    int failures = 0;

    void Require(bool condition, const char* message) {
        if (!condition) {
            std::fprintf(stderr, "FAIL: %s\n", message);
            ++failures;
        }
    }
};

inline void Write16(
    std::uint8_t* destination,
    std::uint16_t value) {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
}

inline void Write32(
    std::uint8_t* destination,
    std::uint32_t value) {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] = static_cast<std::uint8_t>(
            value >> (index * 8U));
    }
}

struct Hooks final {
    std::uint8_t randomSeed = 1;
    std::uint64_t now = 70'000;
};

inline bool Random(
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

inline bool Clock(
    void* context,
    std::uint64_t* unixMilliseconds) noexcept {
    if (context == nullptr || unixMilliseconds == nullptr) {
        return false;
    }
    *unixMilliseconds = static_cast<Hooks*>(context)->now;
    return true;
}

inline void BuildLoginPacket(
    std::uint8_t principalSeed,
    std::uint8_t* packet) {
    std::memset(packet, 0, LoginPacketBytes);
    Write16(packet, static_cast<std::uint16_t>(LoginPacketBytes));
    Write16(packet + 2, LegacyLoginGameServerOpcode);
    for (std::size_t index = 0;
         index < SecurePrincipalFingerprintBytes;
         ++index) {
        packet[4 + index] = static_cast<std::uint8_t>(
            principalSeed + index);
    }
}

inline std::int32_t SubId(LegacyHolySuitAction action) {
    switch (action) {
        case LegacyHolySuitAction::StoreExperience:
            return LegacyHolySuitStoreSubId;
        case LegacyHolySuitAction::TransferExperience:
            return LegacyHolySuitTransferSubId;
        case LegacyHolySuitAction::ConsumeWare:
            return LegacyHolySuitConsumeWareSubId;
        case LegacyHolySuitAction::TransformExperience:
            return LegacyHolySuitTransformSubId;
        default:
            return -1;
    }
}

inline void BuildHolySuitPacket(
    std::uint8_t* packet,
    LegacyHolySuitAction action,
    int primaryReference,
    int secondaryReference,
    std::uint32_t amount,
    std::uint32_t npcId = LegacySpartaHolySuitNpc,
    bool navigation = false) {
    std::memset(packet, 0xFF, LegacyHolySuitActionPacketBytes);
    Write16(packet, LegacyHolySuitActionPacketBytes);
    Write16(packet + 2, LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyHolySuitDialog);
    Write32(packet + 12, LegacyHolySuitDialog);
    Write32(packet + 16, static_cast<std::uint32_t>(SubId(action)));
    if (navigation) {
        return;
    }

    // The stock client writes a zero scratch value at argument zero for
    // commits. Navigation retains the normal all--1 argument vector.
    Write32(
        packet + 20 + LegacyHolySuitScratchArgument * 4,
        0);

    if (action == LegacyHolySuitAction::StoreExperience) {
        Write32(
            packet + 20 + LegacyHolySuitFirstItemArgument * 4,
            static_cast<std::uint32_t>(primaryReference));
        Write32(
            packet + 20 + LegacyHolySuitAmountArgument * 4,
            amount);
    } else if (
        action == LegacyHolySuitAction::TransferExperience ||
        action == LegacyHolySuitAction::ConsumeWare) {
        Write32(
            packet + 20 + LegacyHolySuitFirstItemArgument * 4,
            static_cast<std::uint32_t>(primaryReference));
        Write32(
            packet + 20 + LegacyHolySuitSecondItemArgument * 4,
            static_cast<std::uint32_t>(secondaryReference));
    } else {
        Write32(
            packet + 20 + LegacyHolySuitAmountArgument * 4,
            amount);
    }
}

inline bool Establish(
    SecurePendingOperationRegistry* registry,
    std::uint8_t principalSeed = 40,
    int characterId = 810) {
    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(principalSeed, login);
    LegacyPacketDescriptor descriptor{};
    return registry != nullptr &&
        registry->DescribePacket(
            login,
            sizeof(login),
            &descriptor) == SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry->SetCharacter(characterId) ==
            SecureOperationRegistryResult::Success;
}

inline SecureOperationRegistryResult DescribeHolySuit(
    SecurePendingOperationRegistry* registry,
    LegacyHolySuitAction action,
    int primaryReference,
    int secondaryReference,
    std::uint32_t amount,
    std::uint32_t npcId,
    LegacyPacketDescriptor* descriptor) {
    std::uint8_t packet[LegacyHolySuitActionPacketBytes]{};
    BuildHolySuitPacket(
        packet,
        action,
        primaryReference,
        secondaryReference,
        amount,
        npcId);
    return registry->DescribePacket(
        packet,
        sizeof(packet),
        descriptor);
}

inline bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return first.hasOperation && second.hasOperation &&
        std::memcmp(
            first.operation.operationId,
            second.operation.operationId,
            sizeof(first.operation.operationId)) == 0;
}

inline SecureLegacyCommandResult ResultFor(
    const LegacyPacketDescriptor& descriptor,
    SecureLegacyCommandFamily family) {
    SecureLegacyCommandResult result{};
    result.disposition = SecureLegacyCommandDisposition::Applied;
    result.commandFamily = family;
    result.inventoryRevision = 1;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

} // namespace holy_suit_test
