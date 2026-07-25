#pragma once

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecureUdpBindingGrantBytes = 72;
inline constexpr std::size_t SecureUdpConnectionIdBytes = 16;
inline constexpr std::size_t SecureUdpProofKeyBytes = 32;

// Owns the secret material delivered over the authenticated game TLS channel.
// Copying is forbidden so every proof-key copy has an explicit owner.
class SecureUdpBindingGrant final {
public:
    SecureUdpBindingGrant() noexcept = default;
    ~SecureUdpBindingGrant() noexcept;

    SecureUdpBindingGrant(
        const SecureUdpBindingGrant&) = delete;
    SecureUdpBindingGrant& operator=(
        const SecureUdpBindingGrant&) = delete;

    SecureUdpBindingGrant(
        SecureUdpBindingGrant&& other) noexcept;
    SecureUdpBindingGrant& operator=(
        SecureUdpBindingGrant&& other) noexcept;

    bool IsValid() const noexcept;
    std::uint16_t UdpPort() const noexcept;
    std::uint32_t ServerId() const noexcept;
    std::uint64_t ExpiryUnixMilliseconds() const noexcept;

    bool ConnectionIdEquals(
        const std::uint8_t* connectionId,
        std::size_t connectionIdBytes) const noexcept;
    bool TryCopyConnectionId(
        std::uint8_t* destination,
        std::size_t destinationBytes) const noexcept;
    // The caller owns and must erase every successful proof-key copy.
    bool TryCopyProofKey(
        std::uint8_t* destination,
        std::size_t destinationBytes) const noexcept;

    void Clear() noexcept;

private:
    friend bool TryDecodeSecureUdpBindingGrant(
        const void*,
        std::size_t,
        SecureUdpBindingGrant*) noexcept;

    void MoveFrom(SecureUdpBindingGrant* other) noexcept;

    std::uint16_t udpPort_ = 0;
    std::uint32_t serverId_ = 0;
    std::uint64_t expiryUnixMilliseconds_ = 0;
    std::uint8_t connectionId_[SecureUdpConnectionIdBytes]{};
    std::uint8_t proofKey_[SecureUdpProofKeyBytes]{};
    bool valid_ = false;
};

bool TryDecodeSecureUdpBindingGrant(
    const void* source,
    std::size_t sourceBytes,
    SecureUdpBindingGrant* grant) noexcept;

} // namespace godswar::network
