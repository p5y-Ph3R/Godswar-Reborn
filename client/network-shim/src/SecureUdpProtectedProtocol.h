#pragma once

#include "SecureUdpBindingGrant.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecureUdpProtectedHeaderBytes = 64;
inline constexpr std::size_t SecureUdpProtectedTagBytes = 16;
inline constexpr std::size_t SecureUdpProtectedMinimumBytes = 80;
inline constexpr std::size_t SecureUdpProtectedMaximumBytes = 1'200;
inline constexpr std::size_t SecureUdpProtectedMaximumPayloadBytes = 1'120;

enum class SecureUdpDirection : std::uint8_t {
    ClientToServer = 1,
    ServerToClient = 2,
};

enum class SecureUdpProtectedMessageType : std::uint8_t {
    Ping = 1,
    Pong = 2,
    BindingConfirm = 3,
};

struct SecureUdpProtectedHeader final {
    std::uint32_t keyEpoch = 0;
    std::uint64_t sequence = 0;
    std::uint32_t acknowledgmentEpoch = 0;
    std::uint64_t acknowledgmentSequence = 0;
    std::uint64_t acknowledgmentMask = 0;
    SecureUdpProtectedMessageType messageType =
        SecureUdpProtectedMessageType::Ping;
    std::uint16_t payloadBytes = 0;
};

bool TryDeriveSecureUdpEpochKey(
    const std::uint8_t* proofKey,
    std::size_t proofKeyBytes,
    const std::uint8_t* connectionId,
    std::size_t connectionIdBytes,
    std::uint32_t serverId,
    SecureUdpDirection direction,
    std::uint32_t keyEpoch,
    std::uint8_t* destination,
    std::size_t destinationBytes) noexcept;

bool TrySealSecureUdpProtectedDatagram(
    const std::uint8_t* proofKey,
    std::size_t proofKeyBytes,
    const std::uint8_t* connectionId,
    std::size_t connectionIdBytes,
    std::uint32_t serverId,
    SecureUdpDirection direction,
    const SecureUdpProtectedHeader& header,
    const void* plaintext,
    std::size_t plaintextBytes,
    void* destination,
    std::size_t destinationBytes,
    std::size_t* bytesWritten) noexcept;

// Performs strict structural parsing only. Authentication state must never be
// committed from this result; it exists solely for cheap epoch/replay
// preflight before TryOpenSecureUdpProtectedDatagram.
bool TryInspectSecureUdpProtectedDatagram(
    const std::uint8_t* expectedConnectionId,
    std::size_t connectionIdBytes,
    const void* datagram,
    std::size_t datagramBytes,
    SecureUdpProtectedHeader* header) noexcept;

bool TryOpenSecureUdpProtectedDatagram(
    const std::uint8_t* proofKey,
    std::size_t proofKeyBytes,
    const std::uint8_t* expectedConnectionId,
    std::size_t connectionIdBytes,
    std::uint32_t serverId,
    SecureUdpDirection direction,
    const void* datagram,
    std::size_t datagramBytes,
    SecureUdpProtectedHeader* header,
    void* plaintext,
    std::size_t plaintextCapacity,
    std::size_t* plaintextBytes) noexcept;

} // namespace godswar::network
