#include "EndpointManifestCrypto.h"

#include <Windows.h>
#include <bcrypt.h>

#include <climits>
#include <cstddef>
#include <cstdint>
#include <cstring>

#pragma comment(lib, "bcrypt.lib")

namespace godswar::network {

bool VerifyEndpointManifestSignature(
    const std::uint8_t* signedBytes,
    std::size_t signedByteCount,
    const std::uint8_t* signature,
    const EndpointManifestPublicKey& publicKey) noexcept {
    if (signedBytes == nullptr ||
        signature == nullptr ||
        signedByteCount > ULONG_MAX) {
        return false;
    }

    BCRYPT_ALG_HANDLE hashAlgorithm = nullptr;
    BCRYPT_ALG_HANDLE signatureAlgorithm = nullptr;
    BCRYPT_KEY_HANDLE key = nullptr;
    std::uint8_t hash[32]{};

    struct PublicKeyBlob final {
        BCRYPT_ECCKEY_BLOB header{};
        std::uint8_t coordinates[64]{};
    } blob{};
    blob.header.dwMagic = BCRYPT_ECDSA_PUBLIC_P256_MAGIC;
    blob.header.cbKey = 32;
    std::memcpy(blob.coordinates, publicKey.x, sizeof(publicKey.x));
    std::memcpy(
        blob.coordinates + sizeof(publicKey.x),
        publicKey.y,
        sizeof(publicKey.y));

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
            const_cast<PUCHAR>(signedBytes),
            static_cast<ULONG>(signedByteCount),
            hash,
            static_cast<ULONG>(sizeof(hash))) >= 0 &&
        BCryptOpenAlgorithmProvider(
            &signatureAlgorithm,
            BCRYPT_ECDSA_P256_ALGORITHM,
            nullptr,
            0) >= 0 &&
        BCryptImportKeyPair(
            signatureAlgorithm,
            nullptr,
            BCRYPT_ECCPUBLIC_BLOB,
            &key,
            reinterpret_cast<PUCHAR>(&blob),
            static_cast<ULONG>(sizeof(blob)),
            0) >= 0 &&
        BCryptVerifySignature(
            key,
            nullptr,
            hash,
            static_cast<ULONG>(sizeof(hash)),
            const_cast<PUCHAR>(signature),
            static_cast<ULONG>(EndpointManifestSignatureBytes),
            0) >= 0;

    SecureZeroMemory(hash, sizeof(hash));
    SecureZeroMemory(&blob, sizeof(blob));
    if (key != nullptr) {
        static_cast<void>(BCryptDestroyKey(key));
    }
    if (signatureAlgorithm != nullptr) {
        static_cast<void>(
            BCryptCloseAlgorithmProvider(signatureAlgorithm, 0));
    }
    if (hashAlgorithm != nullptr) {
        static_cast<void>(
            BCryptCloseAlgorithmProvider(hashAlgorithm, 0));
    }
    return succeeded;
}

} // namespace godswar::network
