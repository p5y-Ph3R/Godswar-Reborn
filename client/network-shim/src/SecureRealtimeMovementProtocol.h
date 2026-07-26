#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::uint8_t SecureRealtimeMovementVersion = 1;
inline constexpr std::size_t SecureRealtimeLegacyMovementBytes = 20;
inline constexpr std::uint16_t SecureRealtimeLegacyMovementOpcode = 10194;
inline constexpr std::size_t SecureRealtimeMovementInputBytes = 52;
inline constexpr std::size_t SecureRealtimePositionSnapshotBytes = 64;

enum class SecureRealtimeMovementInputFlag : std::uint8_t {
    None = 0,
    CurrentWorld = 1,
};

enum class SecureRealtimePositionSnapshotFlag : std::uint8_t {
    None = 0,
    Keyframe = 1,
    Correction = 2,
};

enum class SecureRealtimeMovementRejection : std::uint8_t {
    None = 0,
    Malformed = 1,
    NotReady = 2,
    Dead = 3,
    InvalidCoordinates = 4,
    MapTransition = 5,
    Cadence = 6,
    Speed = 7,
    Distance = 8,
    StaleInput = 9,
    TransportEpoch = 10,
    TransportSource = 11,
    Overloaded = 12,
};

enum class SecureRealtimeMovementSource : std::uint8_t {
    Udp = 1,
    TlsFallback = 2,
};

struct SecureRealtimeLegacyMovement final {
    std::uint32_t legacyState = 0;
    float x = 0.0F;
    float z = 0.0F;
    float auxiliary = 0.0F;
};

struct SecureRealtimeMovementInput final {
    std::uint8_t flags = 0;
    std::uint32_t transportEpoch = 0;
    std::uint64_t inputId = 0;
    std::uint64_t clientMonotonicMilliseconds = 0;
    std::uint32_t worldGeneration = 0;
    std::uint32_t legacyState = 0;
    float x = 0.0F;
    float z = 0.0F;
    float auxiliary = 0.0F;
    std::uint8_t mapId = 0;
};

struct SecureRealtimePositionSnapshot final {
    std::uint8_t flags = 0;
    std::uint32_t transportEpoch = 0;
    std::uint64_t acknowledgedInputId = 0;
    std::uint64_t serverTick = 0;
    std::uint64_t revision = 0;
    std::uint64_t snapshotSequence = 0;
    std::uint32_t worldGeneration = 0;
    std::uint32_t legacyState = 0;
    float x = 0.0F;
    float z = 0.0F;
    float auxiliary = 0.0F;
    std::uint8_t mapId = 0;
    SecureRealtimeMovementRejection rejection =
        SecureRealtimeMovementRejection::None;
};

bool TryParseSecureRealtimeLegacyMovement(
    const void* source,
    std::size_t sourceBytes,
    SecureRealtimeLegacyMovement* movement) noexcept;

bool TryEncodeSecureRealtimeMovementInput(
    const SecureRealtimeMovementInput& movement,
    SecureRealtimeMovementSource source,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureRealtimeMovementInput(
    const void* source,
    std::size_t sourceBytes,
    SecureRealtimeMovementSource transport,
    SecureRealtimeMovementInput* movement) noexcept;

bool TryEncodeSecureRealtimePositionSnapshot(
    const SecureRealtimePositionSnapshot& snapshot,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureRealtimePositionSnapshot(
    const void* source,
    std::size_t sourceBytes,
    SecureRealtimePositionSnapshot* snapshot) noexcept;

} // namespace godswar::network
