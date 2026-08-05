#pragma once

#include "../src/SecureHolyStoneCommandIdentity.h"
#include "../src/SecureLegacyCommandIdentity.h"
#include "../src/SecurePendingOperationRegistry.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

namespace holy_stone_test {

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

inline void BuildHolyStonePacket(
    std::uint8_t* packet,
    LegacyHolyStoneAction action,
    int targetReference,
    int secondaryValue,
    std::uint32_t npcId = LegacySpartaHolyStoneNpc,
    bool navigation = false) {
    std::memset(
        packet,
        0xFF,
        LegacyHolyStoneActionPacketBytes);
    Write16(packet, LegacyHolyStoneActionPacketBytes);
    Write16(packet + 2, LegacyNpcFunctionActionOpcode);
    Write32(packet + 4, npcId);
    Write32(
        packet + 8,
        static_cast<std::uint32_t>(LegacyHolyStoneDialog));
    Write32(
        packet + 12,
        static_cast<std::uint32_t>(LegacyHolyStoneDialog));

    std::int32_t subId = LegacyHolyStoneMountSubId;
    if (action == LegacyHolyStoneAction::Remove) {
        subId = LegacyHolyStoneRemoveSubId;
    } else if (action == LegacyHolyStoneAction::Drill) {
        subId = LegacyHolyStoneDrillSubId;
    } else if (action == LegacyHolyStoneAction::AdvancedDrill) {
        subId = LegacyHolyStoneAdvancedDrillSubId;
    } else if (action == LegacyHolyStoneAction::Upgrade) {
        subId = LegacyHolyStoneUpgradeSubId;
    } else if (action == LegacyHolyStoneAction::ImplementSpirit) {
        subId = LegacyHolyStoneImplementSpiritSubId;
    } else if (action == LegacyHolyStoneAction::Combine) {
        subId = LegacyHolyStoneCombineSubId;
    }
    Write32(packet + 16, static_cast<std::uint32_t>(subId));
    if (navigation) {
        return;
    }

    if (action == LegacyHolyStoneAction::Mount) {
        Write32(packet + 20, 0);
        Write32(
            packet + 20 + 6 * 4,
            static_cast<std::uint32_t>(targetReference));
        Write32(
            packet + 20 + 7 * 4,
            static_cast<std::uint32_t>(secondaryValue));
    } else if (action == LegacyHolyStoneAction::Remove) {
        Write32(
            packet + 20 + 6 * 4,
            static_cast<std::uint32_t>(targetReference));
        Write32(
            packet + 20 + 10 * 4,
            static_cast<std::uint32_t>(secondaryValue));
    } else if (action == LegacyHolyStoneAction::Drill) {
        Write32(
            packet + 20 + 6 * 4,
            static_cast<std::uint32_t>(targetReference));
    } else if (action == LegacyHolyStoneAction::AdvancedDrill) {
        Write32(packet + 20, 0);
        Write32(
            packet + 20 + 6 * 4,
            static_cast<std::uint32_t>(targetReference));
        Write32(
            packet + 20 + 7 * 4,
            static_cast<std::uint32_t>(secondaryValue));
    } else if (action == LegacyHolyStoneAction::Upgrade ||
               action == LegacyHolyStoneAction::ImplementSpirit) {
        // No argument role is trusted for staged actions. Emit a populated
        // candidate solely so parser/registry tests can exercise that path.
        Write32(packet + 20 + 3 * 4, 0);
    }
}

inline std::uint32_t EncodeHolyStoneBagReference(int bagSlot) {
    return static_cast<std::uint32_t>(
        (bagSlot / LegacyHolyStoneBagSlotsPerPage) *
            LegacyHolyStoneBagPageStride +
        (bagSlot % LegacyHolyStoneBagSlotsPerPage));
}

inline void BuildHolyStoneCombinePacket(
    std::uint8_t* packet,
    const int* orderedBagSlots,
    std::uint32_t npcId = LegacySpartaHolyStoneNpc,
    bool navigation = false) {
    BuildHolyStonePacket(
        packet,
        LegacyHolyStoneAction::Combine,
        -1,
        -1,
        npcId,
        true);
    if (navigation) {
        return;
    }
    for (std::size_t index = 0; index < 4; ++index) {
        Write32(
            packet + 20 + (6 + index) * 4,
            orderedBagSlots == nullptr
                ? 0xFFFFFFFFU
                : EncodeHolyStoneBagReference(
                      orderedBagSlots[index]));
    }
}

inline bool StageSelection(
    SecurePendingOperationRegistry* registry,
    int bagSlot,
    bool selected = true) {
    if (registry == nullptr || bagSlot < 0 || bagSlot >= 96) {
        return false;
    }
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
    return registry->DescribePacket(
               packet, sizeof(packet), &descriptor) ==
            SecureOperationRegistryResult::Success &&
        !descriptor.hasOperation;
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

inline SecureOperationRegistryResult DescribeHolyStone(
    SecurePendingOperationRegistry* registry,
    LegacyHolyStoneAction action,
    int targetReference,
    int secondaryValue,
    std::uint32_t npcId,
    LegacyPacketDescriptor* descriptor) {
    std::uint8_t packet[LegacyHolyStoneActionPacketBytes]{};
    BuildHolyStonePacket(
        packet,
        action,
        targetReference,
        secondaryValue,
        npcId);
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
    SecureLegacyCommandFamily family,
    SecureLegacyCommandDisposition disposition =
        SecureLegacyCommandDisposition::Applied) {
    SecureLegacyCommandResult result{};
    result.disposition = disposition;
    result.commandFamily = family;
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

} // namespace holy_stone_test
