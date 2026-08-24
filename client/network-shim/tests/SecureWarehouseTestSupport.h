#pragma once

#include "../src/SecureLegacyCommandIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"
#include "../src/SecureWarehouseCommandIdentity.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace warehouse_test {

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
    destination[1] = static_cast<std::uint8_t>(value >> 8U);
}

inline void Write32(
    std::uint8_t* destination,
    std::uint32_t value) {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] = static_cast<std::uint8_t>(
            value >> (index * 8U));
    }
}

inline void BuildTransferPacket(
    std::uint8_t* packet,
    std::int16_t warehouseSlot,
    std::int16_t secondIndex,
    std::int16_t thirdIndex,
    std::uint8_t direction,
    std::int32_t money = 0,
    std::uint16_t storageType = 0) {
    std::memset(
        packet, 0xA5, LegacyWarehouseTransferPacketBytes);
    Write16(packet, LegacyWarehouseTransferPacketBytes);
    Write16(packet + 2, LegacyWarehouseTransferOpcode);
    Write16(packet + 4,
        static_cast<std::uint16_t>(warehouseSlot));
    Write16(packet + 6,
        static_cast<std::uint16_t>(secondIndex));
    Write16(packet + 8,
        static_cast<std::uint16_t>(thirdIndex));
    Write32(packet + 12, static_cast<std::uint32_t>(money));
    packet[16] = direction;
    Write16(packet + 18, storageType);
}

inline void BuildManagerPacket(
    std::uint8_t* packet,
    std::int32_t subId,
    std::uint32_t npcId = LegacyAthensWarehouseManagerNpc) {
    std::memset(
        packet, 0xFF, LegacyWarehouseManagerPacketBytes);
    Write16(packet, LegacyWarehouseManagerPacketBytes);
    Write16(packet + 2, LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(packet + 8, LegacyWarehouseManagerDialog);
    Write32(packet + 12, LegacyWarehouseManagerDialog);
    Write32(packet + 16, static_cast<std::uint32_t>(subId));
}

inline void BuildLoginPacket(std::uint8_t* packet) {
    std::memset(packet, 0, LoginPacketBytes);
    Write16(packet, static_cast<std::uint16_t>(LoginPacketBytes));
    Write16(packet + 2, LegacyLoginGameServerOpcode);
    for (std::size_t index = 0;
         index < SecurePrincipalFingerprintBytes;
         ++index) {
        packet[4 + index] =
            static_cast<std::uint8_t>(90 + index);
    }
}

struct Hooks final {
    std::uint8_t randomSeed = 1;
    std::uint64_t now = 180'000;
};

inline bool Random(
    void* context,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* hooks = static_cast<Hooks*>(context);
    auto* output = static_cast<std::uint8_t*>(destination);
    if (hooks == nullptr || output == nullptr) {
        return false;
    }
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

inline bool Establish(SecurePendingOperationRegistry* registry) {
    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(login);
    LegacyPacketDescriptor descriptor{};
    return registry != nullptr &&
        registry->DescribePacket(
            login, sizeof(login), &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry->SetCharacter(940) ==
            SecureOperationRegistryResult::Success;
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
    SecureLegacyCommandFamily family,
    SecureLegacyCommandDisposition disposition,
    std::uint32_t code,
    std::uint64_t revision) {
    SecureLegacyCommandResult result{};
    result.disposition = disposition;
    result.commandFamily = family;
    result.resultCode = code;
    result.inventoryRevision = revision;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

} // namespace warehouse_test
