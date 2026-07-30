#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t
    LegacyZodiacSkillGridSelectionSid = 102;
inline constexpr int LegacyZodiacSkillKindClear = -1;
inline constexpr int LegacyZodiacSkillKindMinimum = 10'000;
inline constexpr int LegacyZodiacSkillKindMaximum = 29'999;

enum class LegacyZodiacSkillGridSelectionPacketKind :
    std::uint8_t {
    Unrelated = 0,
    Commit,
    InvalidMutation,
};

struct LegacyZodiacSkillGridSelectionCommand final {
    int gridIndex = -1;
    int selectedSkillKind = LegacyZodiacSkillKindClear;
};

// Origin.exe VA 0x552FD4 calls ConsEventRequest(255, 102, grid, Kind).
// Malformed SID-102 mutations fail closed; unrelated Zodiac SIDs pass through.
LegacyZodiacSkillGridSelectionPacketKind
ClassifyLegacyZodiacSkillGridSelectionPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridSelectionCommand* command) noexcept;

bool TryReadLegacyZodiacSkillGridSelection(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridSelectionCommand* command) noexcept;

} // namespace godswar::network
