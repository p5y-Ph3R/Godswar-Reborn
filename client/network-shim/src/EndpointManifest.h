#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t EndpointManifestMaximumBytes = 4096;
inline constexpr std::size_t EndpointManifestHeaderBytes = 72;
inline constexpr std::size_t EndpointManifestSignatureBytes = 64;
inline constexpr std::size_t EndpointManifestMaximumDnsBytes = 253;
inline constexpr std::size_t EndpointManifestMaximumAudienceBytes = 64;
inline constexpr std::size_t EndpointManifestMaximumGameSuffixes = 8;
inline constexpr std::size_t EndpointManifestMaximumAudiences = 8;
inline constexpr std::size_t EndpointManifestMaximumServerIds = 16;
inline constexpr std::uint64_t EndpointManifestMaximumValiditySeconds =
    31ULL * 24ULL * 60ULL * 60ULL;

enum class EndpointManifestEnvironment : std::uint8_t {
    Development = 1,
    Staging = 2,
    Production = 3,
};

enum class EndpointManifestError : std::uint8_t {
    Success = 0,
    InvalidArgument,
    InvalidLength,
    InvalidMagic,
    InvalidHeader,
    UnsupportedFormat,
    EnvironmentMismatch,
    InvalidFlags,
    UnsupportedSignatureAlgorithm,
    UnknownPublicKey,
    InvalidSequence,
    SequenceLookupFailed,
    InvalidValidity,
    ClockLookupFailed,
    UnsupportedProtocol,
    InvalidPort,
    InvalidCount,
    InvalidHost,
    InvalidSuffix,
    InvalidAudience,
    InvalidServerId,
    DuplicateValue,
    TrailingBodyBytes,
    SignatureVerificationFailed,
};

struct EndpointManifestText final {
    char bytes[EndpointManifestMaximumDnsBytes + 1]{};
    std::uint16_t length = 0;
};

struct EndpointManifestAudience final {
    char bytes[EndpointManifestMaximumAudienceBytes + 1]{};
    std::uint8_t length = 0;
};

struct EndpointManifestPublicKey final {
    std::uint8_t x[32]{};
    std::uint8_t y[32]{};
};

struct EndpointManifest final {
    EndpointManifestEnvironment environment =
        EndpointManifestEnvironment::Development;
    std::uint8_t flags = 0;
    std::uint16_t publicKeyId = 0;
    std::uint64_t sequence = 0;
    std::uint64_t notBeforeUnixSeconds = 0;
    std::uint64_t notAfterUnixSeconds = 0;
    std::uint16_t logicalLoginPort = 0;
    std::uint16_t tlsLoginPort = 0;
    EndpointManifestText logicalLoginHost{};
    EndpointManifestText tlsLoginHost{};
    std::uint8_t gameSuffixCount = 0;
    EndpointManifestText
        gameSuffixes[EndpointManifestMaximumGameSuffixes]{};
    std::uint8_t audienceCount = 0;
    EndpointManifestAudience
        audiences[EndpointManifestMaximumAudiences]{};
    std::uint8_t serverIdCount = 0;
    std::uint32_t serverIds[EndpointManifestMaximumServerIds]{};
};

using EndpointManifestPublicKeyLookup = bool (*)(
    void* context,
    std::uint16_t publicKeyId,
    EndpointManifestPublicKey* publicKey) noexcept;

using EndpointManifestSequenceFloorLookup = bool (*)(
    void* context,
    EndpointManifestEnvironment environment,
    std::uint64_t* compiledMinimum,
    std::uint64_t* installedMinimum) noexcept;

using EndpointManifestClock = bool (*)(
    void* context,
    std::uint64_t* unixSeconds) noexcept;

struct EndpointManifestValidationContext final {
    void* context = nullptr;
    EndpointManifestPublicKeyLookup publicKeyLookup = nullptr;
    EndpointManifestSequenceFloorLookup sequenceFloorLookup = nullptr;
    EndpointManifestClock clock = nullptr;
    EndpointManifestEnvironment expectedEnvironment =
        EndpointManifestEnvironment::Production;
};

EndpointManifestError ParseAndVerifyEndpointManifest(
    const std::uint8_t* bytes,
    std::size_t byteCount,
    const EndpointManifestValidationContext& validation,
    EndpointManifest* manifest) noexcept;

bool EndpointManifestAllowsGameHost(
    const EndpointManifest& manifest,
    const char* canonicalDnsHost,
    std::size_t hostLength) noexcept;

bool EndpointManifestAllowsAudience(
    const EndpointManifest& manifest,
    const char* audience,
    std::size_t audienceLength) noexcept;

bool EndpointManifestAllowsServerId(
    const EndpointManifest& manifest,
    std::uint32_t serverId) noexcept;

} // namespace godswar::network
