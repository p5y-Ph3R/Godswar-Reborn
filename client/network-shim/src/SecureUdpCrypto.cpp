#include "SecureUdpCrypto.h"

#include <Windows.h>
#include <bcrypt.h>

#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

constexpr std::size_t MaximumHkdfContextBytes = 128;
constexpr std::size_t MaximumHkdfOutputBytes = 64;
constexpr ULONG MaximumCngKeyObjectBytes = 16 * 1024;

bool FitsUlong(std::size_t value) noexcept {
    return value <= (std::numeric_limits<ULONG>::max)();
}

class AlgorithmHandle final {
public:
    ~AlgorithmHandle() noexcept {
        if (value != nullptr) {
            BCryptCloseAlgorithmProvider(value, 0);
        }
    }

    BCRYPT_ALG_HANDLE value = nullptr;
};

class KeyHandle final {
public:
    ~KeyHandle() noexcept {
        Reset();
    }

    void Reset() noexcept {
        if (value != nullptr) {
            BCryptDestroyKey(value);
            value = nullptr;
        }
    }

    BCRYPT_KEY_HANDLE value = nullptr;
};

bool OpenAesGcm(
    AlgorithmHandle* algorithm,
    ULONG* keyObjectBytes) noexcept {
    if (algorithm == nullptr || keyObjectBytes == nullptr) {
        return false;
    }
    *keyObjectBytes = 0;
    if (BCryptOpenAlgorithmProvider(
            &algorithm->value,
            BCRYPT_AES_ALGORITHM,
            nullptr,
            0) < 0 ||
        BCryptSetProperty(
            algorithm->value,
            BCRYPT_CHAINING_MODE,
            reinterpret_cast<PUCHAR>(
                const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_GCM)),
            sizeof(BCRYPT_CHAIN_MODE_GCM),
            0) < 0) {
        return false;
    }

    ULONG resultBytes = 0;
    return BCryptGetProperty(
               algorithm->value,
               BCRYPT_OBJECT_LENGTH,
               reinterpret_cast<PUCHAR>(keyObjectBytes),
               sizeof(*keyObjectBytes),
               &resultBytes,
               0) >= 0 &&
        resultBytes == sizeof(*keyObjectBytes) &&
        *keyObjectBytes != 0 &&
        *keyObjectBytes <= MaximumCngKeyObjectBytes;
}

bool TransformAesGcm(
    bool encrypt,
    const std::uint8_t* key,
    std::size_t keyBytes,
    const std::uint8_t* nonce,
    std::size_t nonceBytes,
    const void* authenticatedData,
    std::size_t authenticatedDataBytes,
    const void* input,
    std::size_t inputBytes,
    void* output,
    std::size_t outputCapacity,
    std::uint8_t* tag,
    std::size_t tagBytes) noexcept {
    if (key == nullptr ||
        keyBytes != SecureUdpAes256KeyBytes ||
        nonce == nullptr ||
        nonceBytes != SecureUdpGcmNonceBytes ||
        (authenticatedData == nullptr &&
            authenticatedDataBytes != 0) ||
        (input == nullptr && inputBytes != 0) ||
        (output == nullptr && inputBytes != 0) ||
        outputCapacity < inputBytes ||
        tag == nullptr ||
        tagBytes != SecureUdpGcmTagBytes ||
        !FitsUlong(authenticatedDataBytes) ||
        !FitsUlong(inputBytes)) {
        return false;
    }

    AlgorithmHandle algorithm;
    ULONG keyObjectBytes = 0;
    if (!OpenAesGcm(&algorithm, &keyObjectBytes)) {
        return false;
    }

    auto* keyObject = static_cast<std::uint8_t*>(
        HeapAlloc(GetProcessHeap(), 0, keyObjectBytes));
    if (keyObject == nullptr) {
        return false;
    }

    KeyHandle keyHandle;
    bool success = false;
    if (BCryptGenerateSymmetricKey(
            algorithm.value,
            &keyHandle.value,
            keyObject,
            keyObjectBytes,
            const_cast<PUCHAR>(key),
            static_cast<ULONG>(keyBytes),
            0) >= 0) {
        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authentication{};
        BCRYPT_INIT_AUTH_MODE_INFO(authentication);
        authentication.pbNonce = const_cast<PUCHAR>(nonce);
        authentication.cbNonce = static_cast<ULONG>(nonceBytes);
        authentication.pbAuthData = static_cast<PUCHAR>(
            const_cast<void*>(authenticatedData));
        authentication.cbAuthData =
            static_cast<ULONG>(authenticatedDataBytes);
        authentication.pbTag = tag;
        authentication.cbTag = static_cast<ULONG>(tagBytes);

        ULONG transformedBytes = 0;
        const auto status = encrypt
            ? BCryptEncrypt(
                  keyHandle.value,
                  static_cast<PUCHAR>(const_cast<void*>(input)),
                  static_cast<ULONG>(inputBytes),
                  &authentication,
                  nullptr,
                  0,
                  static_cast<PUCHAR>(output),
                  static_cast<ULONG>(inputBytes),
                  &transformedBytes,
                  0)
            : BCryptDecrypt(
                  keyHandle.value,
                  static_cast<PUCHAR>(const_cast<void*>(input)),
                  static_cast<ULONG>(inputBytes),
                  &authentication,
                  nullptr,
                  0,
                  static_cast<PUCHAR>(output),
                  static_cast<ULONG>(inputBytes),
                  &transformedBytes,
                  0);
        success = status >= 0 && transformedBytes == inputBytes;
    }

    keyHandle.Reset();
    SecureZeroMemory(keyObject, keyObjectBytes);
    HeapFree(GetProcessHeap(), 0, keyObject);
    if (!success && output != nullptr && inputBytes != 0) {
        SecureZeroMemory(output, inputBytes);
    }
    return success;
}

} // namespace

bool SecureUdpHmacSha256(
    const void* key,
    std::size_t keyBytes,
    const void* input,
    std::size_t inputBytes,
    std::uint8_t* destination,
    std::size_t destinationBytes) noexcept {
    if ((key == nullptr && keyBytes != 0) ||
        (input == nullptr && inputBytes != 0) ||
        destination == nullptr ||
        destinationBytes < SecureUdpSha256Bytes ||
        !FitsUlong(keyBytes) ||
        !FitsUlong(inputBytes)) {
        return false;
    }

    AlgorithmHandle algorithm;
    if (BCryptOpenAlgorithmProvider(
            &algorithm.value,
            BCRYPT_SHA256_ALGORITHM,
            nullptr,
            BCRYPT_ALG_HANDLE_HMAC_FLAG) < 0) {
        SecureZeroMemory(destination, SecureUdpSha256Bytes);
        return false;
    }
    const auto status = BCryptHash(
        algorithm.value,
        static_cast<PUCHAR>(const_cast<void*>(key)),
        static_cast<ULONG>(keyBytes),
        static_cast<PUCHAR>(const_cast<void*>(input)),
        static_cast<ULONG>(inputBytes),
        destination,
        static_cast<ULONG>(SecureUdpSha256Bytes));
    if (status < 0) {
        SecureZeroMemory(destination, SecureUdpSha256Bytes);
        return false;
    }
    return true;
}

bool SecureUdpHkdfSha256(
    const void* inputKeyMaterial,
    std::size_t inputKeyMaterialBytes,
    const void* salt,
    std::size_t saltBytes,
    const void* context,
    std::size_t contextBytes,
    std::uint8_t* destination,
    std::size_t destinationBytes) noexcept {
    if (inputKeyMaterial == nullptr ||
        inputKeyMaterialBytes == 0 ||
        (salt == nullptr && saltBytes != 0) ||
        (context == nullptr && contextBytes != 0) ||
        contextBytes > MaximumHkdfContextBytes ||
        destination == nullptr ||
        destinationBytes == 0 ||
        destinationBytes > MaximumHkdfOutputBytes) {
        return false;
    }

    if (!FitsUlong(inputKeyMaterialBytes) ||
        !FitsUlong(saltBytes) ||
        !FitsUlong(contextBytes) ||
        !FitsUlong(destinationBytes)) {
        return false;
    }

    AlgorithmHandle algorithm;
    ULONG keyObjectBytes = 0;
    ULONG propertyBytes = 0;
    if (BCryptOpenAlgorithmProvider(
            &algorithm.value,
            BCRYPT_HKDF_ALGORITHM,
            nullptr,
            0) < 0 ||
        BCryptGetProperty(
            algorithm.value,
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&keyObjectBytes),
            sizeof(keyObjectBytes),
            &propertyBytes,
            0) < 0 ||
        propertyBytes != sizeof(keyObjectBytes) ||
        keyObjectBytes == 0 ||
        keyObjectBytes > MaximumCngKeyObjectBytes) {
        SecureZeroMemory(destination, destinationBytes);
        return false;
    }

    auto* keyObject = static_cast<std::uint8_t*>(
        HeapAlloc(GetProcessHeap(), 0, keyObjectBytes));
    if (keyObject == nullptr) {
        SecureZeroMemory(destination, destinationBytes);
        return false;
    }

    KeyHandle keyHandle;
    bool success = BCryptGenerateSymmetricKey(
        algorithm.value,
        &keyHandle.value,
        keyObject,
        keyObjectBytes,
        static_cast<PUCHAR>(
            const_cast<void*>(inputKeyMaterial)),
        static_cast<ULONG>(inputKeyMaterialBytes),
        0) >= 0;
    if (success) {
        success = BCryptSetProperty(
            keyHandle.value,
            BCRYPT_HKDF_HASH_ALGORITHM,
            reinterpret_cast<PUCHAR>(
                const_cast<wchar_t*>(
                    BCRYPT_SHA256_ALGORITHM)),
            sizeof(BCRYPT_SHA256_ALGORITHM),
            0) >= 0;
    }
    if (success) {
        success = BCryptSetProperty(
            keyHandle.value,
            BCRYPT_HKDF_SALT_AND_FINALIZE,
            static_cast<PUCHAR>(const_cast<void*>(salt)),
            static_cast<ULONG>(saltBytes),
            0) >= 0;
    }
    if (success) {
        BCryptBuffer parameter{};
        parameter.BufferType = KDF_HKDF_INFO;
        parameter.cbBuffer = static_cast<ULONG>(contextBytes);
        parameter.pvBuffer = const_cast<void*>(context);
        BCryptBufferDesc parameters{};
        parameters.ulVersion = BCRYPTBUFFER_VERSION;
        parameters.cBuffers = 1;
        parameters.pBuffers = &parameter;
        ULONG derivedBytes = 0;
        success = BCryptKeyDerivation(
            keyHandle.value,
            &parameters,
            destination,
            static_cast<ULONG>(destinationBytes),
            &derivedBytes,
            0) >= 0 &&
            derivedBytes == destinationBytes;
    }

    keyHandle.Reset();
    SecureZeroMemory(keyObject, keyObjectBytes);
    HeapFree(GetProcessHeap(), 0, keyObject);
    if (!success) {
        SecureZeroMemory(destination, destinationBytes);
    }
    return success;
}

bool SecureUdpAes256GcmSeal(
    const std::uint8_t* key,
    std::size_t keyBytes,
    const std::uint8_t* nonce,
    std::size_t nonceBytes,
    const void* authenticatedData,
    std::size_t authenticatedDataBytes,
    const void* plaintext,
    std::size_t plaintextBytes,
    void* ciphertext,
    std::size_t ciphertextCapacity,
    std::uint8_t* tag,
    std::size_t tagBytes) noexcept {
    return TransformAesGcm(
        true,
        key,
        keyBytes,
        nonce,
        nonceBytes,
        authenticatedData,
        authenticatedDataBytes,
        plaintext,
        plaintextBytes,
        ciphertext,
        ciphertextCapacity,
        tag,
        tagBytes);
}

bool SecureUdpAes256GcmOpen(
    const std::uint8_t* key,
    std::size_t keyBytes,
    const std::uint8_t* nonce,
    std::size_t nonceBytes,
    const void* authenticatedData,
    std::size_t authenticatedDataBytes,
    const void* ciphertext,
    std::size_t ciphertextBytes,
    const std::uint8_t* tag,
    std::size_t tagBytes,
    void* plaintext,
    std::size_t plaintextCapacity) noexcept {
    return TransformAesGcm(
        false,
        key,
        keyBytes,
        nonce,
        nonceBytes,
        authenticatedData,
        authenticatedDataBytes,
        ciphertext,
        ciphertextBytes,
        plaintext,
        plaintextCapacity,
        const_cast<std::uint8_t*>(tag),
        tagBytes);
}

} // namespace godswar::network
