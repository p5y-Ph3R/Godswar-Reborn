#include "SecureZodiacSkillGridUpgradeIdentity.h"

#include "SecureLegacyCommandIdentity.h"

namespace godswar::network {
namespace {

std::uint16_t ReadUInt16Little(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

std::uint32_t ReadUInt32Little(
    const std::uint8_t* source) noexcept {
    return source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

} // namespace

LegacyZodiacSkillGridUpgradePacketKind
ClassifyLegacyZodiacSkillGridUpgradePacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridUpgradeCommand* command) noexcept {
    if (packet == nullptr ||
        packetBytes < 4 ||
        packetBytes > SecureLegacyMaximumPacketBytes) {
        return LegacyZodiacSkillGridUpgradePacketKind::Unrelated;
    }

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    if (ReadUInt16Little(bytes + 2) != LegacyZodiacOpcode) {
        return LegacyZodiacSkillGridUpgradePacketKind::Unrelated;
    }
    if (packetBytes != LegacyZodiacPacketBytes ||
        ReadUInt16Little(bytes) != packetBytes) {
        return LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation;
    }

    const std::uint16_t sid = ReadUInt16Little(bytes + 10);
    if (sid != LegacyZodiacSkillGridUpgradeSid) {
        return LegacyZodiacSkillGridUpgradePacketKind::Unrelated;
    }

    const std::uint16_t module = ReadUInt16Little(bytes + 8);
    const auto gridIndex = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 12));
    const auto placeholder = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 16));
    const auto trailing = static_cast<std::int32_t>(
        ReadUInt32Little(bytes + 20));
    if ((module != LegacyZodiacNativeModule &&
            module != LegacyZodiacCompatibilityModule) ||
        gridIndex < LegacyZodiacSkillGridMinimum ||
        gridIndex > LegacyZodiacSkillGridMaximum ||
        placeholder != -1 ||
        trailing != 0) {
        return LegacyZodiacSkillGridUpgradePacketKind::InvalidMutation;
    }

    if (command != nullptr) {
        command->gridIndex = gridIndex;
    }
    return LegacyZodiacSkillGridUpgradePacketKind::Commit;
}

bool TryReadLegacyZodiacSkillGridUpgrade(
    const void* packet,
    std::size_t packetBytes,
    LegacyZodiacSkillGridUpgradeCommand* command) noexcept {
    return command != nullptr &&
        ClassifyLegacyZodiacSkillGridUpgradePacket(
            packet,
            packetBytes,
            command) ==
            LegacyZodiacSkillGridUpgradePacketKind::Commit;
}

} // namespace godswar::network
