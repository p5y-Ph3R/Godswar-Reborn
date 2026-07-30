#include "SecureCharacterLifecycleIdentity.h"

#include "SecureLegacyCommandIdentity.h"

#include <cstring>

namespace godswar::network {
namespace {

inline constexpr std::size_t LegacyNameBytes = 32;
inline constexpr std::size_t CanonicalNameOffset = 3;

bool IsOuterWhitespace(std::uint8_t value) noexcept {
    return value == ' ' ||
        value == '\t' ||
        value == '\r' ||
        value == '\n';
}

bool WriteCanonicalName(
    const std::uint8_t* source,
    std::uint8_t* destination) noexcept {
    std::size_t length = 0;
    while (length < LegacyNameBytes &&
           source[length] != 0) {
        ++length;
    }

    if (length == 0 ||
        IsOuterWhitespace(source[0]) ||
        IsOuterWhitespace(source[length - 1])) {
        return false;
    }
    for (std::size_t index = 0;
         index < length;
         ++index) {
        if (source[index] < 0x20U ||
            source[index] > 0x7EU) {
            return false;
        }
    }

    destination[2] =
        static_cast<std::uint8_t>(length);
    std::memcpy(
        destination + CanonicalNameOffset,
        source,
        length);
    return true;
}

bool WriteCreateIntent(
    const std::uint8_t* packet,
    LegacyCharacterLifecycleIntent* intent) noexcept {
    const std::uint8_t gender = packet[36];
    const std::uint8_t camp = packet[37];
    const std::uint8_t profession = packet[38];
    const std::uint8_t zodiac = packet[39];
    const std::uint8_t faith = packet[74];
    if (gender > 1 ||
        camp > 1 ||
        profession > 3 ||
        zodiac > 11 ||
        faith > 3) {
        return false;
    }

    intent->family =
        SecureLegacyCommandFamily::CharacterCreate;
    intent->bytes[0] = 1;
    intent->bytes[1] = 1;
    if (!WriteCanonicalName(packet + 4, intent->bytes)) {
        return false;
    }
    intent->bytes[35] = gender;
    intent->bytes[36] = camp;
    intent->bytes[37] = profession;
    intent->bytes[38] = zodiac;
    intent->bytes[39] = packet[40];
    intent->bytes[40] = packet[41];
    intent->bytes[41] = faith;
    return true;
}

bool WriteDeleteIntent(
    const std::uint8_t* packet,
    LegacyCharacterLifecycleIntent* intent) noexcept {
    intent->family =
        SecureLegacyCommandFamily::CharacterDelete;
    intent->bytes[0] = 1;
    intent->bytes[1] = 2;
    // The first fixed field is a client-supplied account name. The
    // authenticated login fingerprint is the principal; only the requested
    // character name belongs in the canonical delete intent.
    return WriteCanonicalName(packet + 36, intent->bytes);
}

} // namespace

LegacyCharacterLifecyclePacketKind
ClassifyLegacyCharacterLifecyclePacket(
    const void* packet,
    std::size_t packetBytes,
    LegacyCharacterLifecycleIntent* intent) noexcept {
    std::uint16_t opcode = 0;
    if (!TryReadLegacyPacketHeader(
            packet,
            packetBytes,
            &opcode)) {
        return LegacyCharacterLifecyclePacketKind::Unrelated;
    }
    if (opcode != LegacyCreateCharacterOpcode &&
        opcode != LegacyDeleteCharacterOpcode) {
        return LegacyCharacterLifecyclePacketKind::Unrelated;
    }
    if (intent == nullptr) {
        return LegacyCharacterLifecyclePacketKind::InvalidMutation;
    }
    *intent = LegacyCharacterLifecycleIntent{};

    const auto* bytes =
        static_cast<const std::uint8_t*>(packet);
    const bool valid =
        opcode == LegacyCreateCharacterOpcode
        ? packetBytes == LegacyCreateCharacterPacketBytes &&
            WriteCreateIntent(bytes, intent)
        : packetBytes == LegacyDeleteCharacterPacketBytes &&
            WriteDeleteIntent(bytes, intent);
    if (!valid) {
        *intent = LegacyCharacterLifecycleIntent{};
        return LegacyCharacterLifecyclePacketKind::InvalidMutation;
    }
    return LegacyCharacterLifecyclePacketKind::Command;
}

bool EqualCharacterLifecycleIntent(
    const LegacyCharacterLifecycleIntent& first,
    const LegacyCharacterLifecycleIntent& second) noexcept {
    return first.family == second.family &&
        std::memcmp(
            first.bytes,
            second.bytes,
            sizeof(first.bytes)) == 0;
}

} // namespace godswar::network
