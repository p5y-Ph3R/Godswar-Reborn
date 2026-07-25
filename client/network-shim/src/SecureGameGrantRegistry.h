#pragma once

#include "ClientRoute.h"
#include "EndpointManifest.h"
#include "SecureGameControl.h"

#include <Windows.h>

#include <cstdint>

namespace godswar::network {

enum class SecureGameGrantState : std::uint8_t {
    Empty = 0,
    Pending,
    Claimed,
    Presented,
};

enum class SecureGameGrantResult : std::uint8_t {
    Success = 0,
    InvalidArgument,
    ClockUnavailable,
    PolicyRejected,
    Expired,
    Unavailable,
    RouteMismatch,
    AlreadyClaimed,
    StaleClaim,
    GenerationExhausted,
};

using SecureGameGrantClock = bool (*)(
    void* context,
    std::uint64_t* unixMilliseconds) noexcept;

struct SecureGameGrantPolicy final {
    EndpointManifest manifest{};
    void* clockContext = nullptr;
    SecureGameGrantClock clock = nullptr;
};

struct SecureGameGrantClaim final {
    NativeProxyId proxyId = 0;
    std::uint64_t proxyGeneration = 0;
    std::uint64_t grantGeneration = 0;
};

struct SecureGameGrantRegistrySnapshot final {
    SecureGameGrantState state = SecureGameGrantState::Empty;
    std::uint64_t grantGeneration = 0;
    NativeProxyId claimedProxyId = 0;
};

// Fixed-memory, process-local owner for at most one authenticated game grant.
// It is not wired into the exported proxy in Slice 7. All methods are bounded;
// no timer or worker thread is created, and expiry is enforced lazily on each
// state transition.
class SecureGameGrantRegistry final {
public:
    explicit SecureGameGrantRegistry(
        SecureGameGrantPolicy policy) noexcept;
    ~SecureGameGrantRegistry() noexcept;

    SecureGameGrantRegistry(const SecureGameGrantRegistry&) = delete;
    SecureGameGrantRegistry& operator=(
        const SecureGameGrantRegistry&) = delete;

    SecureGameGrantResult Commit(
        SecureGameGrant&& grant,
        std::uint64_t* committedGeneration = nullptr) noexcept;
    SecureGameGrantResult Claim(
        NativeProxyId proxyId,
        std::uint64_t proxyGeneration,
        const ClientRoute& route,
        SecureGameGrantClaim* claim) noexcept;
    SecureGameGrantResult ReturnUnpresented(
        const SecureGameGrantClaim& claim) noexcept;
    SecureGameGrantResult BeginPresentation(
        const SecureGameGrantClaim& claim,
        SecureGameGrant* grant) noexcept;
    bool EraseIfGeneration(
        std::uint64_t grantGeneration) noexcept;
    void Erase() noexcept;

    SecureGameGrantRegistrySnapshot Snapshot() const noexcept;

private:
    bool TryGetNow(std::uint64_t* unixMilliseconds) const noexcept;
    bool IsAllowed(const SecureGameGrant& grant) const noexcept;
    bool ClearIfExpiredLocked(std::uint64_t now) noexcept;
    bool ClaimMatchesLocked(
        const SecureGameGrantClaim& claim) const noexcept;
    void ClearLocked() noexcept;

    mutable SRWLOCK lock_{};
    SecureGameGrantPolicy policy_{};
    SecureGameGrant grant_{};
    SecureGameGrantState state_ = SecureGameGrantState::Empty;
    std::uint64_t grantGeneration_ = 0;
    NativeProxyId claimedProxyId_ = 0;
    std::uint64_t claimedProxyGeneration_ = 0;
};

} // namespace godswar::network
