#pragma once

#include "EndpointManifest.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

bool VerifyEndpointManifestSignature(
    const std::uint8_t* signedBytes,
    std::size_t signedByteCount,
    const std::uint8_t* signature,
    const EndpointManifestPublicKey& publicKey) noexcept;

} // namespace godswar::network
