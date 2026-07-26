#include "SecureUdpProtectedProtocol.h"

#include "SecureRealtimeMovementProtocol.h"
#include "SecureUdpCrypto.h"

#include <Windows.h>

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

constexpr std::uint8_t ProtectedMagic[] = {'G', 'W', 'S', 'P'};
constexpr std::uint8_t ProtocolMajor = 1;
constexpr std::uint8_t ProtocolMinor = 0;
constexpr std::uint8_t PacketType = 1;
constexpr std::size_t ConnectionIdOffset = 12;
constexpr std::size_t KeyEpochOffset = 28;
constexpr std::size_t SequenceOffset = 32;
constexpr std::size_t AcknowledgmentEpochOffset = 40;
constexpr std::size_t AcknowledgmentSequenceOffset = 44;
constexpr std::size_t AcknowledgmentMaskOffset = 52;
constexpr std::size_t MessageTypeOffset = 60;
constexpr std::size_t PayloadLengthOffset = 62;
constexpr std::uint8_t KeyContextDomain[] =
    "GWSU-PROTECTED-DATAGRAM-V1";

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
        destination[7 - index] =
            static_cast<std::uint8_t>(value);
        value >>= 8U;
    }
}

bool IsKnownDirection(SecureUdpDirection direction) noexcept {
    return direction == SecureUdpDirection::ClientToServer ||
        direction == SecureUdpDirection::ServerToClient;
}

bool RangesOverlap(
    const void* first,
    std::size_t firstBytes,
    const void* second,
    std::size_t secondBytes) noexcept {
    if (first == nullptr || second == nullptr ||
        firstBytes == 0 || secondBytes == 0) {
        return false;
    }
    const auto firstStart =
        reinterpret_cast<std::uintptr_t>(first);
    const auto secondStart =
        reinterpret_cast<std::uintptr_t>(second);
    if (firstBytes >
            (std::numeric_limits<std::uintptr_t>::max)() -
                firstStart ||
        secondBytes >
            (std::numeric_limits<std::uintptr_t>::max)() -
                secondStart) {
        return true;
    }
    return firstStart < secondStart + secondBytes &&
        secondStart < firstStart + firstBytes;
}

bool IsAllowedMessage(
    SecureUdpDirection direction,
    SecureUdpProtectedMessageType type) noexcept {
    return direction == SecureUdpDirection::ClientToServer
        ? type == SecureUdpProtectedMessageType::Ping ||
            type ==
                SecureUdpProtectedMessageType::MovementInput
        : direction == SecureUdpDirection::ServerToClient &&
            (type == SecureUdpProtectedMessageType::Pong ||
                type ==
                    SecureUdpProtectedMessageType::
                        BindingConfirm ||
                type ==
                    SecureUdpProtectedMessageType::
                        PositionSnapshot);
}

std::size_t ExpectedPayloadBytes(
    SecureUdpProtectedMessageType type) noexcept {
    switch (type) {
        case SecureUdpProtectedMessageType::Ping:
            return 16;
        case SecureUdpProtectedMessageType::Pong:
        case SecureUdpProtectedMessageType::BindingConfirm:
            return 32;
        case SecureUdpProtectedMessageType::MovementInput:
            return SecureRealtimeMovementInputBytes;
        case SecureUdpProtectedMessageType::PositionSnapshot:
            return SecureRealtimePositionSnapshotBytes;
        default:
            return 0;
    }
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

bool IsValidPayloadContent(
    SecureUdpProtectedMessageType type,
    const void* payload,
    std::size_t payloadBytes) noexcept {
    if (payload == nullptr ||
        payloadBytes != ExpectedPayloadBytes(type)) {
        return false;
    }
    const auto* input =
        static_cast<const std::uint8_t*>(payload);
    switch (type) {
        case SecureUdpProtectedMessageType::Ping:
            return ReadUInt64(input) != 0;
        case SecureUdpProtectedMessageType::Pong:
            return ReadUInt64(input) != 0 &&
                ReadUInt64(input + 16) != 0 &&
                ReadUInt64(input + 24) != 0;
        case SecureUdpProtectedMessageType::BindingConfirm:
            return !IsAllZero(input, 16) &&
                ReadUInt64(input + 16) != 0 &&
                ReadUInt64(input + 24) != 0;
        case SecureUdpProtectedMessageType::MovementInput: {
            SecureRealtimeMovementInput movement{};
            return TryDecodeSecureRealtimeMovementInput(
                payload,
                payloadBytes,
                SecureRealtimeMovementSource::Udp,
                &movement);
        }
        case SecureUdpProtectedMessageType::PositionSnapshot: {
            SecureRealtimePositionSnapshot snapshot{};
            return TryDecodeSecureRealtimePositionSnapshot(
                payload,
                payloadBytes,
                &snapshot);
        }
        default:
            return false;
    }
}

bool IsValidHeader(
    const SecureUdpProtectedHeader& header,
    std::size_t plaintextBytes) noexcept {
    const bool acknowledgmentValid =
        header.acknowledgmentEpoch == 0
        ? header.acknowledgmentSequence == 0 &&
            header.acknowledgmentMask == 0
        : header.acknowledgmentSequence >= 64 ||
            (header.acknowledgmentSequence == 0
                ? header.acknowledgmentMask == 0
                : (header.acknowledgmentMask >>
                      static_cast<unsigned>(
                          header.acknowledgmentSequence)) == 0);
    return header.keyEpoch != 0 &&
        plaintextBytes ==
            ExpectedPayloadBytes(header.messageType) &&
        plaintextBytes == header.payloadBytes &&
        acknowledgmentValid;
}

void BuildNonce(
    std::uint32_t keyEpoch,
    std::uint64_t sequence,
    std::uint8_t* nonce) noexcept {
    WriteUInt32(nonce, keyEpoch);
    WriteUInt64(nonce + 4, sequence);
}

} // namespace

bool TryDeriveSecureUdpEpochKey(
    const std::uint8_t* proofKey,
    std::size_t proofKeyBytes,
    const std::uint8_t* connectionId,
    std::size_t connectionIdBytes,
    std::uint32_t serverId,
    SecureUdpDirection direction,
    std::uint32_t keyEpoch,
    std::uint8_t* destination,
    std::size_t destinationBytes) noexcept {
    if (proofKey == nullptr ||
        proofKeyBytes != SecureUdpProofKeyBytes ||
        IsAllZero(proofKey, proofKeyBytes) ||
        connectionId == nullptr ||
        connectionIdBytes != SecureUdpConnectionIdBytes ||
        IsAllZero(connectionId, connectionIdBytes) ||
        serverId == 0 ||
        !IsKnownDirection(direction) ||
        keyEpoch == 0 ||
        destination == nullptr ||
        destinationBytes < SecureUdpAes256KeyBytes) {
        return false;
    }

    std::uint8_t salt[SecureUdpConnectionIdBytes + 4]{};
    std::uint8_t context[
        sizeof(KeyContextDomain) - 1 + 1 + 4]{};
    std::memcpy(salt, connectionId, connectionIdBytes);
    WriteUInt32(salt + connectionIdBytes, serverId);
    std::memcpy(
        context,
        KeyContextDomain,
        sizeof(KeyContextDomain) - 1);
    context[sizeof(KeyContextDomain) - 1] =
        static_cast<std::uint8_t>(direction);
    WriteUInt32(
        context + sizeof(KeyContextDomain),
        keyEpoch);

    const bool derived = SecureUdpHkdfSha256(
        proofKey,
        proofKeyBytes,
        salt,
        sizeof(salt),
        context,
        sizeof(context),
        destination,
        SecureUdpAes256KeyBytes);
    SecureZeroMemory(salt, sizeof(salt));
    SecureZeroMemory(context, sizeof(context));
    return derived;
}

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
    std::size_t* bytesWritten) noexcept {
    if (bytesWritten != nullptr) {
        *bytesWritten = 0;
    }
    const auto totalBytes =
        SecureUdpProtectedHeaderBytes +
        plaintextBytes +
        SecureUdpProtectedTagBytes;
    if (bytesWritten == nullptr ||
        connectionId == nullptr ||
        connectionIdBytes != SecureUdpConnectionIdBytes ||
        plaintext == nullptr ||
        !IsAllowedMessage(direction, header.messageType) ||
        !IsValidHeader(header, plaintextBytes) ||
        !IsValidPayloadContent(
            header.messageType,
            plaintext,
            plaintextBytes) ||
        totalBytes > SecureUdpProtectedMaximumBytes ||
        destination == nullptr ||
        destinationBytes < totalBytes ||
        RangesOverlap(
            plaintext,
            plaintextBytes,
            destination,
            totalBytes)) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    SecureZeroMemory(output, totalBytes);
    std::memcpy(output, ProtectedMagic, sizeof(ProtectedMagic));
    WriteUInt16(
        output + 4,
        static_cast<std::uint16_t>(
            SecureUdpProtectedHeaderBytes));
    output[6] = ProtocolMajor;
    output[7] = ProtocolMinor;
    output[8] = PacketType;
    WriteUInt16(
        output + 10,
        static_cast<std::uint16_t>(totalBytes));
    std::memcpy(
        output + ConnectionIdOffset,
        connectionId,
        connectionIdBytes);
    WriteUInt32(output + KeyEpochOffset, header.keyEpoch);
    WriteUInt64(output + SequenceOffset, header.sequence);
    WriteUInt32(
        output + AcknowledgmentEpochOffset,
        header.acknowledgmentEpoch);
    WriteUInt64(
        output + AcknowledgmentSequenceOffset,
        header.acknowledgmentSequence);
    WriteUInt64(
        output + AcknowledgmentMaskOffset,
        header.acknowledgmentMask);
    output[MessageTypeOffset] =
        static_cast<std::uint8_t>(header.messageType);
    WriteUInt16(
        output + PayloadLengthOffset,
        header.payloadBytes);

    std::uint8_t key[SecureUdpAes256KeyBytes]{};
    std::uint8_t nonce[SecureUdpGcmNonceBytes]{};
    const bool derived = TryDeriveSecureUdpEpochKey(
        proofKey,
        proofKeyBytes,
        connectionId,
        connectionIdBytes,
        serverId,
        direction,
        header.keyEpoch,
        key,
        sizeof(key));
    BuildNonce(header.keyEpoch, header.sequence, nonce);
    const bool sealed = derived &&
        SecureUdpAes256GcmSeal(
            key,
            sizeof(key),
            nonce,
            sizeof(nonce),
            output,
            SecureUdpProtectedHeaderBytes,
            plaintext,
            plaintextBytes,
            output + SecureUdpProtectedHeaderBytes,
            plaintextBytes,
            output + SecureUdpProtectedHeaderBytes +
                plaintextBytes,
            SecureUdpProtectedTagBytes);
    SecureZeroMemory(key, sizeof(key));
    SecureZeroMemory(nonce, sizeof(nonce));
    if (!sealed) {
        SecureZeroMemory(output, totalBytes);
        return false;
    }
    *bytesWritten = totalBytes;
    return true;
}

bool TryInspectSecureUdpProtectedDatagram(
    const std::uint8_t* expectedConnectionId,
    std::size_t connectionIdBytes,
    const void* datagram,
    std::size_t datagramBytes,
    SecureUdpProtectedHeader* header) noexcept {
    if (header != nullptr) {
        *header = SecureUdpProtectedHeader{};
    }
    if (expectedConnectionId == nullptr ||
        connectionIdBytes != SecureUdpConnectionIdBytes ||
        datagram == nullptr ||
        datagramBytes < SecureUdpProtectedMinimumBytes ||
        datagramBytes > SecureUdpProtectedMaximumBytes ||
        header == nullptr) {
        return false;
    }

    const auto* input =
        static_cast<const std::uint8_t*>(datagram);
    const auto encodedTotal = ReadUInt16(input + 10);
    const auto encodedPayload =
        ReadUInt16(input + PayloadLengthOffset);
    if (std::memcmp(
            input,
            ProtectedMagic,
            sizeof(ProtectedMagic)) != 0 ||
        ReadUInt16(input + 4) != SecureUdpProtectedHeaderBytes ||
        input[6] != ProtocolMajor ||
        input[7] != ProtocolMinor ||
        input[8] != PacketType ||
        input[9] != 0 ||
        encodedTotal != datagramBytes ||
        !Exact(
            input + ConnectionIdOffset,
            expectedConnectionId,
            connectionIdBytes) ||
        input[61] != 0 ||
        encodedPayload > SecureUdpProtectedMaximumPayloadBytes ||
        datagramBytes != SecureUdpProtectedHeaderBytes +
                encodedPayload + SecureUdpProtectedTagBytes) {
        return false;
    }

    SecureUdpProtectedHeader decoded{};
    decoded.keyEpoch = ReadUInt32(input + KeyEpochOffset);
    decoded.sequence = ReadUInt64(input + SequenceOffset);
    decoded.acknowledgmentEpoch =
        ReadUInt32(input + AcknowledgmentEpochOffset);
    decoded.acknowledgmentSequence =
        ReadUInt64(input + AcknowledgmentSequenceOffset);
    decoded.acknowledgmentMask =
        ReadUInt64(input + AcknowledgmentMaskOffset);
    decoded.messageType =
        static_cast<SecureUdpProtectedMessageType>(
            input[MessageTypeOffset]);
    decoded.payloadBytes = encodedPayload;
    if (!IsValidHeader(decoded, encodedPayload)) {
        return false;
    }
    *header = decoded;
    return true;
}

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
    std::size_t* plaintextBytes) noexcept {
    if (header != nullptr) {
        *header = SecureUdpProtectedHeader{};
    }
    if (plaintextBytes != nullptr) {
        *plaintextBytes = 0;
    }
    if (proofKey == nullptr ||
        proofKeyBytes != SecureUdpProofKeyBytes ||
        expectedConnectionId == nullptr ||
        connectionIdBytes != SecureUdpConnectionIdBytes ||
        serverId == 0 ||
        !IsKnownDirection(direction) ||
        datagram == nullptr ||
        datagramBytes < SecureUdpProtectedMinimumBytes ||
        datagramBytes > SecureUdpProtectedMaximumBytes ||
        header == nullptr ||
        plaintext == nullptr ||
        plaintextBytes == nullptr ||
        RangesOverlap(
            datagram,
            datagramBytes,
            plaintext,
            plaintextCapacity)) {
        return false;
    }

    SecureUdpProtectedHeader decoded{};
    if (!TryInspectSecureUdpProtectedDatagram(
            expectedConnectionId,
            connectionIdBytes,
            datagram,
            datagramBytes,
            &decoded) ||
        !IsAllowedMessage(direction, decoded.messageType) ||
        plaintextCapacity < decoded.payloadBytes) {
        return false;
    }
    const auto* input =
        static_cast<const std::uint8_t*>(datagram);
    const auto encodedPayload = decoded.payloadBytes;

    std::uint8_t key[SecureUdpAes256KeyBytes]{};
    std::uint8_t nonce[SecureUdpGcmNonceBytes]{};
    const bool derived = TryDeriveSecureUdpEpochKey(
        proofKey,
        proofKeyBytes,
        expectedConnectionId,
        connectionIdBytes,
        serverId,
        direction,
        decoded.keyEpoch,
        key,
        sizeof(key));
    BuildNonce(decoded.keyEpoch, decoded.sequence, nonce);
    const bool opened = derived &&
        SecureUdpAes256GcmOpen(
            key,
            sizeof(key),
            nonce,
            sizeof(nonce),
            input,
            SecureUdpProtectedHeaderBytes,
            input + SecureUdpProtectedHeaderBytes,
            encodedPayload,
            input + SecureUdpProtectedHeaderBytes +
                encodedPayload,
            SecureUdpProtectedTagBytes,
            plaintext,
            plaintextCapacity);
    SecureZeroMemory(key, sizeof(key));
    SecureZeroMemory(nonce, sizeof(nonce));
    if (!opened) {
        if (encodedPayload != 0) {
            SecureZeroMemory(plaintext, encodedPayload);
        }
        return false;
    }
    if (!IsValidPayloadContent(
            decoded.messageType,
            plaintext,
            encodedPayload)) {
        SecureZeroMemory(plaintext, encodedPayload);
        return false;
    }

    *header = decoded;
    *plaintextBytes = encodedPayload;
    return true;
}

} // namespace godswar::network
