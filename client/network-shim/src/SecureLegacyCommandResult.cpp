#include "SecureClientProtocol.h"

#include <cstring>

namespace godswar::network {
namespace {

bool IsAllZero(
    const std::uint8_t* bytes,
    std::size_t count) noexcept {
    std::uint8_t combined = 0;
    for (std::size_t index = 0; index < count; ++index) {
        combined |= bytes[index];
    }
    return combined == 0;
}

bool IsDisposition(
    SecureLegacyCommandDisposition disposition) noexcept {
    return disposition >= SecureLegacyCommandDisposition::Applied &&
        disposition <= SecureLegacyCommandDisposition::Conflict;
}

bool IsCommandFamily(
    SecureLegacyCommandFamily family) noexcept {
    return family ==
            SecureLegacyCommandFamily::EquipmentForge ||
        family ==
            SecureLegacyCommandFamily::MakeAttributeStone ||
        family ==
            SecureLegacyCommandFamily::TransformCrystal ||
        family ==
            SecureLegacyCommandFamily::CombineGemPieces ||
        family ==
            SecureLegacyCommandFamily::DecomposeGear ||
        family ==
            SecureLegacyCommandFamily::
                GearMentorEnhanceAttribute ||
        family ==
            SecureLegacyCommandFamily::
                GearMentorAddAttribute ||
        family ==
            SecureLegacyCommandFamily::
                GearMentorDeleteAttribute ||
        family ==
            SecureLegacyCommandFamily::KitBagItemDelete ||
        family ==
            SecureLegacyCommandFamily::KitBagItemMove;
}

bool HasValidRevision(
    SecureLegacyCommandDisposition disposition,
    std::uint64_t inventoryRevision) noexcept {
    return disposition !=
            SecureLegacyCommandDisposition::Applied ||
        inventoryRevision != 0;
}

void WriteUInt16(
    std::uint8_t* destination,
    std::uint16_t value) noexcept {
    destination[0] =
        static_cast<std::uint8_t>(value >> 8U);
    destination[1] = static_cast<std::uint8_t>(value);
}

void WriteUInt32(
    std::uint8_t* destination,
    std::uint32_t value) noexcept {
    destination[0] =
        static_cast<std::uint8_t>(value >> 24U);
    destination[1] =
        static_cast<std::uint8_t>(value >> 16U);
    destination[2] =
        static_cast<std::uint8_t>(value >> 8U);
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

std::uint16_t ReadUInt16(
    const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(source[0]) << 8U) |
        source[1]);
}

std::uint32_t ReadUInt32(
    const std::uint8_t* source) noexcept {
    return
        (static_cast<std::uint32_t>(source[0]) << 24U) |
        (static_cast<std::uint32_t>(source[1]) << 16U) |
        (static_cast<std::uint32_t>(source[2]) << 8U) |
        source[3];
}

std::uint64_t ReadUInt64(
    const std::uint8_t* source) noexcept {
    std::uint64_t value = 0;
    for (std::size_t index = 0; index < 8; ++index) {
        value = (value << 8U) | source[index];
    }
    return value;
}

} // namespace

bool TryEncodeSecureLegacyCommandResult(
    const SecureLegacyCommandResult& result,
    void* destination,
    std::size_t destinationBytes) noexcept {
    if (destination == nullptr ||
        destinationBytes <
            SecureLegacyCommandResultPayloadBytes ||
        !IsDisposition(result.disposition) ||
        !IsCommandFamily(result.commandFamily) ||
        !HasValidRevision(
            result.disposition,
            result.inventoryRevision) ||
        IsAllZero(
            result.operationId,
            sizeof(result.operationId))) {
        return false;
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::memset(
        output,
        0,
        SecureLegacyCommandResultPayloadBytes);
    output[0] = SecureLegacyCommandResultVersion;
    output[1] =
        static_cast<std::uint8_t>(result.disposition);
    WriteUInt16(
        output + 2,
        static_cast<std::uint16_t>(result.commandFamily));
    WriteUInt32(output + 4, result.resultCode);
    WriteUInt64(output + 8, result.inventoryRevision);
    std::memcpy(
        output + 16,
        result.operationId,
        sizeof(result.operationId));
    return true;
}

bool TryDecodeSecureLegacyCommandResult(
    const void* source,
    std::size_t sourceBytes,
    SecureLegacyCommandResult* result) noexcept {
    if (source == nullptr ||
        sourceBytes !=
            SecureLegacyCommandResultPayloadBytes ||
        result == nullptr) {
        return false;
    }

    const auto* input =
        static_cast<const std::uint8_t*>(source);
    SecureLegacyCommandResult decoded{};
    decoded.disposition =
        static_cast<SecureLegacyCommandDisposition>(input[1]);
    decoded.commandFamily =
        static_cast<SecureLegacyCommandFamily>(
            ReadUInt16(input + 2));
    if (input[0] != SecureLegacyCommandResultVersion ||
        !IsDisposition(decoded.disposition) ||
        !IsCommandFamily(decoded.commandFamily) ||
        !HasValidRevision(
            decoded.disposition,
            ReadUInt64(input + 8)) ||
        IsAllZero(input + 16, sizeof(decoded.operationId))) {
        return false;
    }

    decoded.resultCode = ReadUInt32(input + 4);
    decoded.inventoryRevision = ReadUInt64(input + 8);
    std::memcpy(
        decoded.operationId,
        input + 16,
        sizeof(decoded.operationId));
    *result = decoded;
    return true;
}

} // namespace godswar::network
