#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class SecureEndpointRole : std::uint8_t {
    Login = 1,
    Game = 2,
};

enum class SecureServerPrefaceStatus : std::uint8_t {
    Ok = 0,
    UnsupportedVersion = 1,
    WrongEndpoint = 2,
    UnsupportedBuild = 3,
    ServerBusy = 4,
    PolicyRejected = 5,
};

enum class SecureFrameDirection : std::uint8_t {
    ClientToServer = 0,
    ServerToClient = 1,
};

enum class SecureFrameType : std::uint16_t {
    Ping = 0x0001,
    Pong = 0x0002,
    Close = 0x0003,
    LegacyBytes = 0x0100,
    GameGrant = 0x0200,
    GameBind = 0x0201,
    BindResult = 0x0202,
};

struct SecureServerPrefaceView final {
    SecureServerPrefaceStatus status =
        SecureServerPrefaceStatus::PolicyRejected;
    SecureEndpointRole role = SecureEndpointRole::Login;
    std::uint8_t connectionId[16]{};
};

struct SecureFrameHeader final {
    std::uint32_t payloadBytes = 0;
    SecureFrameType type = SecureFrameType::Close;
    std::uint64_t sequence = 0;
};

inline constexpr std::size_t SecureClientPrefaceBytes = 72;
inline constexpr std::size_t SecureServerPrefaceBytes = 40;
inline constexpr std::size_t SecureFrameHeaderBytes = 16;
inline constexpr std::size_t SecureMaximumPayloadBytes = 16 * 1024;
inline constexpr std::size_t SecureGameGrantIdBytes = 16;
inline constexpr std::size_t SecureGameTicketBytes = 32;
inline constexpr std::size_t SecureGameGrantFixedBytes = 68;
inline constexpr std::size_t SecureGameGrantMinimumBytes = 71;
inline constexpr std::size_t SecureGameGrantMaximumBytes = 408;
inline constexpr std::size_t SecureGameBindBytes = 52;
inline constexpr std::size_t SecureBindResultBytes = 4;
inline constexpr std::uint16_t SecureProtocolMajor = 1;
inline constexpr std::uint16_t SecureProtocolMinor = 0;

bool TryEncodeSecureClientPreface(
    SecureEndpointRole role,
    const std::uint8_t* clientInstanceId,
    std::size_t clientInstanceIdBytes,
    const std::uint8_t* originSha256,
    std::size_t originSha256Bytes,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureServerPreface(
    const void* source,
    std::size_t sourceBytes,
    SecureEndpointRole expectedRole,
    SecureServerPrefaceView* preface) noexcept;

bool TryEncodeSecureFrameHeader(
    const SecureFrameHeader& header,
    SecureEndpointRole role,
    SecureFrameDirection direction,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureFrameHeader(
    const void* source,
    std::size_t sourceBytes,
    SecureEndpointRole role,
    SecureFrameDirection direction,
    std::uint64_t expectedSequence,
    SecureFrameHeader* header) noexcept;

bool TryGetNextSecureSequence(
    std::uint64_t current,
    std::uint64_t* next) noexcept;

} // namespace godswar::network
