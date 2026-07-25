#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

bool GenerateSystemSecureRandom(
    void* destination,
    std::size_t destinationBytes) noexcept;

bool ReadSystemUnixMilliseconds(
    std::uint64_t* unixMilliseconds) noexcept;

} // namespace godswar::network
