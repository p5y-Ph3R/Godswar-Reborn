#pragma once

#include "SecurePetCommandIdentity.h"

namespace godswar::network {

inline constexpr std::int32_t LegacyPetGrowthCheckMenuSubId = 4;
inline constexpr std::int32_t LegacyPetGrowthCheckActionSubId = 104;
inline constexpr std::int32_t LegacyPetSealMenuSubId = 5;
inline constexpr std::int32_t LegacyPetSealActionSubId = 105;
inline constexpr std::int32_t LegacyPetCallClaimSubId = 9;
inline constexpr std::int32_t LegacyPetMergeClaimSubId = 10;
inline constexpr std::int32_t LegacyPetGenderMenuSubId = 11;
inline constexpr std::int32_t LegacyPetGenderConfirmArgument = 0;
inline constexpr std::uint32_t LegacyPetGrowthCheckedResult = 1071;
inline constexpr std::uint32_t LegacyPetGrowthNoPetResult = 1011;
inline constexpr std::uint32_t LegacyPetGrowthNoTearResult = 1041;
inline constexpr std::uint32_t LegacyPetSealSucceededResult = 1053;
inline constexpr std::uint32_t LegacyPetSealNoJadeResult = 1051;
inline constexpr std::uint32_t LegacyPetSealBagFullResult = 1052;
inline constexpr std::uint32_t LegacyPetSealBoundResult = 1072;
inline constexpr std::uint32_t LegacyPetCharmBagFullResult = 10000;
inline constexpr std::uint32_t LegacyPetCharmHeldResult = 10001;
inline constexpr std::uint32_t LegacyPetCallClaimedResult = 10002;
inline constexpr std::uint32_t LegacyPetMergeClaimedResult = 10003;
inline constexpr std::uint32_t LegacyPetGenderUnboundResult = 150;
inline constexpr std::uint32_t LegacyPetGenderNoPetResult = 160;
inline constexpr std::uint32_t LegacyPetGenderUnavailableResult = 161;
inline constexpr std::uint32_t LegacyPetGenderNoItemResult = 210;
inline constexpr std::uint32_t LegacyPetGenderMaleResult = 220;
inline constexpr std::uint32_t LegacyPetGenderFemaleResult = 230;
inline constexpr std::uint32_t LegacyPetUnsealedResult = 82;
inline constexpr std::uint32_t LegacyPetUnsealUnavailableResult = 87;
inline constexpr std::uint32_t LegacyPetUnsealLinkInvalidResult = 91;
inline constexpr std::uint32_t LegacyPetUnsealMalformedResult = 94;
inline constexpr std::uint32_t LegacyPetUnsealConflictResult = 95;

enum class LegacyPetManagerUtilityOperation : std::uint8_t {
    CheckGrowth = 1,
    Seal = 2,
    Unseal = 3,
    ClaimPetCall = 4,
    ClaimMerge = 5,
    ChangeGender = 6,
};

bool IsLegacyPetManagerUtilityCandidate(
    const void* packet,
    std::size_t packetBytes) noexcept;

LegacyPetCommandPacketKind ClassifyLegacyPetManagerUtilityPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept;

} // namespace godswar::network
