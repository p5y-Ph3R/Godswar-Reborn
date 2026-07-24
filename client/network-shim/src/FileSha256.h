#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

bool FileMatchesSha256(
    const wchar_t* path,
    const std::uint8_t* expectedHash,
    std::size_t expectedHashSize) noexcept;

} // namespace godswar::network
