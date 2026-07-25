#pragma once

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

bool FileHandleMatchesSha256(
    HANDLE file,
    const std::uint8_t* expectedHash,
    std::size_t expectedHashSize) noexcept;

bool FileMatchesSha256(
    const wchar_t* path,
    const std::uint8_t* expectedHash,
    std::size_t expectedHashSize) noexcept;

} // namespace godswar::network
