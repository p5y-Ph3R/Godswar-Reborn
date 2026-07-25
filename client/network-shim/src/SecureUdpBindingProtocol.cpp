#include "SecureUdpBindingProtocol.h"

#include "SecureUdpCrypto.h"

#include <Windows.h>

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

constexpr std::uint8_t BindingMagic[] = {'G', 'W', 'S', 'U'};
constexpr std::uint16_t HeaderBytes = 48;
constexpr std::uint16_t PayloadBytes = 48;
constexpr std::uint8_t ProtocolMajor = 1;
constexpr std::uint8_t ProtocolMinor = 0;
constexpr std::size_t ConnectionIdOffset = 12;
constexpr std::size_t KeyEpochOffset = 28;
constexpr std::size_t SequenceOffset = 32;
constexpr std::size_t PayloadLengthOffset = 40;
constexpr std::size_t ReservedOffset = 42;
constexpr std::size_t ClientNonceOffset = 48;
constexpr std::size_t IssuedAtOffset = 64;
constexpr std::size_t TlsProofOffset = 72;
constexpr std::size_t CookieOffset = 96;
constexpr std::uint8_t TlsProofDomain[] =
    "GWSU-TLS-BIND-PROOF-V1";

std::uint16_t ReadUInt16(const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(source[0]) << 8U) |
        source[1]);
}

std::uint32_t ReadUInt32(const std::uint8_t* source) noexcept {
    return
        (static_cast<std::uint32_t>(source[0]) << 24U) |
        (static_cast<std::uint32_t>(source[1]) << 16U) |
        (static_cast<std::uint32_t>(source[2]) << 8U) |
        source[3];
}

std::uint64_t ReadUInt64(const std::uint8_t* source) noexcept {
    std::uint64_t value = 0;
    for (std::size_t index = 0; index < 8; ++index) {
        value = (value << 8U) | source[index];
    }
    return value;
}

void WriteUInt16(
    std::uint8_t* destination,
    std::uint16_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 8U);
    destination[1] = static_cast<std::uint8_t>(value);
}

bool IsAllZero(
    const std::uint8_t* input,
    std::size_t inputBytes) noexcept {
    if (input == nullptr || inputBytes == 0) {
        return true;
    }
    std::uint8_t combined = 0;
    for (std::size_t index = 0; index < inputBytes; ++index) {
        combined |= input[index];
    }
    return combined == 0;
}

bool Exact(
    const std::uint8_t* left,
    const std::uint8_t* right,
    std::size_t bytes) noexcept {
    if (left == nullptr || right == nullptr || bytes == 0) {
        return false;
    }
    std::uint8_t difference = 0;
    for (std::size_t index = 0; index < bytes; ++index) {
        difference |= static_cast<std::uint8_t>(
            left[index] ^ right[index]);
    }
    return difference == 0;
}

bool IsKnownType(SecureUdpBindingPacketType type) noexcept {
    return type == SecureUdpBindingPacketType::ClientHello ||
        type == SecureUdpBindingPacketType::ServerChallenge ||
        type == SecureUdpBindingPacketType::ClientProof ||
        type ==
            SecureUdpBindingPacketType::AuthenticatedClientProof;
}

} // namespace

bool TryDecodeSecureUdpBindingPacket(
    const void* source,
    std::size_t sourceBytes,
    SecureUdpBindingPacket* packet) noexcept {
    if (packet == nullptr) {
        return false;
    }
    *packet = SecureUdpBindingPacket{};
    if (source == nullptr ||
        sourceBytes != SecureUdpBindingDatagramBytes) {
        return false;
    }
    const auto* input = static_cast<const std::uint8_t*>(source);
    const auto type =
        static_cast<SecureUdpBindingPacketType>(input[8]);
    if (std::memcmp(input, BindingMagic, sizeof(BindingMagic)) != 0 ||
        ReadUInt16(input + 4) != HeaderBytes ||
        input[6] != ProtocolMajor ||
        input[7] != ProtocolMinor ||
        !IsKnownType(type) ||
        input[9] != 0 ||
        ReadUInt16(input + 10) != SecureUdpBindingDatagramBytes ||
        ReadUInt16(input + PayloadLengthOffset) != PayloadBytes ||
        !IsAllZero(input + ReservedOffset, 6) ||
        IsAllZero(
            input + ConnectionIdOffset,
            SecureUdpConnectionIdBytes) ||
        IsAllZero(
            input + ClientNonceOffset,
            SecureUdpClientNonceBytes)) {
        return false;
    }

    const auto keyEpoch = ReadUInt32(input + KeyEpochOffset);
    const auto sequence = ReadUInt64(input + SequenceOffset);
    const auto issuedAt = ReadUInt64(input + IssuedAtOffset);
    const bool hasTlsProof = !IsAllZero(
        input + TlsProofOffset,
        SecureUdpTlsProofTagBytes);
    const bool hasCookie = !IsAllZero(
        input + CookieOffset,
        SecureUdpCookieTagBytes);
    if (sequence != 0 ||
        (type == SecureUdpBindingPacketType::ClientHello
            ? keyEpoch != 0 || issuedAt != 0 ||
                hasTlsProof || hasCookie
            : keyEpoch == 0 || issuedAt == 0 ||
                issuedAt >
                    static_cast<std::uint64_t>(
                        (std::numeric_limits<std::int64_t>::max)()) ||
                !hasCookie)) {
        return false;
    }
    if (type ==
            SecureUdpBindingPacketType::AuthenticatedClientProof
        ? !hasTlsProof
        : hasTlsProof) {
        return false;
    }

    packet->type = type;
    packet->keyEpoch = keyEpoch;
    packet->sequence = sequence;
    packet->issuedAtUnixSeconds = issuedAt;
    std::memcpy(
        packet->connectionId,
        input + ConnectionIdOffset,
        sizeof(packet->connectionId));
    std::memcpy(
        packet->clientNonce,
        input + ClientNonceOffset,
        sizeof(packet->clientNonce));
    std::memcpy(
        packet->tlsProofTag,
        input + TlsProofOffset,
        sizeof(packet->tlsProofTag));
    std::memcpy(
        packet->cookieTag,
        input + CookieOffset,
        sizeof(packet->cookieTag));
    return true;
}

bool TryEncodeSecureUdpClientHello(
    const std::uint8_t* connectionId,
    std::size_t connectionIdBytes,
    const std::uint8_t* clientNonce,
    std::size_t clientNonceBytes,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (connectionId == nullptr ||
        connectionIdBytes != SecureUdpConnectionIdBytes ||
        IsAllZero(connectionId, connectionIdBytes) ||
        clientNonce == nullptr ||
        clientNonceBytes != SecureUdpClientNonceBytes ||
        IsAllZero(clientNonce, clientNonceBytes) ||
        destination == nullptr ||
        destinationBytes < SecureUdpBindingDatagramBytes) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    SecureZeroMemory(output, SecureUdpBindingDatagramBytes);
    std::memcpy(output, BindingMagic, sizeof(BindingMagic));
    WriteUInt16(output + 4, HeaderBytes);
    output[6] = ProtocolMajor;
    output[7] = ProtocolMinor;
    output[8] = static_cast<std::uint8_t>(
        SecureUdpBindingPacketType::ClientHello);
    WriteUInt16(
        output + 10,
        static_cast<std::uint16_t>(
            SecureUdpBindingDatagramBytes));
    std::memcpy(
        output + ConnectionIdOffset,
        connectionId,
        connectionIdBytes);
    WriteUInt16(output + PayloadLengthOffset, PayloadBytes);
    std::memcpy(
        output + ClientNonceOffset,
        clientNonce,
        clientNonceBytes);
    return true;
}

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
    std::size_t destinationBytes) noexcept {
    if (serverChallenge == nullptr ||
        serverChallengeBytes != SecureUdpBindingDatagramBytes ||
        expectedConnectionId == nullptr ||
        connectionIdBytes != SecureUdpConnectionIdBytes ||
        expectedClientNonce == nullptr ||
        clientNonceBytes != SecureUdpClientNonceBytes ||
        proofKey == nullptr ||
        proofKeyBytes != SecureUdpProofKeyBytes ||
        IsAllZero(proofKey, proofKeyBytes) ||
        destination == nullptr ||
        destinationBytes < SecureUdpBindingDatagramBytes) {
        return false;
    }

    SecureUdpBindingPacket challenge{};
    if (!TryDecodeSecureUdpBindingPacket(
            serverChallenge,
            serverChallengeBytes,
            &challenge) ||
        challenge.type !=
            SecureUdpBindingPacketType::ServerChallenge ||
        !Exact(
            challenge.connectionId,
            expectedConnectionId,
            connectionIdBytes) ||
        !Exact(
            challenge.clientNonce,
            expectedClientNonce,
            clientNonceBytes)) {
        return false;
    }

    std::uint8_t authenticated[
        sizeof(TlsProofDomain) - 1 +
        SecureUdpBindingDatagramBytes]{};
    std::uint8_t hash[SecureUdpSha256Bytes]{};
    std::memcpy(
        authenticated,
        TlsProofDomain,
        sizeof(TlsProofDomain) - 1);
    std::memcpy(
        authenticated + sizeof(TlsProofDomain) - 1,
        serverChallenge,
        serverChallengeBytes);
    const bool computed = SecureUdpHmacSha256(
        proofKey,
        proofKeyBytes,
        authenticated,
        sizeof(authenticated),
        hash,
        sizeof(hash));
    SecureZeroMemory(authenticated, sizeof(authenticated));
    if (!computed) {
        SecureZeroMemory(hash, sizeof(hash));
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::memcpy(
        output,
        serverChallenge,
        SecureUdpBindingDatagramBytes);
    output[8] = static_cast<std::uint8_t>(
        SecureUdpBindingPacketType::AuthenticatedClientProof);
    std::memcpy(
        output + TlsProofOffset,
        hash,
        SecureUdpTlsProofTagBytes);
    SecureZeroMemory(hash, sizeof(hash));
    return true;
}

} // namespace godswar::network
