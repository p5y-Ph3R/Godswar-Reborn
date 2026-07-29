#pragma once

#include "../src/SecurePendingOperationRegistry.h"

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace godswar::network::forge_test {

inline void Write16(
    std::uint8_t* destination,
    std::uint16_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value);
    destination[1] =
        static_cast<std::uint8_t>(value >> 8U);
}

inline void Write32(
    std::uint8_t* destination,
    std::uint32_t value) noexcept {
    for (std::size_t index = 0; index < 4; ++index) {
        destination[index] =
            static_cast<std::uint8_t>(
                value >> (index * 8U));
    }
}

inline void Header(
    std::uint8_t* packet,
    std::uint16_t packetBytes,
    std::uint16_t opcode) noexcept {
    Write16(packet, packetBytes);
    Write16(packet + 2, opcode);
}

inline void LoginPacket(
    const char* principal,
    std::uint8_t* packet) noexcept {
    std::memset(packet, 0, 36);
    Header(packet, 36, LegacyLoginGameServerOpcode);
    const std::size_t principalBytes =
        principal == nullptr ? 0 : std::strlen(principal);
    const std::size_t copyBytes =
        principalBytes < SecurePrincipalFingerprintBytes
        ? principalBytes
        : SecurePrincipalFingerprintBytes;
    if (copyBytes != 0) {
        std::memcpy(packet + 4, principal, copyBytes);
    }
}

inline void ForgeSelectionPacket(
    int bagSlot,
    std::uint32_t destination,
    std::uint32_t mode,
    std::uint8_t* packet,
    std::uint8_t scratch = 0xA5) noexcept {
    std::memset(
        packet,
        scratch,
        LegacyForgeSelectionPacketBytes);
    Header(
        packet,
        static_cast<std::uint16_t>(
            LegacyForgeSelectionPacketBytes),
        LegacyForgeSelectionOpcode);
    Write32(
        packet + 4,
        static_cast<std::uint32_t>(
            bagSlot / LegacyForgeSlotsPerPage));
    Write32(
        packet + 8,
        static_cast<std::uint32_t>(
            bagSlot % LegacyForgeSlotsPerPage));
    Write32(packet + 12, destination);
    Write32(packet + 16, mode);
    if (mode == LegacyOrdinaryForgeMode &&
        destination != LegacyForgeOddsIncrementAction) {
        Write32(
            packet + 20,
            static_cast<std::uint32_t>(9'000 + bagSlot));
        Write32(packet + 24, 5);
        Write32(packet + 28, 12);
        Write32(packet + 32, 99);
        Write32(packet + 36, 1);
    }
}

inline void ForgeStartPacket(
    std::uint32_t mode,
    std::uint8_t* packet,
    std::uint8_t scratch = 0xCD) noexcept {
    std::memset(
        packet,
        scratch,
        LegacyForgeStartPacketBytes);
    Header(
        packet,
        static_cast<std::uint16_t>(
            LegacyForgeStartPacketBytes),
        LegacyForgeStartOpcode);
    Write32(packet + 8, mode);
}

inline void ForgeCancelPacket(std::uint8_t* packet) noexcept {
    Header(
        packet,
        static_cast<std::uint16_t>(
            LegacyForgeCancelPacketBytes),
        LegacyForgeCancelOpcode);
}

inline void ForgeReplacementPacket(
    std::uint16_t opcode,
    std::uint8_t* packet,
    std::size_t packetBytes) noexcept {
    std::memset(packet, 0x6B, packetBytes);
    Header(
        packet,
        static_cast<std::uint16_t>(packetBytes),
        opcode);
}

struct Hooks final {
    std::uint64_t now = 50'000;
    std::uint8_t randomSeed = 41;
};

inline bool Random(
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

inline bool Clock(
    void* contextValue,
    std::uint64_t* now) noexcept {
    const auto* context =
        static_cast<const Hooks*>(contextValue);
    if (context == nullptr || now == nullptr) {
        return false;
    }
    *now = context->now;
    return true;
}

inline SecureOperationRegistryResult Describe(
    SecurePendingOperationRegistry* registry,
    const void* packet,
    std::size_t packetBytes,
    LegacyPacketDescriptor* descriptor = nullptr) noexcept {
    LegacyPacketDescriptor local{};
    return registry->DescribePacket(
        packet,
        packetBytes,
        descriptor == nullptr ? &local : descriptor);
}

inline bool Establish(
    SecurePendingOperationRegistry* registry,
    const char* principal = "test2",
    int characterId = 505) noexcept {
    std::uint8_t login[36]{};
    LoginPacket(principal, login);
    return Describe(registry, login, sizeof(login)) ==
            SecureOperationRegistryResult::Success &&
        registry->SetCharacter(characterId) ==
            SecureOperationRegistryResult::Success;
}

inline bool Stage(
    SecurePendingOperationRegistry* registry,
    int bagSlot,
    std::uint32_t destination,
    std::uint32_t mode =
        LegacyOrdinaryForgeMode) noexcept {
    std::uint8_t packet[LegacyForgeSelectionPacketBytes]{};
    ForgeSelectionPacket(
        bagSlot,
        destination,
        mode,
        packet);
    return Describe(registry, packet, sizeof(packet)) ==
        SecureOperationRegistryResult::Success;
}

inline bool Start(
    SecurePendingOperationRegistry* registry,
    LegacyPacketDescriptor* descriptor,
    std::uint8_t scratch = 0xCD) noexcept {
    std::uint8_t packet[LegacyForgeStartPacketBytes]{};
    ForgeStartPacket(
        LegacyOrdinaryForgeMode,
        packet,
        scratch);
    return Describe(
               registry,
               packet,
               sizeof(packet),
               descriptor) ==
            SecureOperationRegistryResult::Success &&
        descriptor != nullptr &&
        descriptor->hasOperation;
}

inline bool SameOperation(
    const LegacyPacketDescriptor& first,
    const LegacyPacketDescriptor& second) noexcept {
    return std::memcmp(
               first.operation.operationId,
               second.operation.operationId,
               sizeof(first.operation.operationId)) == 0;
}

inline SecureLegacyCommandResult ResultFor(
    const LegacyPacketDescriptor& descriptor,
    SecureLegacyCommandDisposition disposition =
        SecureLegacyCommandDisposition::Applied,
    std::uint32_t resultCode = 1,
    std::uint64_t inventoryRevision = 73) noexcept {
    SecureLegacyCommandResult result{};
    result.disposition = disposition;
    result.commandFamily =
        SecureLegacyCommandFamily::EquipmentForge;
    result.resultCode = resultCode;
    result.inventoryRevision = inventoryRevision;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

} // namespace godswar::network::forge_test
