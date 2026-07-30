#pragma once

#include "../src/SecureLegacyCommandIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"
#include "../src/SecureZodiacSkillGridUpgradeIdentity.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace zodiac_upgrade_test {

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
    std::uint64_t now = 120'000;
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

inline void BuildUpgradePacket(
    std::uint8_t* packet,
    int gridIndex,
    std::uint16_t module = LegacyZodiacNativeModule,
    std::uint32_t playerId = 0,
    std::int32_t placeholder = -1,
    std::int32_t trailing = 0) {
    std::memset(packet, 0, LegacyZodiacPacketBytes);
    Write16(packet, LegacyZodiacPacketBytes);
    Write16(packet + 2, LegacyZodiacOpcode);
    Write32(packet + 4, playerId);
    Write16(packet + 8, module);
    Write16(packet + 10, LegacyZodiacSkillGridUpgradeSid);
    Write32(
        packet + 12,
        static_cast<std::uint32_t>(gridIndex));
    Write32(
        packet + 16,
        static_cast<std::uint32_t>(placeholder));
    Write32(
        packet + 20,
        static_cast<std::uint32_t>(trailing));
}

inline bool Establish(
    SecurePendingOperationRegistry* registry,
    std::uint8_t principalSeed = 70,
    int characterId = 920) {
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

inline SecureOperationRegistryResult DescribeUpgrade(
    SecurePendingOperationRegistry* registry,
    int gridIndex,
    LegacyPacketDescriptor* descriptor,
    std::uint16_t module = LegacyZodiacNativeModule,
    std::uint32_t playerId = 0) {
    std::uint8_t packet[LegacyZodiacPacketBytes]{};
    BuildUpgradePacket(
        packet,
        gridIndex,
        module,
        playerId);
    return registry->DescribePacket(
        packet,
        sizeof(packet),
        descriptor);
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
    SecureLegacyCommandDisposition disposition =
        SecureLegacyCommandDisposition::Applied) {
    SecureLegacyCommandResult result{};
    result.disposition = disposition;
    result.commandFamily =
        SecureLegacyCommandFamily::ZodiacSkillGridUpgrade;
    result.inventoryRevision =
        disposition == SecureLegacyCommandDisposition::Applied
        ? 1
        : 0;
    std::memcpy(
        result.operationId,
        descriptor.operation.operationId,
        sizeof(result.operationId));
    return result;
}

} // namespace zodiac_upgrade_test
