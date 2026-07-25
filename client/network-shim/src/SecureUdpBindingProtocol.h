#pragma once

#include "SecureUdpBindingGrant.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecureUdpBindingDatagramBytes = 128;
inline constexpr std::size_t SecureUdpClientNonceBytes = 16;
inline constexpr std::size_t SecureUdpTlsProofTagBytes = 24;
inline constexpr std::size_t SecureUdpCookieTagBytes = 32;

enum class SecureUdpBindingPacketType : std::uint8_t {
    ClientHello = 1,
    ServerChallenge = 2,
    ClientProof = 3,
    AuthenticatedClientProof = 4,
};

struct SecureUdpBindingPacket final {
    SecureUdpBindingPacketType type =
        SecureUdpBindingPacketType::ClientHello;
    std::uint8_t connectionId[SecureUdpConnectionIdBytes]{};
    std::uint32_t keyEpoch = 0;
    std::uint64_t sequence = 0;
    std::uint8_t clientNonce[SecureUdpClientNonceBytes]{};
    std::uint64_t issuedAtUnixSeconds = 0;
    std::uint8_t tlsProofTag[SecureUdpTlsProofTagBytes]{};
    std::uint8_t cookieTag[SecureUdpCookieTagBytes]{};
};

bool TryDecodeSecureUdpBindingPacket(
    const void* source,
    std::size_t sourceBytes,
    SecureUdpBindingPacket* packet) noexcept;

bool TryEncodeSecureUdpClientHello(
    const std::uint8_t* connectionId,
    std::size_t connectionIdBytes,
    const std::uint8_t* clientNonce,
    std::size_t clientNonceBytes,
    void* destination,
    std::size_t destinationBytes) noexcept;

// Copies the exact server challenge into a type-4 proof and authenticates the
// byte-exact challenge with the TLS-delivered proof key.
bool TryEncodeSecureUdpAuthenticatedProof(
    const void* serverChallenge,
    std::size_t serverChallengeBytes,
    const std::uint8_t* expectedConnectionId,
    std::size_t connectionIdBytes,
    const std::uint8_t* expectedClientNonce,
    std::size_t clientNonceBytes,
    const std::uint8_t* proofKey,
    std::size_t proofKeyBytes,
    void* destination,
    std::size_t destinationBytes) noexcept;

} // namespace godswar::network
