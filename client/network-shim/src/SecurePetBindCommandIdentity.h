#pragma once

#include "SecurePetCommandIdentity.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

bool IsLegacyPetBindCandidate(
    const std::uint8_t* bytes,
    std::size_t packetBytes) noexcept;

LegacyPetCommandPacketKind ClassifyLegacyPetBindPacket(
    const std::uint8_t* bytes,
    std::size_t packetBytes,
    LegacyPetCommandIntent* intent) noexcept;

} // namespace godswar::network
