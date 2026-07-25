#pragma once

#include "EndpointManifest.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

#pragma pack(push, 1)
struct SecureClientManifestBuildContract final {
    char magic[8]{};
    std::uint16_t version = 0;
    std::uint16_t structureBytes = 0;
    std::uint8_t environment = 0;
    std::uint8_t reserved[3]{};
    std::uint16_t currentKeyId = 0;
    std::uint16_t nextKeyId = 0;
    std::uint64_t compiledMinimumSequence = 0;
    EndpointManifestPublicKey currentKey{};
    EndpointManifestPublicKey nextKey{};
};
#pragma pack(pop)

static_assert(
    sizeof(SecureClientManifestBuildContract) == 156,
    "manifest build contract layout changed");

const SecureClientManifestBuildContract&
GetSecureClientManifestBuildContract() noexcept;

bool IsValidSecureClientManifestBuildContract(
    const SecureClientManifestBuildContract& contract) noexcept;

// Reads the immutable `.gwkey` section from an x86 candidate without loading
// or executing it. Every offset/count is bounded by the pinned file size.
bool ReadSecureClientManifestBuildContract(
    const wchar_t* candidatePath,
    SecureClientManifestBuildContract* contract) noexcept;

} // namespace godswar::network
