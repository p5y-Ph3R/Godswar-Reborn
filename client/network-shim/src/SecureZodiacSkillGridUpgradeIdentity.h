#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyZodiacOpcode = 10297;
inline constexpr std::uint16_t LegacyZodiacPacketBytes = 24;
inline constexpr std::uint16_t LegacyZodiacNativeModule = 0x00FF;
inline constexpr std::uint16_t LegacyZodiacCompatibilityModule = 0;
inline constexpr std::uint16_t LegacyZodiacSkillGridUpgradeSid = 101;
inline constexpr int LegacyZodiacSkillGridMinimum = 0;
inline constexpr int LegacyZodiacSkillGridMaximum = 15;

enum class LegacyZodiacSkillGridUpgradePacketKind : std::uint8_t {
    Unrelated = 0,
    Commit,
    InvalidMutation,
};

struct LegacyZodiacSkillGridUpgradeCommand final {
    int gridIndex = -1;
};

// SID 101 is a repeatable valuable operation. Unrelated Zodiac SIDs continue
// without a marker, while malformed SID-101 attempts fail closed.
LegacyZodiacSkillGridUpgradePacketKind
ClassifyLegacyZodiacSkillGridUpgradePacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridUpgradeCommand* command) noexcept;

bool TryReadLegacyZodiacSkillGridUpgrade(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridUpgradeCommand* command) noexcept;

} // namespace godswar::network
