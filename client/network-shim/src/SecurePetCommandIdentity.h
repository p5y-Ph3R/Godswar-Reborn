#pragma once

#include "SecureClientProtocol.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyBagItemActivationOpcode = 10051;
inline constexpr std::uint16_t LegacyPetTakeOpcode = 10239;
inline constexpr std::uint16_t LegacyPetCallOutOpcode = 10240;
inline constexpr std::uint16_t LegacyPetRecallOpcode = 10241;
inline constexpr std::uint16_t LegacyPetLevelUpgradeOpcode = 10285;
inline constexpr std::size_t LegacyBagItemActivationPacketBytes = 92;
inline constexpr std::size_t LegacyPetCommandPacketBytes = 8;
inline constexpr std::size_t SecurePetCommandIntentBytes = 16;

enum class LegacyPetCommandPacketKind : std::uint8_t {
    Unrelated = 0,
    Command,
    InvalidMutation,
};

struct LegacyPetCommandIntent final {
    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::BagItemActivation;
    std::uint8_t bytes[SecurePetCommandIntentBytes]{};
};

LegacyPetCommandPacketKind ClassifyLegacyPetCommandPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept;

bool EqualPetCommandIntent(
    const LegacyPetCommandIntent& first,
    const LegacyPetCommandIntent& second) noexcept;

} // namespace godswar::network
