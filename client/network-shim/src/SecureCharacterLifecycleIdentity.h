#pragma once

#include "SecureClientProtocol.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyCreateCharacterOpcode = 10003;
inline constexpr std::uint16_t LegacyDeleteCharacterOpcode = 10004;
inline constexpr std::size_t LegacyCreateCharacterPacketBytes = 80;
inline constexpr std::size_t LegacyDeleteCharacterPacketBytes = 68;
inline constexpr std::size_t
    SecureCharacterLifecycleIntentBytes = 48;

enum class LegacyCharacterLifecyclePacketKind : std::uint8_t {
    Unrelated = 0,
    Command,
    InvalidMutation,
};

struct LegacyCharacterLifecycleIntent final {
    SecureLegacyCommandFamily family =
        SecureLegacyCommandFamily::CharacterCreate;
    std::uint8_t
        bytes[SecureCharacterLifecycleIntentBytes]{};
};

LegacyCharacterLifecyclePacketKind
ClassifyLegacyCharacterLifecyclePacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyCharacterLifecycleIntent* intent) noexcept;

bool EqualCharacterLifecycleIntent(
    const LegacyCharacterLifecycleIntent& first,
    const LegacyCharacterLifecycleIntent& second) noexcept;

} // namespace godswar::network
