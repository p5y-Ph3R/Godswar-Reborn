#include "SecureClientProtocol.h"

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

constexpr std::uint8_t ClientMagic[] = {'G', 'W', 'S', 'C'};
constexpr std::uint8_t ServerMagic[] = {'G', 'W', 'S', 'S'};

bool IsRole(SecureEndpointRole role) noexcept {
    return role == SecureEndpointRole::Login ||
        role == SecureEndpointRole::Game;
}

bool IsDirection(SecureFrameDirection direction) noexcept {
    return direction == SecureFrameDirection::ClientToServer ||
        direction == SecureFrameDirection::ServerToClient;
}

bool IsAllZero(
    const std::uint8_t* bytes,
    std::size_t count) noexcept {
    if (bytes == nullptr) {
        return true;
    }

    std::uint8_t combined = 0;
    for (std::size_t index = 0; index < count; ++index) {
        combined |= bytes[index];
    }
    return combined == 0;
}

void WriteUInt16(
    std::uint8_t* destination,
    std::uint16_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 8U);
    destination[1] = static_cast<std::uint8_t>(value);
}

void WriteUInt32(
    std::uint8_t* destination,
    std::uint32_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 24U);
    destination[1] = static_cast<std::uint8_t>(value >> 16U);
    destination[2] = static_cast<std::uint8_t>(value >> 8U);
    destination[3] = static_cast<std::uint8_t>(value);
}

void WriteUInt64(
    std::uint8_t* destination,
    std::uint64_t value) noexcept {
    for (std::size_t index = 0; index < 8; ++index) {
        destination[index] = static_cast<std::uint8_t>(
            value >> ((7U - index) * 8U));
    }
}

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
    std::uint64_t result = 0;
    for (std::size_t index = 0; index < 8; ++index) {
        result = (result << 8U) | source[index];
    }
    return result;
}

bool IsPayloadValid(
    SecureFrameType type,
    SecureEndpointRole role,
    SecureFrameDirection direction,
    std::uint32_t payloadBytes) noexcept {
    switch (type) {
        case SecureFrameType::Ping:
            return direction == SecureFrameDirection::ServerToClient &&
                payloadBytes == 8;
        case SecureFrameType::Pong:
            return direction == SecureFrameDirection::ClientToServer &&
                payloadBytes == 8;
        case SecureFrameType::Close:
            return payloadBytes == 4;
        case SecureFrameType::LegacyBytes:
            return payloadBytes >= 1 &&
                payloadBytes <= SecureMaximumPayloadBytes;
        case SecureFrameType::GameGrant:
            return role == SecureEndpointRole::Login &&
                direction == SecureFrameDirection::ServerToClient &&
                payloadBytes >= SecureGameGrantMinimumBytes &&
                payloadBytes <= SecureGameGrantMaximumBytes;
        case SecureFrameType::GameBind:
            return role == SecureEndpointRole::Game &&
                direction == SecureFrameDirection::ClientToServer &&
                payloadBytes == SecureGameBindBytes;
        case SecureFrameType::BindResult:
            return role == SecureEndpointRole::Game &&
                direction == SecureFrameDirection::ServerToClient &&
                payloadBytes == SecureBindResultBytes;
        case SecureFrameType::UdpBindingGrant:
            return role == SecureEndpointRole::Game &&
                direction == SecureFrameDirection::ServerToClient &&
                payloadBytes == SecureUdpBindingGrantPayloadBytes;
        case SecureFrameType::RealtimeMovementInput:
            return role == SecureEndpointRole::Game &&
                direction == SecureFrameDirection::ClientToServer &&
                payloadBytes ==
                    SecureRealtimeMovementInputPayloadBytes;
    }
    return false;
}

} // namespace

bool TryEncodeSecureClientPreface(
    SecureEndpointRole role,
    const std::uint8_t* clientInstanceId,
    std::size_t clientInstanceIdBytes,
    const std::uint8_t* originSha256,
    std::size_t originSha256Bytes,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (!IsRole(role) ||
        clientInstanceId == nullptr ||
        clientInstanceIdBytes != 16 ||
        IsAllZero(clientInstanceId, clientInstanceIdBytes) ||
        originSha256 == nullptr ||
        originSha256Bytes != 32 ||
        destination == nullptr ||
        destinationBytes < SecureClientPrefaceBytes) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::memset(output, 0, SecureClientPrefaceBytes);
    std::memcpy(output, ClientMagic, sizeof(ClientMagic));
    WriteUInt16(output + 4, SecureClientPrefaceBytes);
    WriteUInt16(output + 6, SecureProtocolMajor);
    WriteUInt16(output + 8, SecureProtocolMinor);
    WriteUInt16(output + 10, SecureProtocolMinor);
    output[12] = static_cast<std::uint8_t>(role);
    WriteUInt32(
        output + 20,
        static_cast<std::uint32_t>(SecureMaximumPayloadBytes));
    std::memcpy(output + 24, clientInstanceId, 16);
    std::memcpy(output + 40, originSha256, 32);
    return true;
}

bool TryDecodeSecureServerPreface(
    const void* source,
    std::size_t sourceBytes,
    SecureEndpointRole expectedRole,
    SecureServerPrefaceView* preface) noexcept {
    if (source == nullptr ||
        sourceBytes != SecureServerPrefaceBytes ||
        !IsRole(expectedRole) ||
        preface == nullptr) {
        return false;
    }

    const auto* input = static_cast<const std::uint8_t*>(source);
    const auto status =
        static_cast<SecureServerPrefaceStatus>(input[10]);
    if (std::memcmp(input, ServerMagic, sizeof(ServerMagic)) != 0 ||
        ReadUInt16(input + 4) != SecureServerPrefaceBytes ||
        ReadUInt16(input + 6) != SecureProtocolMajor ||
        ReadUInt16(input + 8) != SecureProtocolMinor ||
        status > SecureServerPrefaceStatus::PolicyRejected ||
        input[11] != static_cast<std::uint8_t>(expectedRole) ||
        ReadUInt32(input + 12) != 0 ||
        ReadUInt32(input + 16) != SecureMaximumPayloadBytes ||
        ReadUInt16(input + 20) != 30 ||
        ReadUInt16(input + 22) != 90) {
        return false;
    }

    const bool connectionIdIsZero = IsAllZero(input + 24, 16);
    if ((status == SecureServerPrefaceStatus::Ok &&
            connectionIdIsZero) ||
        (status != SecureServerPrefaceStatus::Ok &&
            !connectionIdIsZero)) {
        return false;
    }

    SecureServerPrefaceView decoded{};
    decoded.status = status;
    decoded.role = expectedRole;
    std::memcpy(decoded.connectionId, input + 24, 16);
    *preface = decoded;
    return true;
}

bool TryEncodeSecureFrameHeader(
    const SecureFrameHeader& header,
    SecureEndpointRole role,
    SecureFrameDirection direction,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (!IsRole(role) ||
        !IsDirection(direction) ||
        header.sequence == 0 ||
        destination == nullptr ||
        destinationBytes < SecureFrameHeaderBytes ||
        !IsPayloadValid(
            header.type,
            role,
            direction,
            header.payloadBytes)) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::memset(output, 0, SecureFrameHeaderBytes);
    WriteUInt32(output, header.payloadBytes);
    WriteUInt16(output + 4, static_cast<std::uint16_t>(header.type));
    WriteUInt64(output + 8, header.sequence);
    return true;
}

bool TryDecodeSecureFrameHeader(
    const void* source,
    std::size_t sourceBytes,
    SecureEndpointRole role,
    SecureFrameDirection direction,
    std::uint64_t expectedSequence,
    SecureFrameHeader* header) noexcept {
    if (source == nullptr ||
        sourceBytes != SecureFrameHeaderBytes ||
        !IsRole(role) ||
        !IsDirection(direction) ||
        expectedSequence == 0 ||
        header == nullptr) {
        return false;
    }

    const auto* input = static_cast<const std::uint8_t*>(source);
    SecureFrameHeader decoded{};
    decoded.payloadBytes = ReadUInt32(input);
    decoded.type =
        static_cast<SecureFrameType>(ReadUInt16(input + 4));
    decoded.sequence = ReadUInt64(input + 8);
    if (ReadUInt16(input + 6) != 0 ||
        decoded.sequence != expectedSequence ||
        decoded.payloadBytes > SecureMaximumPayloadBytes ||
        !IsPayloadValid(
            decoded.type,
            role,
            direction,
            decoded.payloadBytes)) {
        return false;
    }

    *header = decoded;
    return true;
}

bool TryGetNextSecureSequence(
    std::uint64_t current,
    std::uint64_t* next) noexcept {
    if (next == nullptr ||
        current == 0 ||
        current == (std::numeric_limits<std::uint64_t>::max)()) {
        return false;
    }

    *next = current + 1;
    return true;
}

} // namespace godswar::network
