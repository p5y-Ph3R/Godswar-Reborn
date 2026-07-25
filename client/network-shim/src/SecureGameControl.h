#pragma once

#include "SecureClientProtocol.h"

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecureGameRouteHostMaximumBytes = 23;
inline constexpr std::size_t SecureGameTlsHostMaximumBytes = 253;
inline constexpr std::size_t SecureGameAudienceMaximumBytes = 64;

enum class SecureBindStatus : std::uint16_t {
    Accepted = 0,
    Rejected = 1,
    ServerBusy = 2,
    PolicyRejected = 3,
};

// Owns the only client-side copy of one opaque game ticket while it moves
// between the login control stream, the process grant registry, and a game
// bind attempt. It is deliberately noncopyable. Moving transfers and wipes the
// source; destruction wipes all fields.
class SecureGameGrant final {
public:
    SecureGameGrant() noexcept = default;
    ~SecureGameGrant() noexcept;

    SecureGameGrant(const SecureGameGrant&) = delete;
    SecureGameGrant& operator=(const SecureGameGrant&) = delete;

    SecureGameGrant(SecureGameGrant&& other) noexcept;
    SecureGameGrant& operator=(SecureGameGrant&& other) noexcept;

    bool IsValid() const noexcept;
    const char* RouteHost() const noexcept;
    std::size_t RouteHostLength() const noexcept;
    const char* TlsHost() const noexcept;
    std::size_t TlsHostLength() const noexcept;
    const char* Audience() const noexcept;
    std::size_t AudienceLength() const noexcept;
    std::uint16_t RoutePort() const noexcept;
    std::uint16_t TlsPort() const noexcept;
    std::uint32_t TargetServerId() const noexcept;
    std::uint64_t ExpiryUnixMilliseconds() const noexcept;

    bool TryCopySecrets(
        void* grantId,
        std::size_t grantIdBytes,
        void* ticket,
        std::size_t ticketBytes) const noexcept;
    void Clear() noexcept;

private:
    friend bool TryDecodeSecureGameGrant(
        const void*,
        std::size_t,
        SecureGameGrant*) noexcept;

    void MoveFrom(SecureGameGrant* other) noexcept;

    char routeHost_[SecureGameRouteHostMaximumBytes + 1]{};
    char tlsHost_[SecureGameTlsHostMaximumBytes + 1]{};
    char audience_[SecureGameAudienceMaximumBytes + 1]{};
    std::uint16_t routeHostLength_ = 0;
    std::uint16_t tlsHostLength_ = 0;
    std::uint8_t audienceLength_ = 0;
    std::uint16_t routePort_ = 0;
    std::uint16_t tlsPort_ = 0;
    std::uint32_t targetServerId_ = 0;
    std::uint64_t expiryUnixMilliseconds_ = 0;
    std::uint8_t grantId_[SecureGameGrantIdBytes]{};
    std::uint8_t ticket_[SecureGameTicketBytes]{};
    bool valid_ = false;
};

bool TryDecodeSecureGameGrant(
    const void* source,
    std::size_t sourceBytes,
    SecureGameGrant* grant) noexcept;

bool TryEncodeSecureGameBind(
    const SecureGameGrant& grant,
    void* destination,
    std::size_t destinationBytes) noexcept;

bool TryDecodeSecureBindResult(
    const void* source,
    std::size_t sourceBytes,
    SecureBindStatus* status) noexcept;

} // namespace godswar::network
