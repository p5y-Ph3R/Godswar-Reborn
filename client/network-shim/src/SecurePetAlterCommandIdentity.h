#pragma once

#include "SecurePetCommandIdentity.h"

namespace godswar::network {

bool IsLegacyPetAlterOpcode(std::uint16_t opcode) noexcept;

LegacyPetCommandPacketKind ClassifyLegacyPetAlterPacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept;

} // namespace godswar::network
