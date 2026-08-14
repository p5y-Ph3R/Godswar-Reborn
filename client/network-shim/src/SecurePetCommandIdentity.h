#pragma once

#include "SecureClientProtocol.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyBagItemActivationOpcode = 10051;
inline constexpr std::uint16_t LegacyPetTakeOpcode = 10239;
inline constexpr std::uint16_t LegacyPetCallOutOpcode = 10240;
inline constexpr std::uint16_t LegacyPetRecallOpcode = 10241;
inline constexpr std::uint16_t LegacyPetToPetMergeOpcode = 10268;
inline constexpr std::uint16_t LegacyPetSoulContractOpcode = 10270;
inline constexpr std::uint16_t LegacyPetRebirthOpcode = 10272;
inline constexpr std::uint16_t LegacyPetOwnerMergeOpcode = 10274;
inline constexpr std::uint16_t LegacyPetLevelUpgradeOpcode = 10285;
inline constexpr std::uint32_t LegacySpartaPetManagerNpc = 5085;
inline constexpr std::uint32_t LegacySpartaSourcePetManagerNpc = 5087;
inline constexpr std::uint32_t LegacyAthensPetManagerNpc = 5227;
inline constexpr std::int32_t LegacyPetManagerDialog = 31;
inline constexpr std::int32_t LegacyPetPointResetDialog = 36;
inline constexpr std::int32_t LegacyPetBindMenuSubId = 7;
inline constexpr std::int32_t LegacyPetBindActionSubId = 112;
inline constexpr std::uint32_t LegacyPetBindAlreadyBoundResultSubId = 1072;
inline constexpr std::uint32_t LegacyPetBindSucceededResultSubId = 1073;
inline constexpr std::uint32_t LegacyPetBindNoPetResultSubId = 1075;
inline constexpr std::int32_t LegacyPetAppearanceChangeSubId = 8;
inline constexpr std::int32_t LegacyPetAppearanceDescriptionSubId = 113;
inline constexpr std::int32_t LegacyPetAppearanceConfirmationArgument = 0;
inline constexpr std::size_t LegacyPetAppearanceItemArgumentIndex = 6;
inline constexpr std::size_t LegacyPetManagerScratchArgumentFirst = 10;
inline constexpr std::size_t LegacyPetManagerScratchArgumentCount = 3;
inline constexpr std::uint32_t LegacyPetAppearanceSucceededResultSubId = 130;
inline constexpr std::uint32_t LegacyPetAppearanceMissingJadeResultSubId = 137;
inline constexpr std::uint32_t LegacyPetAppearanceIncompatibleJadeResultSubId = 138;
inline constexpr std::uint32_t LegacyPetAppearanceNoPetResultSubId = 139;
inline constexpr std::uint32_t LegacyPetAppearanceUnboundPetResultSubId = 140;
inline constexpr std::int32_t LegacyPetSkillUnlearnMenuSubId = 6;
inline constexpr std::int32_t LegacyPetSkillUnlearnFirstSubId = 106;
inline constexpr std::int32_t LegacyPetSkillUnlearnFirstRangeLastSubId = 111;
inline constexpr std::int32_t LegacyPetSkillUnlearnSecondRangeFirstSubId = 114;
inline constexpr std::int32_t LegacyPetSkillUnlearnLastSubId = 119;
inline constexpr std::uint32_t LegacyPetSkillUnlearnNoPetResultSubId = 1011;
inline constexpr std::uint32_t LegacyPetSkillUnlearnNoPotionResultSubId = 1061;
inline constexpr std::uint32_t LegacyPetSkillUnlearnEmptySlotResultSubId = 1062;
inline constexpr std::uint32_t LegacyPetSkillUnlearnSucceededResultSubId = 1063;
inline constexpr std::int32_t LegacyPetGrowthResetMenuSubId = 101;
inline constexpr std::int32_t LegacyPetGrowthResetActionSubId = 117;
inline constexpr std::uint32_t LegacyPetGrowthResetNoFeatherResultSubId = 127;
inline constexpr std::uint32_t LegacyPetGrowthResetNoPetResultSubId = 128;
inline constexpr std::uint32_t LegacyPetGrowthResetNoPreviewResultSubId = 129;
inline constexpr std::uint32_t LegacyPetGrowthResetSucceededResultSubId = 130;
inline constexpr std::int32_t LegacyPetBasicSavvyResetMenuSubId = 100;
inline constexpr std::int32_t LegacyPetBasicSavvyResetActionSubId = 116;
inline constexpr std::uint32_t LegacyPetBasicSavvyResetLegacyNoPetResultSubId = 118;
inline constexpr std::uint32_t LegacyPetBasicSavvyResetLegacyNoFeatherResultSubId = 119;
inline constexpr std::uint32_t LegacyPetBasicSavvyResetNoFeatherResultSubId = 127;
inline constexpr std::uint32_t LegacyPetBasicSavvyResetNoPetResultSubId = 128;
inline constexpr std::uint32_t LegacyPetBasicSavvyResetNoPreviewResultSubId = 129;
inline constexpr std::uint32_t LegacyPetBasicSavvyResetSucceededResultSubId = 120;
inline constexpr std::size_t LegacyBagItemActivationPacketBytes = 92;
inline constexpr std::size_t LegacyPetOwnerMergePacketBytes = 4;
inline constexpr std::size_t LegacyPetToPetMergePacketBytes = 20;
inline constexpr std::size_t LegacyPetSoulContractPacketBytes = 12;
inline constexpr std::size_t LegacyPetRebirthPacketBytes = 12;
inline constexpr std::size_t LegacyPetCommandPacketBytes = 8;
inline constexpr std::size_t LegacyPetManagerActionPacketBytes = 92;
inline constexpr std::size_t LegacyPetManagerArgumentCount = 18;
inline constexpr std::uint32_t LegacyPetBagPageCount = 4;
inline constexpr std::uint32_t LegacyPetBagSlotsPerPage = 24;
inline constexpr std::size_t SecurePetCommandIntentBytes = 16;
inline constexpr std::uint32_t LegacyMergedSpiritItemId = 10103;
inline constexpr std::uint32_t LegacyFusedHarpyiaItemId = 10097;
inline constexpr std::uint8_t LegacyMaximumPetMergeMaterialQuantity = 5;
inline constexpr std::uint32_t LegacyRebirthSpiritItemId = 10104;
inline constexpr std::uint32_t LegacyRebornHarpyiaItemId = 10098;
inline constexpr std::uint32_t LegacyContractSpiritItemId = 10105;
inline constexpr std::uint8_t LegacyMaximumPetAlterMaterialQuantity = 5;

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
