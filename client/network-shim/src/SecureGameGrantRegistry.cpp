#include "SecureGameGrantRegistry.h"

#include <cstring>
#include <limits>
#include <utility>

namespace godswar::network {
namespace {

class ExclusiveLock final {
public:
    explicit ExclusiveLock(SRWLOCK* lock) noexcept : lock_(lock) {
        AcquireSRWLockExclusive(lock_);
    }

    ~ExclusiveLock() noexcept {
        ReleaseSRWLockExclusive(lock_);
    }

    ExclusiveLock(const ExclusiveLock&) = delete;
    ExclusiveLock& operator=(const ExclusiveLock&) = delete;

private:
    SRWLOCK* lock_;
};

class SharedLock final {
public:
    explicit SharedLock(SRWLOCK* lock) noexcept : lock_(lock) {
        AcquireSRWLockShared(lock_);
    }

    ~SharedLock() noexcept {
        ReleaseSRWLockShared(lock_);
    }

    SharedLock(const SharedLock&) = delete;
    SharedLock& operator=(const SharedLock&) = delete;

private:
    SRWLOCK* lock_;
};

} // namespace

SecureGameGrantRegistry::SecureGameGrantRegistry(
    SecureGameGrantPolicy policy) noexcept
    : policy_(policy) {
    InitializeSRWLock(&lock_);
}

SecureGameGrantRegistry::~SecureGameGrantRegistry() noexcept {
    Erase();
}

SecureGameGrantResult SecureGameGrantRegistry::Commit(
    SecureGameGrant&& grant,
    std::uint64_t* committedGeneration) noexcept {
    if (committedGeneration != nullptr) {
        *committedGeneration = 0;
    }
    if (!grant.IsValid()) {
        return SecureGameGrantResult::InvalidArgument;
    }

    std::uint64_t now = 0;
    if (!TryGetNow(&now)) {
        return SecureGameGrantResult::ClockUnavailable;
    }
    if (grant.ExpiryUnixMilliseconds() <= now) {
        return SecureGameGrantResult::Expired;
    }
    if (!IsAllowed(grant)) {
        return SecureGameGrantResult::PolicyRejected;
    }

    ExclusiveLock guard(&lock_);
    if (grantGeneration_ ==
        (std::numeric_limits<std::uint64_t>::max)()) {
        ClearLocked();
        return SecureGameGrantResult::GenerationExhausted;
    }

    ++grantGeneration_;
    grant_ = std::move(grant);
    state_ = SecureGameGrantState::Pending;
    claimedProxyId_ = 0;
    claimedProxyGeneration_ = 0;
    if (committedGeneration != nullptr) {
        *committedGeneration = grantGeneration_;
    }
    return SecureGameGrantResult::Success;
}

SecureGameGrantResult SecureGameGrantRegistry::Claim(
    NativeProxyId proxyId,
    std::uint64_t proxyGeneration,
    const ClientRoute& route,
    SecureGameGrantClaim* claim) noexcept {
    if (claim == nullptr ||
        proxyId == 0 ||
        proxyGeneration == 0 ||
        route.hostLength == 0 ||
        route.hostLength > NativeRouteHostMaximumBytes ||
        route.port == 0) {
        return SecureGameGrantResult::InvalidArgument;
    }
    *claim = SecureGameGrantClaim{};

    std::uint64_t now = 0;
    if (!TryGetNow(&now)) {
        return SecureGameGrantResult::ClockUnavailable;
    }

    ExclusiveLock guard(&lock_);
    if (ClearIfExpiredLocked(now)) {
        return SecureGameGrantResult::Expired;
    }
    if (state_ == SecureGameGrantState::Claimed) {
        return SecureGameGrantResult::AlreadyClaimed;
    }
    if (state_ != SecureGameGrantState::Pending ||
        !grant_.IsValid()) {
        return SecureGameGrantResult::Unavailable;
    }
    if (route.port != grant_.RoutePort() ||
        route.hostLength != grant_.RouteHostLength() ||
        std::memcmp(
            route.host,
            grant_.RouteHost(),
            route.hostLength) != 0) {
        return SecureGameGrantResult::RouteMismatch;
    }

    state_ = SecureGameGrantState::Claimed;
    claimedProxyId_ = proxyId;
    claimedProxyGeneration_ = proxyGeneration;
    *claim = SecureGameGrantClaim{
        proxyId,
        proxyGeneration,
        grantGeneration_};
    return SecureGameGrantResult::Success;
}

bool SecureGameGrantRegistry::MatchesPendingRoute(
    const ClientRoute& route) noexcept {
    if (route.hostLength == 0 ||
        route.hostLength > NativeRouteHostMaximumBytes ||
        route.port == 0) {
        return false;
    }

    std::uint64_t now = 0;
    if (!TryGetNow(&now)) {
        return false;
    }

    ExclusiveLock guard(&lock_);
    if (ClearIfExpiredLocked(now) ||
        state_ != SecureGameGrantState::Pending ||
        !grant_.IsValid()) {
        return false;
    }

    return route.port == grant_.RoutePort() &&
        route.hostLength == grant_.RouteHostLength() &&
        std::memcmp(
            route.host,
            grant_.RouteHost(),
            route.hostLength) == 0;
}

SecureGameGrantResult SecureGameGrantRegistry::TryCopyClaimedTarget(
    const SecureGameGrantClaim& claim,
    SecureGameGrantTarget* target) noexcept {
    if (target == nullptr ||
        claim.proxyId == 0 ||
        claim.proxyGeneration == 0 ||
        claim.grantGeneration == 0) {
        return SecureGameGrantResult::InvalidArgument;
    }
    *target = SecureGameGrantTarget{};

    std::uint64_t now = 0;
    if (!TryGetNow(&now)) {
        return SecureGameGrantResult::ClockUnavailable;
    }

    ExclusiveLock guard(&lock_);
    if (ClearIfExpiredLocked(now)) {
        return SecureGameGrantResult::Expired;
    }
    if (!ClaimMatchesLocked(claim)) {
        return SecureGameGrantResult::StaleClaim;
    }

    const auto hostLength = grant_.TlsHostLength();
    if (hostLength == 0 ||
        hostLength > EndpointManifestMaximumDnsBytes ||
        grant_.TlsPort() == 0) {
        return SecureGameGrantResult::PolicyRejected;
    }

    SecureGameGrantTarget candidate{};
    std::memcpy(
        candidate.tlsHost,
        grant_.TlsHost(),
        hostLength);
    candidate.tlsHost[hostLength] = '\0';
    candidate.tlsHostLength =
        static_cast<std::uint16_t>(hostLength);
    candidate.tlsPort = grant_.TlsPort();
    *target = candidate;
    return SecureGameGrantResult::Success;
}

SecureGameGrantResult SecureGameGrantRegistry::ReturnUnpresented(
    const SecureGameGrantClaim& claim) noexcept {
    if (claim.proxyId == 0 ||
        claim.proxyGeneration == 0 ||
        claim.grantGeneration == 0) {
        return SecureGameGrantResult::InvalidArgument;
    }

    std::uint64_t now = 0;
    if (!TryGetNow(&now)) {
        return SecureGameGrantResult::ClockUnavailable;
    }

    ExclusiveLock guard(&lock_);
    if (ClearIfExpiredLocked(now)) {
        return SecureGameGrantResult::Expired;
    }
    if (!ClaimMatchesLocked(claim)) {
        return SecureGameGrantResult::StaleClaim;
    }

    state_ = SecureGameGrantState::Pending;
    claimedProxyId_ = 0;
    claimedProxyGeneration_ = 0;
    return SecureGameGrantResult::Success;
}

SecureGameGrantResult SecureGameGrantRegistry::BeginPresentation(
    const SecureGameGrantClaim& claim,
    SecureGameGrant* grant) noexcept {
    if (grant == nullptr ||
        claim.proxyId == 0 ||
        claim.proxyGeneration == 0 ||
        claim.grantGeneration == 0) {
        if (grant != nullptr) {
            grant->Clear();
        }
        return SecureGameGrantResult::InvalidArgument;
    }
    grant->Clear();

    std::uint64_t now = 0;
    if (!TryGetNow(&now)) {
        return SecureGameGrantResult::ClockUnavailable;
    }

    ExclusiveLock guard(&lock_);
    if (ClearIfExpiredLocked(now)) {
        return SecureGameGrantResult::Expired;
    }
    if (!ClaimMatchesLocked(claim)) {
        return SecureGameGrantResult::StaleClaim;
    }

    *grant = std::move(grant_);
    state_ = SecureGameGrantState::Presented;
    claimedProxyId_ = 0;
    claimedProxyGeneration_ = 0;
    return SecureGameGrantResult::Success;
}

bool SecureGameGrantRegistry::EraseIfGeneration(
    std::uint64_t grantGeneration) noexcept {
    if (grantGeneration == 0) {
        return false;
    }
    ExclusiveLock guard(&lock_);
    if (grantGeneration_ != grantGeneration ||
        state_ == SecureGameGrantState::Empty ||
        state_ == SecureGameGrantState::Presented) {
        return false;
    }
    ClearLocked();
    return true;
}

void SecureGameGrantRegistry::Erase() noexcept {
    ExclusiveLock guard(&lock_);
    ClearLocked();
}

SecureGameGrantRegistrySnapshot
SecureGameGrantRegistry::Snapshot() const noexcept {
    SharedLock guard(&lock_);
    return SecureGameGrantRegistrySnapshot{
        state_,
        grantGeneration_,
        claimedProxyId_};
}

bool SecureGameGrantRegistry::TryGetNow(
    std::uint64_t* unixMilliseconds) const noexcept {
    if (unixMilliseconds == nullptr || policy_.clock == nullptr) {
        return false;
    }
    *unixMilliseconds = 0;
    return policy_.clock(
        policy_.clockContext,
        unixMilliseconds);
}

bool SecureGameGrantRegistry::IsAllowed(
    const SecureGameGrant& grant) const noexcept {
    return EndpointManifestAllowsGameHost(
               policy_.manifest,
               grant.TlsHost(),
               grant.TlsHostLength()) &&
        EndpointManifestAllowsAudience(
               policy_.manifest,
               grant.Audience(),
               grant.AudienceLength()) &&
        EndpointManifestAllowsServerId(
               policy_.manifest,
               grant.TargetServerId());
}

bool SecureGameGrantRegistry::ClearIfExpiredLocked(
    std::uint64_t now) noexcept {
    if ((state_ == SecureGameGrantState::Pending ||
            state_ == SecureGameGrantState::Claimed) &&
        grant_.IsValid() &&
        grant_.ExpiryUnixMilliseconds() <= now) {
        ClearLocked();
        return true;
    }
    return false;
}

bool SecureGameGrantRegistry::ClaimMatchesLocked(
    const SecureGameGrantClaim& claim) const noexcept {
    return state_ == SecureGameGrantState::Claimed &&
        grant_.IsValid() &&
        claim.grantGeneration == grantGeneration_ &&
        claim.proxyId == claimedProxyId_ &&
        claim.proxyGeneration == claimedProxyGeneration_;
}

void SecureGameGrantRegistry::ClearLocked() noexcept {
    grant_.Clear();
    state_ = SecureGameGrantState::Empty;
    claimedProxyId_ = 0;
    claimedProxyGeneration_ = 0;
}

} // namespace godswar::network
