#pragma once

#include "../src/SecureEquipmentBagTransferIdentity.h"
#include "../src/SecureKitBagItemMoveIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace equipment_bag_transfer_test {

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
    std::uint64_t now = 90'000;
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
    *unixMilliseconds =
        static_cast<Hooks*>(context)->now;
    return true;
}

inline void BuildLoginPacket(
    std::uint8_t principalSeed,
    std::uint8_t* packet) {
    std::memset(packet, 0, LoginPacketBytes);
    Write16(
        packet,
        static_cast<std::uint16_t>(LoginPacketBytes));
    Write16(packet + 2, LegacyLoginGameServerOpcode);
    for (std::size_t index = 0;
         index < SecurePrincipalFingerprintBytes;
         ++index) {
        packet[4 + index] = static_cast<std::uint8_t>(
            principalSeed + index);
    }
}

inline void BuildTransferPacket(
    std::uint8_t* packet,
    std::size_t packetBytes,
    int equipmentSlot,
    int bagSlot,
    std::uint8_t tailSeed) {
    std::memset(packet, tailSeed, packetBytes);
    Write16(
        packet,
        static_cast<std::uint16_t>(packetBytes));
    Write16(packet + 2, LegacyStorageItemOpcode);
    Write32(packet + 4, 0x001AFB14U);
    Write16(
        packet + 8,
        static_cast<std::uint16_t>(equipmentSlot));
    Write16(packet + 10, UINT16_MAX);
    Write16(
        packet + 12,
        static_cast<std::uint16_t>(
            bagSlot / LegacyKitBagSlotsPerPage));
    Write16(
        packet + 14,
        static_cast<std::uint16_t>(
            bagSlot % LegacyKitBagSlotsPerPage));
}

inline bool Establish(
    SecurePendingOperationRegistry* registry,
    std::uint8_t principalSeed = 50,
    int characterId = 910) {
    std::uint8_t login[LoginPacketBytes]{};
    BuildLoginPacket(principalSeed, login);
    LegacyPacketDescriptor descriptor{};
    return registry != nullptr &&
        registry->DescribePacket(
            login,
            sizeof(login),
            &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation &&
        registry->SetCharacter(characterId) ==
            SecureOperationRegistryResult::Success;
}

inline bool DescribeTransfer(
    SecurePendingOperationRegistry* registry,
    int equipmentSlot,
    int bagSlot,
    std::uint8_t tailSeed,
    LegacyPacketDescriptor* descriptor) {
    std::uint8_t
        packet[LegacyEquipmentBagTransferPacketBytes]{};
    BuildTransferPacket(
        packet,
        sizeof(packet),
        equipmentSlot,
        bagSlot,
        tailSeed);
    return registry != nullptr &&
        registry->DescribePacket(
            packet,
            sizeof(packet),
            descriptor) ==
            SecureOperationRegistryResult::Success;
}

inline bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) {
    return first.hasOperation &&
        second.hasOperation &&
        std::memcmp(
            first.operation.operationId,
            second.operation.operationId,
            sizeof(first.operation.operationId)) == 0;
}

inline SecureLegacyCommandResult ResultFor(
    const LegacyPacketDescriptor& descriptor,
    SecureLegacyCommandDisposition disposition,
    std::uint32_t resultCode,
    std::uint64_t inventoryRevision) {
    SecureLegacyCommandResult result{};
    result.disposition = disposition;
    result.commandFamily =
        SecureLegacyCommandFamily::EquipmentBagTransfer;
    result.resultCode = resultCode;
    result.inventoryRevision = inventoryRevision;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

} // namespace equipment_bag_transfer_test
