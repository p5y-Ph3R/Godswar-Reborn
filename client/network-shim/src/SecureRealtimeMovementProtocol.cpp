#include "SecureRealtimeMovementProtocol.h"

#include <cmath>
#include <cstring>

namespace godswar::network {
namespace {

constexpr std::uint8_t KnownInputFlags =
    static_cast<std::uint8_t>(
        SecureRealtimeMovementInputFlag::CurrentWorld);
constexpr std::uint8_t KnownSnapshotFlags =
    static_cast<std::uint8_t>(
        SecureRealtimePositionSnapshotFlag::Keyframe) |
    static_cast<std::uint8_t>(
        SecureRealtimePositionSnapshotFlag::Correction);

std::uint16_t ReadBigUInt16(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(source[0]) << 8U) |
        source[1]);
}

std::uint32_t ReadBigUInt32(
    const std::uint8_t* source) noexcept {
    return
        (static_cast<std::uint32_t>(source[0]) << 24U) |
        (static_cast<std::uint32_t>(source[1]) << 16U) |
        (static_cast<std::uint32_t>(source[2]) << 8U) |
        source[3];
}

std::uint64_t ReadBigUInt64(
    const std::uint8_t* source) noexcept {
    std::uint64_t value = 0;
    for (std::size_t index = 0; index < 8; ++index) {
        value = (value << 8U) | source[index];
    }
    return value;
}

std::uint16_t ReadLittleUInt16(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        source[0] |
        (static_cast<std::uint16_t>(source[1]) << 8U));
}

std::uint32_t ReadLittleUInt32(
    const std::uint8_t* source) noexcept {
    return
        source[0] |
        (static_cast<std::uint32_t>(source[1]) << 8U) |
        (static_cast<std::uint32_t>(source[2]) << 16U) |
        (static_cast<std::uint32_t>(source[3]) << 24U);
}

void WriteBigUInt16(
    std::uint8_t* destination,
    std::uint16_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 8U);
    destination[1] = static_cast<std::uint8_t>(value);
}

void WriteBigUInt32(
    std::uint8_t* destination,
    std::uint32_t value) noexcept {
    destination[0] = static_cast<std::uint8_t>(value >> 24U);
    destination[1] = static_cast<std::uint8_t>(value >> 16U);
    destination[2] = static_cast<std::uint8_t>(value >> 8U);
    destination[3] = static_cast<std::uint8_t>(value);
}

void WriteBigUInt64(
    std::uint8_t* destination,
    std::uint64_t value) noexcept {
    for (std::size_t index = 0; index < 8; ++index) {
        destination[7 - index] =
            static_cast<std::uint8_t>(value);
        value >>= 8U;
    }
}

float FloatFromBits(std::uint32_t bits) noexcept {
    float value = 0.0F;
    static_assert(sizeof(value) == sizeof(bits));
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

std::uint32_t FloatBits(float value) noexcept {
    std::uint32_t bits = 0;
    static_assert(sizeof(value) == sizeof(bits));
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
}

bool IsFiniteMovement(
    float x,
    float z,
    float auxiliary) noexcept {
    return std::isfinite(x) &&
        std::isfinite(z) &&
        std::isfinite(auxiliary);
}

bool HasFlag(std::uint8_t flags, std::uint8_t flag) noexcept {
    return (flags & flag) != 0;
}

bool IsValidInput(
    const SecureRealtimeMovementInput& movement,
    SecureRealtimeMovementSource source) noexcept {
    if ((movement.flags & ~KnownInputFlags) != 0 ||
        movement.transportEpoch == 0 ||
        movement.inputId == 0 ||
        movement.clientMonotonicMilliseconds == 0 ||
        !IsFiniteMovement(
            movement.x,
            movement.z,
            movement.auxiliary)) {
        return false;
    }
    return source == SecureRealtimeMovementSource::Udp
        ? movement.flags == 0
        : source == SecureRealtimeMovementSource::TlsFallback;
}

bool IsValidSnapshot(
    const SecureRealtimePositionSnapshot& snapshot) noexcept {
    const auto correction = static_cast<std::uint8_t>(
        SecureRealtimePositionSnapshotFlag::Correction);
    const auto rejection =
        static_cast<std::uint8_t>(snapshot.rejection);
    return (snapshot.flags & ~KnownSnapshotFlags) == 0 &&
        snapshot.transportEpoch != 0 &&
        snapshot.serverTick != 0 &&
        snapshot.snapshotSequence != 0 &&
        rejection <= static_cast<std::uint8_t>(
            SecureRealtimeMovementRejection::Overloaded) &&
        (rejection == 0 ||
            HasFlag(snapshot.flags, correction)) &&
        IsFiniteMovement(
            snapshot.x,
            snapshot.z,
            snapshot.auxiliary);
}

} // namespace

bool TryParseSecureRealtimeLegacyMovement(
    const void* source,
    std::size_t sourceBytes,
    SecureRealtimeLegacyMovement* movement) noexcept {
    if (movement != nullptr) {
        *movement = SecureRealtimeLegacyMovement{};
    }
    if (source == nullptr ||
        sourceBytes != SecureRealtimeLegacyMovementBytes ||
        movement == nullptr) {
        return false;
    }

    const auto* input =
        static_cast<const std::uint8_t*>(source);
    if (ReadLittleUInt16(input) !=
            SecureRealtimeLegacyMovementBytes ||
        ReadLittleUInt16(input + 2) !=
            SecureRealtimeLegacyMovementOpcode) {
        return false;
    }

    SecureRealtimeLegacyMovement decoded{};
    decoded.legacyState = ReadLittleUInt32(input + 4);
    decoded.x = FloatFromBits(ReadLittleUInt32(input + 8));
    decoded.z = FloatFromBits(ReadLittleUInt32(input + 12));
    decoded.auxiliary =
        FloatFromBits(ReadLittleUInt32(input + 16));
    if (!IsFiniteMovement(
            decoded.x,
            decoded.z,
            decoded.auxiliary)) {
        return false;
    }
    *movement = decoded;
    return true;
}

bool TryEncodeSecureRealtimeMovementInput(
    const SecureRealtimeMovementInput& movement,
    SecureRealtimeMovementSource source,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (destination == nullptr ||
        destinationBytes < SecureRealtimeMovementInputBytes ||
        !IsValidInput(movement, source)) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::memset(output, 0, SecureRealtimeMovementInputBytes);
    output[0] = SecureRealtimeMovementVersion;
    output[1] = movement.flags;
    WriteBigUInt16(
        output + 2,
        static_cast<std::uint16_t>(
            SecureRealtimeMovementInputBytes));
    WriteBigUInt32(output + 4, movement.transportEpoch);
    WriteBigUInt64(output + 8, movement.inputId);
    WriteBigUInt64(
        output + 16,
        movement.clientMonotonicMilliseconds);
    WriteBigUInt32(output + 24, movement.worldGeneration);
    WriteBigUInt32(output + 28, movement.legacyState);
    WriteBigUInt32(output + 32, FloatBits(movement.x));
    WriteBigUInt32(output + 36, FloatBits(movement.z));
    WriteBigUInt32(
        output + 40,
        FloatBits(movement.auxiliary));
    output[44] = movement.mapId;
    WriteBigUInt16(
        output + 48,
        SecureRealtimeLegacyMovementOpcode);
    WriteBigUInt16(
        output + 50,
        static_cast<std::uint16_t>(
            SecureRealtimeLegacyMovementBytes));
    return true;
}

bool TryDecodeSecureRealtimeMovementInput(
    const void* source,
    std::size_t sourceBytes,
    SecureRealtimeMovementSource transport,
    SecureRealtimeMovementInput* movement) noexcept {
    if (movement != nullptr) {
        *movement = SecureRealtimeMovementInput{};
    }
    if (source == nullptr ||
        sourceBytes != SecureRealtimeMovementInputBytes ||
        movement == nullptr) {
        return false;
    }
    const auto* input =
        static_cast<const std::uint8_t*>(source);
    if (input[0] != SecureRealtimeMovementVersion ||
        ReadBigUInt16(input + 2) !=
            SecureRealtimeMovementInputBytes ||
        input[45] != 0 ||
        input[46] != 0 ||
        input[47] != 0 ||
        ReadBigUInt16(input + 48) !=
            SecureRealtimeLegacyMovementOpcode ||
        ReadBigUInt16(input + 50) !=
            SecureRealtimeLegacyMovementBytes) {
        return false;
    }

    SecureRealtimeMovementInput decoded{};
    decoded.flags = input[1];
    decoded.transportEpoch = ReadBigUInt32(input + 4);
    decoded.inputId = ReadBigUInt64(input + 8);
    decoded.clientMonotonicMilliseconds =
        ReadBigUInt64(input + 16);
    decoded.worldGeneration = ReadBigUInt32(input + 24);
    decoded.legacyState = ReadBigUInt32(input + 28);
    decoded.x = FloatFromBits(ReadBigUInt32(input + 32));
    decoded.z = FloatFromBits(ReadBigUInt32(input + 36));
    decoded.auxiliary =
        FloatFromBits(ReadBigUInt32(input + 40));
    decoded.mapId = input[44];
    if (!IsValidInput(decoded, transport)) {
        return false;
    }
    *movement = decoded;
    return true;
}

bool TryEncodeSecureRealtimePositionSnapshot(
    const SecureRealtimePositionSnapshot& snapshot,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (destination == nullptr ||
        destinationBytes <
            SecureRealtimePositionSnapshotBytes ||
        !IsValidSnapshot(snapshot)) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::memset(
        output,
        0,
        SecureRealtimePositionSnapshotBytes);
    output[0] = SecureRealtimeMovementVersion;
    output[1] = snapshot.flags;
    WriteBigUInt16(
        output + 2,
        static_cast<std::uint16_t>(
            SecureRealtimePositionSnapshotBytes));
    WriteBigUInt32(output + 4, snapshot.transportEpoch);
    WriteBigUInt64(
        output + 8,
        snapshot.acknowledgedInputId);
    WriteBigUInt64(output + 16, snapshot.serverTick);
    WriteBigUInt64(output + 24, snapshot.revision);
    WriteBigUInt64(
        output + 32,
        snapshot.snapshotSequence);
    WriteBigUInt32(
        output + 40,
        snapshot.worldGeneration);
    WriteBigUInt32(output + 44, snapshot.legacyState);
    WriteBigUInt32(output + 48, FloatBits(snapshot.x));
    WriteBigUInt32(output + 52, FloatBits(snapshot.z));
    WriteBigUInt32(
        output + 56,
        FloatBits(snapshot.auxiliary));
    output[60] = snapshot.mapId;
    output[61] =
        static_cast<std::uint8_t>(snapshot.rejection);
    return true;
}

bool TryDecodeSecureRealtimePositionSnapshot(
    const void* source,
    std::size_t sourceBytes,
    SecureRealtimePositionSnapshot* snapshot) noexcept {
    if (snapshot != nullptr) {
        *snapshot = SecureRealtimePositionSnapshot{};
    }
    if (source == nullptr ||
        sourceBytes != SecureRealtimePositionSnapshotBytes ||
        snapshot == nullptr) {
        return false;
    }
    const auto* input =
        static_cast<const std::uint8_t*>(source);
    if (input[0] != SecureRealtimeMovementVersion ||
        ReadBigUInt16(input + 2) !=
            SecureRealtimePositionSnapshotBytes ||
        input[62] != 0 ||
        input[63] != 0) {
        return false;
    }

    SecureRealtimePositionSnapshot decoded{};
    decoded.flags = input[1];
    decoded.transportEpoch = ReadBigUInt32(input + 4);
    decoded.acknowledgedInputId =
        ReadBigUInt64(input + 8);
    decoded.serverTick = ReadBigUInt64(input + 16);
    decoded.revision = ReadBigUInt64(input + 24);
    decoded.snapshotSequence = ReadBigUInt64(input + 32);
    decoded.worldGeneration = ReadBigUInt32(input + 40);
    decoded.legacyState = ReadBigUInt32(input + 44);
    decoded.x = FloatFromBits(ReadBigUInt32(input + 48));
    decoded.z = FloatFromBits(ReadBigUInt32(input + 52));
    decoded.auxiliary =
        FloatFromBits(ReadBigUInt32(input + 56));
    decoded.mapId = input[60];
    decoded.rejection =
        static_cast<SecureRealtimeMovementRejection>(
            input[61]);
    if (!IsValidSnapshot(decoded)) {
        return false;
    }
    *snapshot = decoded;
    return true;
}

} // namespace godswar::network
