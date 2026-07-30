#include "SecureZodiacSkillGridSelectionIdentity.h"

#include "SecureClientProtocol.h"
#include "SecureZodiacSkillGridUpgradeIdentity.h"

namespace godswar::network {
namespace {

std::uint16_t ReadUInt16Little(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

std::int32_t ReadInt32Little(
    const std::uint8_t* source) noexcept {
    const auto value =
        static_cast<std::uint32_t>(source[0]) |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
    return static_cast<std::int32_t>(value);
}

bool IsKindAllowedForRow(
    int gridIndex,
    int selectedSkillKind) noexcept {
    if (selectedSkillKind == LegacyZodiacSkillKindClear) {
        return true;
    }
    const int expectedGroup =
        gridIndex % 8 < 4 ? 1 : 2;
    return selectedSkillKind / 10'000 == expectedGroup;
}

} // namespace

LegacyZodiacSkillGridSelectionPacketKind
ClassifyLegacyZodiacSkillGridSelectionPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridSelectionCommand* command) noexcept {
    if (packet == nullptr ||
        packetBytes < 4 ||
        packetBytes > SecureLegacyMaximumPacketBytes) {
        return
            LegacyZodiacSkillGridSelectionPacketKind::Unrelated;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    if (ReadUInt16Little(bytes + 2) != LegacyZodiacOpcode) {
        return
            LegacyZodiacSkillGridSelectionPacketKind::Unrelated;
    }
    if (packetBytes != LegacyZodiacPacketBytes ||
        ReadUInt16Little(bytes) != packetBytes) {
        return LegacyZodiacSkillGridSelectionPacketKind::
            InvalidMutation;
    }
    if (ReadUInt16Little(bytes + 10) !=
        LegacyZodiacSkillGridSelectionSid) {
        return
            LegacyZodiacSkillGridSelectionPacketKind::Unrelated;
    }

    const auto module = ReadUInt16Little(bytes + 8);
    const int gridIndex = ReadInt32Little(bytes + 12);
    const int selectedSkillKind = ReadInt32Little(bytes + 16);
    const int trailing = ReadInt32Little(bytes + 20);
    if ((module != LegacyZodiacNativeModule &&
            module != LegacyZodiacCompatibilityModule) ||
        gridIndex < LegacyZodiacSkillGridMinimum ||
        gridIndex > LegacyZodiacSkillGridMaximum ||
        (selectedSkillKind != LegacyZodiacSkillKindClear &&
         (selectedSkillKind < LegacyZodiacSkillKindMinimum ||
          selectedSkillKind > LegacyZodiacSkillKindMaximum)) ||
        !IsKindAllowedForRow(gridIndex, selectedSkillKind) ||
        trailing != 0) {
        return LegacyZodiacSkillGridSelectionPacketKind::
            InvalidMutation;
    }

    if (command != nullptr) {
        command->gridIndex = gridIndex;
        command->selectedSkillKind = selectedSkillKind;
    }
    return LegacyZodiacSkillGridSelectionPacketKind::Commit;
}

bool TryReadLegacyZodiacSkillGridSelection(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridSelectionCommand* command) noexcept {
    return command != nullptr &&
        ClassifyLegacyZodiacSkillGridSelectionPacket(
            packet,
            packetBytes,
            command) ==
            LegacyZodiacSkillGridSelectionPacketKind::Commit;
}

} // namespace godswar::network
