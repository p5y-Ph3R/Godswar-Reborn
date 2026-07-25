#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecureUdpSha256Bytes = 32;
inline constexpr std::size_t SecureUdpAes256KeyBytes = 32;
inline constexpr std::size_t SecureUdpGcmNonceBytes = 12;
inline constexpr std::size_t SecureUdpGcmTagBytes = 16;

bool SecureUdpHmacSha256(
    const void* key,
    std::size_t keyBytes,
    const void* input,
    std::size_t inputBytes,
    std::uint8_t* destination,
    std::size_t destinationBytes) noexcept;

// RFC 5869 HKDF-SHA256. Native UDP keys are deliberately small; this bounded
// helper refuses oversized context or output instead of allocating.
bool SecureUdpHkdfSha256(
    const void* inputKeyMaterial,
    std::size_t inputKeyMaterialBytes,
    const void* salt,
    std::size_t saltBytes,
    const void* context,
    std::size_t contextBytes,
    std::uint8_t* destination,
    std::size_t destinationBytes) noexcept;

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
    std::size_t tagBytes) noexcept;

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
    std::size_t plaintextCapacity) noexcept;

} // namespace godswar::network
