#include "EndpointManifest.h"
#include "EndpointManifestCrypto.h"

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace godswar::network {
namespace {

constexpr std::size_t MinimumManifestBytes = 146;
constexpr std::size_t MaximumVersionOneBytes = 3258;
constexpr std::uint16_t FormatMajor = 1;
constexpr std::uint16_t FormatMinor = 0;
constexpr std::uint16_t SignatureAlgorithmP256Sha256P1363 = 1;
constexpr std::uint16_t ProtocolMajor = 1;
constexpr std::uint16_t ProtocolMinor = 0;
constexpr std::uint8_t LegacyPassthroughFlag = 0x01;

std::uint16_t ReadUint16(
    const std::uint8_t* bytes) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(bytes[0]) << 8) |
        static_cast<std::uint16_t>(bytes[1]));
}

std::uint32_t ReadUint32(
    const std::uint8_t* bytes) noexcept {
    return (static_cast<std::uint32_t>(bytes[0]) << 24) |
        (static_cast<std::uint32_t>(bytes[1]) << 16) |
        (static_cast<std::uint32_t>(bytes[2]) << 8) |
        static_cast<std::uint32_t>(bytes[3]);
}

std::uint64_t ReadUint64(
    const std::uint8_t* bytes) noexcept {
    return (static_cast<std::uint64_t>(ReadUint32(bytes)) << 32) |
        static_cast<std::uint64_t>(ReadUint32(bytes + 4));
}

bool IsKnownEnvironment(
    EndpointManifestEnvironment environment) noexcept {
    switch (environment) {
        case EndpointManifestEnvironment::Development:
        case EndpointManifestEnvironment::Staging:
        case EndpointManifestEnvironment::Production:
            return true;
    }
    return false;
}

bool IsAsciiLowerLetter(std::uint8_t value) noexcept {
    return value >= static_cast<std::uint8_t>('a') &&
        value <= static_cast<std::uint8_t>('z');
}

bool IsAsciiDigit(std::uint8_t value) noexcept {
    return value >= static_cast<std::uint8_t>('0') &&
        value <= static_cast<std::uint8_t>('9');
}

bool IsDnsLabelCharacter(std::uint8_t value) noexcept {
    return IsAsciiLowerLetter(value) ||
        IsAsciiDigit(value) ||
        value == static_cast<std::uint8_t>('-');
}

bool LooksLikeNumericDottedHost(
    const char* value,
    std::size_t length) noexcept {
    bool foundDot = false;
    for (std::size_t index = 0; index < length; ++index) {
        const auto character =
            static_cast<std::uint8_t>(value[index]);
        if (character == static_cast<std::uint8_t>('.')) {
            foundDot = true;
        } else if (!IsAsciiDigit(character)) {
            return false;
        }
    }
    return foundDot;
}

bool IsCanonicalDnsName(
    const char* value,
    std::size_t length,
    bool rejectNumericDotted) noexcept {
    if (value == nullptr ||
        length == 0 ||
        length > EndpointManifestMaximumDnsBytes ||
        value[length - 1] == '.' ||
        (rejectNumericDotted &&
            LooksLikeNumericDottedHost(value, length))) {
        return false;
    }

    std::size_t labelStart = 0;
    for (std::size_t index = 0; index <= length; ++index) {
        if (index != length && value[index] != '.') {
            if (!IsDnsLabelCharacter(
                    static_cast<std::uint8_t>(value[index]))) {
                return false;
            }
            continue;
        }

        const std::size_t labelLength = index - labelStart;
        if (labelLength == 0 || labelLength > 63) {
            return false;
        }
        if (value[labelStart] == '-' || value[index - 1] == '-') {
            return false;
        }
        labelStart = index + 1;
    }
    return true;
}

bool IsCanonicalIpv4(
    const char* value,
    std::size_t length) noexcept {
    if (value == nullptr || length < 7 || length > 15) {
        return false;
    }

    std::size_t cursor = 0;
    for (std::size_t octet = 0; octet < 4; ++octet) {
        const std::size_t start = cursor;
        std::uint32_t numericValue = 0;
        while (cursor < length && value[cursor] != '.') {
            const auto character =
                static_cast<std::uint8_t>(value[cursor]);
            if (!IsAsciiDigit(character) || cursor - start >= 3) {
                return false;
            }
            numericValue = numericValue * 10 +
                static_cast<std::uint32_t>(character - '0');
            ++cursor;
        }

        const std::size_t digits = cursor - start;
        if (digits == 0 ||
            numericValue > 255 ||
            (digits > 1 && value[start] == '0')) {
            return false;
        }

        if (octet < 3) {
            if (cursor >= length || value[cursor] != '.') {
                return false;
            }
            ++cursor;
        } else if (cursor != length) {
            return false;
        }
    }
    return true;
}

bool IsCanonicalLogicalHost(
    const char* value,
    std::size_t length) noexcept {
    if (LooksLikeNumericDottedHost(value, length)) {
        return IsCanonicalIpv4(value, length);
    }
    return IsCanonicalDnsName(value, length, false);
}

bool IsValidAudience(
    const char* value,
    std::size_t length) noexcept {
    if (value == nullptr ||
        length == 0 ||
        length > EndpointManifestMaximumAudienceBytes) {
        return false;
    }

    for (std::size_t index = 0; index < length; ++index) {
        const auto character =
            static_cast<std::uint8_t>(value[index]);
        const bool valid =
            IsAsciiLowerLetter(character) ||
            (character >= static_cast<std::uint8_t>('A') &&
                character <= static_cast<std::uint8_t>('Z')) ||
            IsAsciiDigit(character) ||
            character == static_cast<std::uint8_t>('.') ||
            character == static_cast<std::uint8_t>('_') ||
            character == static_cast<std::uint8_t>('-');
        if (!valid) {
            return false;
        }
    }
    return true;
}

template <typename Text>
bool TextEquals(
    const Text& left,
    const Text& right) noexcept {
    return left.length == right.length &&
        std::memcmp(left.bytes, right.bytes, left.length) == 0;
}

bool ReadText(
    const std::uint8_t* bytes,
    std::size_t bodyEnd,
    std::size_t* cursor,
    std::size_t length,
    EndpointManifestText* text) noexcept {
    if (length == 0 ||
        length > EndpointManifestMaximumDnsBytes ||
        *cursor > bodyEnd ||
        length > bodyEnd - *cursor) {
        return false;
    }
    std::memcpy(text->bytes, bytes + *cursor, length);
    text->bytes[length] = '\0';
    text->length = static_cast<std::uint16_t>(length);
    *cursor += length;
    return true;
}

} // namespace

EndpointManifestError ParseAndVerifyEndpointManifest(
    const std::uint8_t* bytes,
    std::size_t byteCount,
    const EndpointManifestValidationContext& validation,
    EndpointManifest* manifest) noexcept {
    if (bytes == nullptr ||
        manifest == nullptr ||
        validation.publicKeyLookup == nullptr ||
        validation.sequenceFloorLookup == nullptr ||
        validation.clock == nullptr ||
        !IsKnownEnvironment(validation.expectedEnvironment)) {
        return EndpointManifestError::InvalidArgument;
    }
    if (byteCount < MinimumManifestBytes ||
        byteCount > MaximumVersionOneBytes ||
        byteCount > EndpointManifestMaximumBytes) {
        return EndpointManifestError::InvalidLength;
    }
    if (std::memcmp(bytes, "GWEM", 4) != 0) {
        return EndpointManifestError::InvalidMagic;
    }

    const std::uint32_t declaredTotal = ReadUint32(bytes + 4);
    const std::uint32_t signedByteCount = ReadUint32(bytes + 64);
    if (declaredTotal != byteCount ||
        signedByteCount !=
            byteCount - EndpointManifestSignatureBytes ||
        signedByteCount < EndpointManifestHeaderBytes) {
        return EndpointManifestError::InvalidLength;
    }
    if (ReadUint16(bytes + 8) != EndpointManifestHeaderBytes ||
        ReadUint32(bytes + 20) != 0 ||
        bytes[63] != 0 ||
        ReadUint32(bytes + 68) != 0) {
        return EndpointManifestError::InvalidHeader;
    }
    if (ReadUint16(bytes + 10) != FormatMajor ||
        ReadUint16(bytes + 12) != FormatMinor) {
        return EndpointManifestError::UnsupportedFormat;
    }

    EndpointManifest candidate{};
    candidate.environment =
        static_cast<EndpointManifestEnvironment>(bytes[14]);
    if (!IsKnownEnvironment(candidate.environment) ||
        candidate.environment != validation.expectedEnvironment) {
        return EndpointManifestError::EnvironmentMismatch;
    }
    candidate.flags = bytes[15];
    if ((candidate.flags & ~LegacyPassthroughFlag) != 0 ||
        (candidate.flags == LegacyPassthroughFlag &&
            candidate.environment !=
                EndpointManifestEnvironment::Development)) {
        return EndpointManifestError::InvalidFlags;
    }
    if (ReadUint16(bytes + 16) !=
        SignatureAlgorithmP256Sha256P1363) {
        return EndpointManifestError::UnsupportedSignatureAlgorithm;
    }

    candidate.publicKeyId = ReadUint16(bytes + 18);
    EndpointManifestPublicKey publicKey{};
    if (candidate.publicKeyId == 0 ||
        !validation.publicKeyLookup(
            validation.context,
            candidate.publicKeyId,
            &publicKey)) {
        return EndpointManifestError::UnknownPublicKey;
    }

    candidate.sequence = ReadUint64(bytes + 24);
    if (candidate.sequence == 0) {
        return EndpointManifestError::InvalidSequence;
    }
    std::uint64_t compiledMinimum = 0;
    std::uint64_t installedMinimum = 0;
    if (!validation.sequenceFloorLookup(
            validation.context,
            candidate.environment,
            &compiledMinimum,
            &installedMinimum)) {
        return EndpointManifestError::SequenceLookupFailed;
    }
    if (candidate.sequence < compiledMinimum ||
        candidate.sequence < installedMinimum) {
        return EndpointManifestError::InvalidSequence;
    }

    candidate.notBeforeUnixSeconds = ReadUint64(bytes + 32);
    candidate.notAfterUnixSeconds = ReadUint64(bytes + 40);
    std::uint64_t now = 0;
    if (!validation.clock(validation.context, &now)) {
        return EndpointManifestError::ClockLookupFailed;
    }
    if (candidate.notAfterUnixSeconds <=
            candidate.notBeforeUnixSeconds ||
        candidate.notAfterUnixSeconds -
                candidate.notBeforeUnixSeconds >
            EndpointManifestMaximumValiditySeconds ||
        now < candidate.notBeforeUnixSeconds ||
        now > candidate.notAfterUnixSeconds) {
        return EndpointManifestError::InvalidValidity;
    }

    if (ReadUint16(bytes + 48) != ProtocolMajor ||
        ReadUint16(bytes + 50) != ProtocolMinor) {
        return EndpointManifestError::UnsupportedProtocol;
    }
    candidate.logicalLoginPort = ReadUint16(bytes + 52);
    candidate.tlsLoginPort = ReadUint16(bytes + 54);
    if (candidate.logicalLoginPort == 0 ||
        candidate.tlsLoginPort == 0) {
        return EndpointManifestError::InvalidPort;
    }

    const std::size_t logicalHostLength = ReadUint16(bytes + 56);
    const std::size_t tlsHostLength = ReadUint16(bytes + 58);
    candidate.gameSuffixCount = bytes[60];
    candidate.audienceCount = bytes[61];
    candidate.serverIdCount = bytes[62];
    if (logicalHostLength == 0 ||
        logicalHostLength > EndpointManifestMaximumDnsBytes ||
        tlsHostLength == 0 ||
        tlsHostLength > EndpointManifestMaximumDnsBytes ||
        candidate.gameSuffixCount == 0 ||
        candidate.gameSuffixCount >
            EndpointManifestMaximumGameSuffixes ||
        candidate.audienceCount == 0 ||
        candidate.audienceCount >
            EndpointManifestMaximumAudiences ||
        candidate.serverIdCount == 0 ||
        candidate.serverIdCount >
            EndpointManifestMaximumServerIds) {
        return EndpointManifestError::InvalidCount;
    }

    std::size_t cursor = EndpointManifestHeaderBytes;
    if (!ReadText(
            bytes,
            signedByteCount,
            &cursor,
            logicalHostLength,
            &candidate.logicalLoginHost) ||
        !IsCanonicalLogicalHost(
            candidate.logicalLoginHost.bytes,
            candidate.logicalLoginHost.length) ||
        !ReadText(
            bytes,
            signedByteCount,
            &cursor,
            tlsHostLength,
            &candidate.tlsLoginHost) ||
        !IsCanonicalDnsName(
            candidate.tlsLoginHost.bytes,
            candidate.tlsLoginHost.length,
            true)) {
        return EndpointManifestError::InvalidHost;
    }

    for (std::size_t index = 0;
         index < candidate.gameSuffixCount;
         ++index) {
        if (cursor >= signedByteCount) {
            return EndpointManifestError::InvalidSuffix;
        }
        const std::size_t length = bytes[cursor++];
        if (!ReadText(
                bytes,
                signedByteCount,
                &cursor,
                length,
                &candidate.gameSuffixes[index]) ||
            !IsCanonicalDnsName(
                candidate.gameSuffixes[index].bytes,
                candidate.gameSuffixes[index].length,
                true)) {
            return EndpointManifestError::InvalidSuffix;
        }
        for (std::size_t earlier = 0; earlier < index; ++earlier) {
            if (TextEquals(
                    candidate.gameSuffixes[index],
                    candidate.gameSuffixes[earlier])) {
                return EndpointManifestError::DuplicateValue;
            }
        }
    }

    for (std::size_t index = 0;
         index < candidate.audienceCount;
         ++index) {
        if (cursor >= signedByteCount) {
            return EndpointManifestError::InvalidAudience;
        }
        const std::size_t length = bytes[cursor++];
        if (length == 0 ||
            length > EndpointManifestMaximumAudienceBytes ||
            cursor > signedByteCount ||
            length > signedByteCount - cursor) {
            return EndpointManifestError::InvalidAudience;
        }
        auto& audience = candidate.audiences[index];
        std::memcpy(audience.bytes, bytes + cursor, length);
        audience.bytes[length] = '\0';
        audience.length = static_cast<std::uint8_t>(length);
        cursor += length;
        if (!IsValidAudience(audience.bytes, audience.length)) {
            return EndpointManifestError::InvalidAudience;
        }
        for (std::size_t earlier = 0; earlier < index; ++earlier) {
            if (TextEquals(audience, candidate.audiences[earlier])) {
                return EndpointManifestError::DuplicateValue;
            }
        }
    }

    for (std::size_t index = 0;
         index < candidate.serverIdCount;
         ++index) {
        if (cursor > signedByteCount ||
            sizeof(std::uint32_t) > signedByteCount - cursor) {
            return EndpointManifestError::InvalidServerId;
        }
        const std::uint32_t serverId = ReadUint32(bytes + cursor);
        cursor += sizeof(std::uint32_t);
        if (serverId == 0) {
            return EndpointManifestError::InvalidServerId;
        }
        for (std::size_t earlier = 0; earlier < index; ++earlier) {
            if (candidate.serverIds[earlier] == serverId) {
                return EndpointManifestError::DuplicateValue;
            }
        }
        candidate.serverIds[index] = serverId;
    }

    if (cursor != signedByteCount) {
        return EndpointManifestError::TrailingBodyBytes;
    }
    if (!VerifyEndpointManifestSignature(
            bytes,
            signedByteCount,
            bytes + signedByteCount,
            publicKey)) {
        return EndpointManifestError::SignatureVerificationFailed;
    }

    *manifest = candidate;
    return EndpointManifestError::Success;
}

bool EndpointManifestAllowsGameHost(
    const EndpointManifest& manifest,
    const char* canonicalDnsHost,
    std::size_t hostLength) noexcept {
    if (!IsCanonicalDnsName(
            canonicalDnsHost,
            hostLength,
            true)) {
        return false;
    }

    for (std::size_t index = 0;
         index < manifest.gameSuffixCount &&
             index < EndpointManifestMaximumGameSuffixes;
         ++index) {
        const auto& suffix = manifest.gameSuffixes[index];
        if (hostLength == suffix.length &&
            std::memcmp(
                canonicalDnsHost,
                suffix.bytes,
                hostLength) == 0) {
            return true;
        }
        if (hostLength > suffix.length &&
            canonicalDnsHost[
                hostLength - suffix.length - 1] == '.' &&
            std::memcmp(
                canonicalDnsHost + hostLength - suffix.length,
                suffix.bytes,
                suffix.length) == 0) {
            return true;
        }
    }
    return false;
}

bool EndpointManifestAllowsAudience(
    const EndpointManifest& manifest,
    const char* audience,
    std::size_t audienceLength) noexcept {
    if (!IsValidAudience(audience, audienceLength)) {
        return false;
    }
    for (std::size_t index = 0;
         index < manifest.audienceCount &&
             index < EndpointManifestMaximumAudiences;
         ++index) {
        if (manifest.audiences[index].length == audienceLength &&
            std::memcmp(
                manifest.audiences[index].bytes,
                audience,
                audienceLength) == 0) {
            return true;
        }
    }
    return false;
}

bool EndpointManifestAllowsServerId(
    const EndpointManifest& manifest,
    std::uint32_t serverId) noexcept {
    if (serverId == 0) {
        return false;
    }
    for (std::size_t index = 0;
         index < manifest.serverIdCount &&
             index < EndpointManifestMaximumServerIds;
         ++index) {
        if (manifest.serverIds[index] == serverId) {
            return true;
        }
    }
    return false;
}

} // namespace godswar::network
