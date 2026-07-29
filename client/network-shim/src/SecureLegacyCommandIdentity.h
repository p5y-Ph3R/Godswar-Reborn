#pragma once

#include "SecureClientProtocol.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint16_t LegacyLoginGameServerOpcode = 10000;
inline constexpr std::uint16_t LegacyNpcFunctionActionOpcode = 10069;
inline constexpr std::uint16_t LegacyGearSelectionOpcode = 10193;
inline constexpr std::uint16_t LegacyEnterMainOpcode = 0x2723;
inline constexpr std::uint16_t LegacyEnterMainPacketBytes = 0x0658;
inline constexpr std::uint16_t LegacyGearMentorActionPacketBytes = 92;
inline constexpr std::uint32_t LegacySpartaGearMentorNpc = 5067;
inline constexpr std::uint32_t LegacyAthensGearMentorNpc = 5209;
inline constexpr std::int32_t LegacyGearMentorDialog = 4;
inline constexpr std::int32_t LegacyMakeAttributeStoneSubId = 4;
inline constexpr std::int32_t LegacyTransformCrystalSubId = 8;
inline constexpr std::int32_t LegacyCombineGemPiecesSubId = 9;
inline constexpr std::size_t SecurePrincipalFingerprintBytes = 32;

enum class LegacyGearMentorAction : std::int32_t {
    InitialMenu = -1,
    DecomposeGear = 1,
    EnhanceAttribute = 2,
    AddAttribute = 3,
    MakeAttributeStone = LegacyMakeAttributeStoneSubId,
    Instructions = 5,
    DeleteAttribute = 6,
    WashDust = 7,
    TransformCrystal = LegacyTransformCrystalSubId,
    CombineGemPieces = LegacyCombineGemPiecesSubId,
};

struct LegacyPacketDescriptor final {
    std::uint16_t packetBytes = 0;
    std::uint16_t opcode = 0;
    bool hasOperation = false;
    SecureLegacyCommandOperation operation{};
};

bool TryReadLegacyPacketHeader(
    const void* packet,
    std::size_t packetBytes,
    std::uint16_t* opcode) noexcept;

bool TryHashLegacyLoginPrincipal(
    const void* packet,
    std::size_t packetBytes,
    std::uint8_t* fingerprint,
    std::size_t fingerprintBytes) noexcept;

bool TryReadLegacyGearSelection(
    const void* packet,
    std::size_t packetBytes,
    int* bagSlot,
    bool* selected) noexcept;

bool TryReadLegacyGearMentorAction(
    const void* packet,
    std::size_t packetBytes,
    LegacyGearMentorAction* action,
    std::uint32_t* npcId) noexcept;

// Stock receive messages have one x86 vtable pointer before the clear packet.
// EnterMain carries the persistent character key at clear-packet offset four.
bool TryReadLegacyEnterMainCharacterId(
    const void* message,
    int* characterId) noexcept;

} // namespace godswar::network
