#include "EndpointManifestTestSupport.h"

#include <Windows.h>
#include <bcrypt.h>

#include <climits>
#include <cstddef>
#include <cstdint>
#include <cstring>

#pragma comment(lib, "bcrypt.lib")

namespace godswar::network::tests {
namespace {

std::size_t Append(
    std::uint8_t* destination,
    std::size_t cursor,
    const char* value) noexcept {
    const std::size_t length = std::strlen(value);
    std::memcpy(destination + cursor, value, length);
    return cursor + length;
}

bool LookupKey(
    void* rawContext,
    std::uint16_t keyId,
    EndpointManifestPublicKey* publicKey) noexcept {
    auto* context =
        static_cast<EndpointManifestValidationFixture*>(rawContext);
    if (context == nullptr ||
        publicKey == nullptr ||
        !context->keyLookupSucceeds ||
        (keyId != TestCurrentKeyId && keyId != TestNextKeyId)) {
        return false;
    }
    *publicKey = context->publicKey;
    return true;
}

bool LookupSequenceFloors(
    void* rawContext,
    EndpointManifestEnvironment,
    std::uint64_t* compiledMinimum,
    std::uint64_t* installedMinimum) noexcept {
    auto* context =
        static_cast<EndpointManifestValidationFixture*>(rawContext);
    if (context == nullptr ||
        compiledMinimum == nullptr ||
        installedMinimum == nullptr ||
        !context->sequenceLookupSucceeds) {
        return false;
    }
    *compiledMinimum = context->compiledMinimum;
    *installedMinimum = context->installedMinimum;
    return true;
}

bool ReadClock(
    void* rawContext,
    std::uint64_t* unixSeconds) noexcept {
    auto* context =
        static_cast<EndpointManifestValidationFixture*>(rawContext);
    if (context == nullptr ||
        unixSeconds == nullptr ||
        !context->clockSucceeds) {
        return false;
    }
    *unixSeconds = context->now;
    return true;
}

} // namespace

void WriteTestUint16(
    std::uint8_t* bytes,
    std::uint16_t value) noexcept {
    bytes[0] = static_cast<std::uint8_t>(value >> 8);
    bytes[1] = static_cast<std::uint8_t>(value);
}

void WriteTestUint32(
    std::uint8_t* bytes,
    std::uint32_t value) noexcept {
    for (std::size_t index = 0; index < 4; ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            value >> ((3 - index) * 8));
    }
}

void WriteTestUint64(
    std::uint8_t* bytes,
    std::uint64_t value) noexcept {
    for (std::size_t index = 0; index < 8; ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            value >> ((7 - index) * 8));
    }
}

EndpointManifestTestSigner::EndpointManifestTestSigner() noexcept {
    if (BCryptOpenAlgorithmProvider(
            &algorithm_,
            BCRYPT_ECDSA_P256_ALGORITHM,
            nullptr,
            0) < 0 ||
        BCryptGenerateKeyPair(
            algorithm_,
            &key_,
            256,
            0) < 0 ||
        BCryptFinalizeKeyPair(key_, 0) < 0) {
        return;
    }

    struct PublicBlob final {
        BCRYPT_ECCKEY_BLOB header{};
        std::uint8_t coordinates[64]{};
    } blob{};
    ULONG exported = 0;
    if (BCryptExportKey(
            key_,
            nullptr,
            BCRYPT_ECCPUBLIC_BLOB,
            reinterpret_cast<PUCHAR>(&blob),
            static_cast<ULONG>(sizeof(blob)),
            &exported,
            0) < 0 ||
        exported != sizeof(blob) ||
        blob.header.dwMagic != BCRYPT_ECDSA_PUBLIC_P256_MAGIC ||
        blob.header.cbKey != 32) {
        return;
    }
    std::memcpy(
        publicKey_.x,
        blob.coordinates,
        sizeof(publicKey_.x));
    std::memcpy(
        publicKey_.y,
        blob.coordinates + sizeof(publicKey_.x),
        sizeof(publicKey_.y));
    valid_ = true;
}

EndpointManifestTestSigner::~EndpointManifestTestSigner() {
    if (key_ != nullptr) {
        static_cast<void>(BCryptDestroyKey(key_));
    }
    if (algorithm_ != nullptr) {
        static_cast<void>(
            BCryptCloseAlgorithmProvider(algorithm_, 0));
    }
}

bool EndpointManifestTestSigner::IsValid() const noexcept {
    return valid_;
}

const EndpointManifestPublicKey&
EndpointManifestTestSigner::PublicKey() const noexcept {
    return publicKey_;
}

bool EndpointManifestTestSigner::Sign(
    const std::uint8_t* bytes,
    std::size_t byteCount,
    std::uint8_t* signature) const noexcept {
    if (!valid_ ||
        bytes == nullptr ||
        signature == nullptr ||
        byteCount > ULONG_MAX) {
        return false;
    }

    BCRYPT_ALG_HANDLE hashAlgorithm = nullptr;
    std::uint8_t hash[32]{};
    ULONG signatureBytes = 0;
    const bool succeeded =
        BCryptOpenAlgorithmProvider(
            &hashAlgorithm,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            0) >= 0 &&
        BCryptHash(
            hashAlgorithm,
            nullptr,
            0,
            const_cast<PUCHAR>(bytes),
            static_cast<ULONG>(byteCount),
            hash,
            static_cast<ULONG>(sizeof(hash))) >= 0 &&
        BCryptSignHash(
            key_,
            nullptr,
            hash,
            static_cast<ULONG>(sizeof(hash)),
            signature,
            64,
            &signatureBytes,
            0) >= 0 &&
        signatureBytes == 64;
    SecureZeroMemory(hash, sizeof(hash));
    if (hashAlgorithm != nullptr) {
        static_cast<void>(
            BCryptCloseAlgorithmProvider(hashAlgorithm, 0));
    }
    return succeeded;
}

bool SignTestManifest(
    EndpointManifestTestBytes* manifest,
    const EndpointManifestTestSigner& signer) noexcept {
    if (manifest == nullptr) {
        return false;
    }
    return signer.Sign(
        manifest->bytes,
        manifest->signedByteCount,
        manifest->bytes + manifest->signedByteCount);
}

bool BuildTestManifest(
    EndpointManifestTestBytes* manifest,
    const EndpointManifestTestSigner& signer,
    EndpointManifestEnvironment environment,
    std::uint16_t keyId) noexcept {
    if (manifest == nullptr) {
        return false;
    }
    *manifest = EndpointManifestTestBytes{};
    auto& bytes = manifest->bytes;
    std::memcpy(bytes, "GWEM", 4);
    WriteTestUint16(bytes + 8, 72);
    WriteTestUint16(bytes + 10, 1);
    WriteTestUint16(bytes + 12, 0);
    bytes[14] = static_cast<std::uint8_t>(environment);
    WriteTestUint16(bytes + 16, 1);
    WriteTestUint16(bytes + 18, keyId);
    WriteTestUint64(bytes + 24, 12);
    WriteTestUint64(bytes + 32, TestManifestNow - 60);
    WriteTestUint64(bytes + 40, TestManifestNow + 60);
    WriteTestUint16(bytes + 48, 1);
    WriteTestUint16(bytes + 50, 0);
    WriteTestUint16(bytes + 52, 5999);
    WriteTestUint16(bytes + 54, 6599);

    constexpr char logicalHost[] = "127.0.0.1";
    constexpr char tlsHost[] = "login.reborn.test";
    constexpr char suffixOne[] = "reborn.test";
    constexpr char suffixTwo[] = "staged.test";
    constexpr char audienceOne[] = "reborn-game";
    constexpr char audienceTwo[] = "reborn_test";
    WriteTestUint16(
        bytes + 56,
        static_cast<std::uint16_t>(sizeof(logicalHost) - 1));
    WriteTestUint16(
        bytes + 58,
        static_cast<std::uint16_t>(sizeof(tlsHost) - 1));
    bytes[60] = 2;
    bytes[61] = 2;
    bytes[62] = 2;

    std::size_t cursor = 72;
    manifest->logicalHostOffset = cursor;
    cursor = Append(bytes, cursor, logicalHost);
    manifest->tlsHostOffset = cursor;
    cursor = Append(bytes, cursor, tlsHost);

    bytes[cursor++] =
        static_cast<std::uint8_t>(sizeof(suffixOne) - 1);
    manifest->suffixOffsets[0] = cursor;
    cursor = Append(bytes, cursor, suffixOne);
    bytes[cursor++] =
        static_cast<std::uint8_t>(sizeof(suffixTwo) - 1);
    manifest->suffixOffsets[1] = cursor;
    cursor = Append(bytes, cursor, suffixTwo);

    bytes[cursor++] =
        static_cast<std::uint8_t>(sizeof(audienceOne) - 1);
    manifest->audienceOffsets[0] = cursor;
    cursor = Append(bytes, cursor, audienceOne);
    bytes[cursor++] =
        static_cast<std::uint8_t>(sizeof(audienceTwo) - 1);
    manifest->audienceOffsets[1] = cursor;
    cursor = Append(bytes, cursor, audienceTwo);

    manifest->serverIdOffsets[0] = cursor;
    WriteTestUint32(bytes + cursor, 100);
    cursor += 4;
    manifest->serverIdOffsets[1] = cursor;
    WriteTestUint32(bytes + cursor, 200);
    cursor += 4;

    manifest->signedByteCount = cursor;
    manifest->byteCount = cursor + EndpointManifestSignatureBytes;
    WriteTestUint32(
        bytes + 4,
        static_cast<std::uint32_t>(manifest->byteCount));
    WriteTestUint32(
        bytes + 64,
        static_cast<std::uint32_t>(manifest->signedByteCount));
    return SignTestManifest(manifest, signer);
}

EndpointManifestValidationContext MakeTestValidation(
    EndpointManifestValidationFixture* fixture,
    EndpointManifestEnvironment environment) noexcept {
    EndpointManifestValidationContext validation{};
    validation.context = fixture;
    validation.publicKeyLookup = LookupKey;
    validation.sequenceFloorLookup = LookupSequenceFloors;
    validation.clock = ReadClock;
    validation.expectedEnvironment = environment;
    return validation;
}

} // namespace godswar::network::tests
