#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t NativeRouteHostMaximumBytes = 253;
inline constexpr std::size_t NativeRouteHostCapacity =
    NativeRouteHostMaximumBytes + 1;
inline constexpr std::size_t NativeClientRegistryCapacity = 64;

inline constexpr std::size_t NativeBridgeChunkMaximumBytes = 16 * 1024;
inline constexpr std::size_t NativeBridgeQueueMaximumItems = 128;
inline constexpr std::size_t NativeBridgeQueueMaximumBytes = 512 * 1024;
inline constexpr std::uint32_t NativeBridgeQueueAdmissionMilliseconds = 250;

} // namespace godswar::network
