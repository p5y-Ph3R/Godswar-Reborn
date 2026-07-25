#pragma once

#include "../src/EndpointManifest.h"

#include <Windows.h>
#include <bcrypt.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network::tests {

inline constexpr std::uint16_t TestCurrentKeyId = 7;
inline constexpr std::uint16_t TestNextKeyId = 8;
inline constexpr std::uint64_t TestManifestNow = 2'000'000'000ULL;

void WriteTestUint16(
    std::uint8_t* bytes,
    std::uint16_t value) noexcept;

void WriteTestUint32(
    std::uint8_t* bytes,
    std::uint32_t value) noexcept;

void WriteTestUint64(
    std::uint8_t* bytes,
    std::uint64_t value) noexcept;

class EndpointManifestTestSigner final {
public:
    EndpointManifestTestSigner() noexcept;
    ~EndpointManifestTestSigner();

    EndpointManifestTestSigner(
        const EndpointManifestTestSigner&) = delete;
    EndpointManifestTestSigner& operator=(
        const EndpointManifestTestSigner&) = delete;

    bool IsValid() const noexcept;
    const EndpointManifestPublicKey& PublicKey() const noexcept;
    bool Sign(
        const std::uint8_t* bytes,
        std::size_t byteCount,
        std::uint8_t* signature) const noexcept;

private:
    BCRYPT_ALG_HANDLE algorithm_ = nullptr;
    BCRYPT_KEY_HANDLE key_ = nullptr;
    EndpointManifestPublicKey publicKey_{};
    bool valid_ = false;
};

struct EndpointManifestTestBytes final {
    std::uint8_t bytes[EndpointManifestMaximumBytes]{};
    std::size_t byteCount = 0;
    std::size_t signedByteCount = 0;
    std::size_t logicalHostOffset = 0;
    std::size_t tlsHostOffset = 0;
    std::size_t suffixOffsets[2]{};
    std::size_t audienceOffsets[2]{};
    std::size_t serverIdOffsets[2]{};
};

bool SignTestManifest(
    EndpointManifestTestBytes* manifest,
    const EndpointManifestTestSigner& signer) noexcept;

bool BuildTestManifest(
    EndpointManifestTestBytes* manifest,
    const EndpointManifestTestSigner& signer,
    EndpointManifestEnvironment environment =
        EndpointManifestEnvironment::Production,
    std::uint16_t keyId = TestCurrentKeyId) noexcept;

struct EndpointManifestValidationFixture final {
    EndpointManifestPublicKey publicKey{};
    std::uint64_t now = TestManifestNow;
    std::uint64_t compiledMinimum = 10;
    std::uint64_t installedMinimum = 11;
    bool keyLookupSucceeds = true;
    bool sequenceLookupSucceeds = true;
    bool clockSucceeds = true;
};

EndpointManifestValidationContext MakeTestValidation(
    EndpointManifestValidationFixture* fixture,
    EndpointManifestEnvironment environment =
        EndpointManifestEnvironment::Production) noexcept;

} // namespace godswar::network::tests
