#include "SecureUdpBindingGrant.h"

#include <Windows.h>

#include <cstring>
#include <utility>

namespace godswar::network {
namespace {

constexpr std::uint8_t GrantMagic[] = {'G', 'W', 'U', 'G'};
constexpr std::uint16_t GrantMajor = 1;
constexpr std::uint16_t GrantMinor = 0;
constexpr std::uint16_t KnownCapabilityFlags =
    static_cast<std::uint16_t>(
        SecureUdpBindingCapability::AuthoritativeMovement);

std::uint16_t ReadUInt16(const std::uint8_t* source) noexcept {
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(source[0]) << 8U) |
        source[1]);
}

std::uint32_t ReadUInt32(const std::uint8_t* source) noexcept {
    return
        (static_cast<std::uint32_t>(source[0]) << 24U) |
        (static_cast<std::uint32_t>(source[1]) << 16U) |
        (static_cast<std::uint32_t>(source[2]) << 8U) |
        source[3];
}

std::uint64_t ReadUInt64(const std::uint8_t* source) noexcept {
    std::uint64_t value = 0;
    for (std::size_t index = 0; index < 8; ++index) {
        value = (value << 8U) | source[index];
    }
    return value;
}

bool IsAllZero(
    const std::uint8_t* bytes,
    std::size_t byteCount) noexcept {
    if (bytes == nullptr || byteCount == 0) {
        return true;
    }
    std::uint8_t combined = 0;
    for (std::size_t index = 0; index < byteCount; ++index) {
        combined |= bytes[index];
    }
    return combined == 0;
}

} // namespace

SecureUdpBindingGrant::~SecureUdpBindingGrant() noexcept {
    Clear();
}

SecureUdpBindingGrant::SecureUdpBindingGrant(
    SecureUdpBindingGrant&& other) noexcept {
    MoveFrom(&other);
}

SecureUdpBindingGrant& SecureUdpBindingGrant::operator=(
    SecureUdpBindingGrant&& other) noexcept {
    if (this != &other) {
        Clear();
        MoveFrom(&other);
    }
    return *this;
}

bool SecureUdpBindingGrant::IsValid() const noexcept {
    return valid_;
}

std::uint16_t SecureUdpBindingGrant::UdpPort() const noexcept {
    return valid_ ? udpPort_ : 0;
}

std::uint16_t
SecureUdpBindingGrant::CapabilityFlags() const noexcept {
    return valid_ ? capabilityFlags_ : 0;
}

bool SecureUdpBindingGrant::HasCapability(
    SecureUdpBindingCapability capability) const noexcept {
    const auto requested =
        static_cast<std::uint16_t>(capability);
    return valid_ &&
        requested != 0 &&
        (requested & ~KnownCapabilityFlags) == 0 &&
        (capabilityFlags_ & requested) == requested;
}

std::uint32_t SecureUdpBindingGrant::ServerId() const noexcept {
    return valid_ ? serverId_ : 0;
}

std::uint64_t
SecureUdpBindingGrant::ExpiryUnixMilliseconds() const noexcept {
    return valid_ ? expiryUnixMilliseconds_ : 0;
}

bool SecureUdpBindingGrant::ConnectionIdEquals(
    const std::uint8_t* connectionId,
    std::size_t connectionIdBytes) const noexcept {
    if (!valid_ ||
        connectionId == nullptr ||
        connectionIdBytes != sizeof(connectionId_)) {
        return false;
    }
    std::uint8_t difference = 0;
    for (std::size_t index = 0;
         index < sizeof(connectionId_);
         ++index) {
        difference |= static_cast<std::uint8_t>(
            connectionId_[index] ^ connectionId[index]);
    }
    return difference == 0;
}

bool SecureUdpBindingGrant::TryCopyConnectionId(
    std::uint8_t* destination,
    std::size_t destinationBytes) const noexcept {
    if (destination == nullptr ||
        destinationBytes < sizeof(connectionId_)) {
        return false;
    }
    if (!valid_) {
        SecureZeroMemory(destination, sizeof(connectionId_));
        return false;
    }
    std::memcpy(destination, connectionId_, sizeof(connectionId_));
    return true;
}

bool SecureUdpBindingGrant::TryCopyProofKey(
    std::uint8_t* destination,
    std::size_t destinationBytes) const noexcept {
    if (destination == nullptr ||
        destinationBytes < sizeof(proofKey_)) {
        return false;
    }
    if (!valid_) {
        SecureZeroMemory(destination, sizeof(proofKey_));
        return false;
    }
    std::memcpy(destination, proofKey_, sizeof(proofKey_));
    return true;
}

void SecureUdpBindingGrant::Clear() noexcept {
    SecureZeroMemory(connectionId_, sizeof(connectionId_));
    SecureZeroMemory(proofKey_, sizeof(proofKey_));
    udpPort_ = 0;
    capabilityFlags_ = 0;
    serverId_ = 0;
    expiryUnixMilliseconds_ = 0;
    valid_ = false;
}

void SecureUdpBindingGrant::MoveFrom(
    SecureUdpBindingGrant* other) noexcept {
    if (other == nullptr || !other->valid_) {
        return;
    }
    udpPort_ = other->udpPort_;
    capabilityFlags_ = other->capabilityFlags_;
    serverId_ = other->serverId_;
    expiryUnixMilliseconds_ = other->expiryUnixMilliseconds_;
    std::memcpy(
        connectionId_,
        other->connectionId_,
        sizeof(connectionId_));
    std::memcpy(proofKey_, other->proofKey_, sizeof(proofKey_));
    valid_ = true;
    other->Clear();
}

bool TryDecodeSecureUdpBindingGrant(
    const void* source,
    std::size_t sourceBytes,
    SecureUdpBindingGrant* grant) noexcept {
    if (grant == nullptr) {
        return false;
    }
    grant->Clear();
    if (source == nullptr ||
        sourceBytes != SecureUdpBindingGrantBytes) {
        return false;
    }

    const auto* input = static_cast<const std::uint8_t*>(source);
    const auto udpPort = ReadUInt16(input + 8);
    const auto capabilityFlags = ReadUInt16(input + 10);
    const auto serverId = ReadUInt32(input + 12);
    const auto expiry = ReadUInt64(input + 16);
    if (std::memcmp(input, GrantMagic, sizeof(GrantMagic)) != 0 ||
        ReadUInt16(input + 4) != GrantMajor ||
        ReadUInt16(input + 6) != GrantMinor ||
        udpPort == 0 ||
        (capabilityFlags & ~KnownCapabilityFlags) != 0 ||
        serverId == 0 ||
        expiry == 0 ||
        IsAllZero(input + 24, SecureUdpConnectionIdBytes) ||
        IsAllZero(input + 40, SecureUdpProofKeyBytes)) {
        return false;
    }

    SecureUdpBindingGrant decoded;
    decoded.udpPort_ = udpPort;
    decoded.capabilityFlags_ = capabilityFlags;
    decoded.serverId_ = serverId;
    decoded.expiryUnixMilliseconds_ = expiry;
    std::memcpy(
        decoded.connectionId_,
        input + 24,
        sizeof(decoded.connectionId_));
    std::memcpy(
        decoded.proofKey_,
        input + 40,
        sizeof(decoded.proofKey_));
    decoded.valid_ = true;
    *grant = std::move(decoded);
    return true;
}

} // namespace godswar::network
