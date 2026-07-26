#pragma once

#include "SecureRealtimeMovementProtocol.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class SecureRealtimeMovementRouteResult : std::uint8_t {
    PassThrough = 0,
    Accepted,
    Rejected,
};

enum class SecureRealtimeMovementOwner : std::uint8_t {
    LegacyTls = 0,
    SecureUdp,
    SecureTls,
};

struct SecureRealtimeMovementRouterSnapshot final {
    bool authoritativeCapability = false;
    bool hasAuthenticatedBaseline = false;
    bool stopped = false;
    SecureRealtimeMovementOwner owner =
        SecureRealtimeMovementOwner::LegacyTls;
    std::uint32_t transportEpoch = 0;
    std::uint64_t highestSubmittedInputId = 0;
    std::uint64_t lastAcknowledgedInputId = 0;
    std::uint64_t latestSnapshotSequence = 0;
    std::uint64_t pendingReplacements = 0;
    std::uint64_t acknowledgmentDeadlineMilliseconds = 0;
};

// Thread-safe boundary between the Origin game thread and the single-owner
// realtime worker. At most one unsent movement sample is retained; a newer
// absolute-position intent replaces a stale pending sample.
class SecureRealtimeMovementRouter final {
public:
    static constexpr std::uint64_t
        GameplayAcknowledgmentTimeoutMilliseconds = 1'000;

    SecureRealtimeMovementRouter() noexcept;
    ~SecureRealtimeMovementRouter() noexcept;

    SecureRealtimeMovementRouter(
        const SecureRealtimeMovementRouter&) = delete;
    SecureRealtimeMovementRouter& operator=(
        const SecureRealtimeMovementRouter&) = delete;

    bool IsValid() const noexcept;
    bool Configure(bool authoritativeCapability) noexcept;
    bool AcceptAuthenticatedSnapshot(
        const SecureRealtimePositionSnapshot& snapshot,
        std::uint64_t nowMonotonicMilliseconds) noexcept;

    SecureRealtimeMovementRouteResult RouteLegacyPacket(
        const void* packet,
        int packetBytes,
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    bool TryTakePending(
        SecureRealtimeMovementInput* movement) noexcept;
    bool PrepareForCurrentOwner(
        SecureRealtimeMovementInput* movement) const noexcept;
    bool RecordUdpSent(
        const SecureRealtimeMovementInput& movement,
        std::uint64_t nowMonotonicMilliseconds) noexcept;
    bool UdpAcknowledgmentTimedOut(
        std::uint64_t nowMonotonicMilliseconds) const noexcept;
    bool SwitchToTls(
        SecureRealtimeMovementInput* retry,
        bool* hasRetry) noexcept;

    HANDLE WakeEvent() const noexcept;
    void Stop() noexcept;
    SecureRealtimeMovementRouterSnapshot Snapshot() const noexcept;

private:
    static bool HasFlag(
        std::uint8_t flags,
        SecureRealtimePositionSnapshotFlag flag) noexcept;
    static void IncrementSaturated(
        std::uint64_t* value) noexcept;
    static std::uint64_t DeadlineAfter(
        std::uint64_t now,
        std::uint64_t interval) noexcept;
    void PrepareForTlsLocked(
        SecureRealtimeMovementInput* movement) const noexcept;

    mutable SRWLOCK lock_{};
    HANDLE wakeEvent_ = nullptr;
    bool configured_ = false;
    bool authoritativeCapability_ = false;
    bool hasAuthenticatedBaseline_ = false;
    bool stopped_ = false;
    SecureRealtimeMovementOwner owner_ =
        SecureRealtimeMovementOwner::LegacyTls;
    SecureRealtimePositionSnapshot baseline_{};
    std::uint32_t transportEpoch_ = 0;
    std::uint64_t nextInputId_ = 1;
    std::uint64_t highestSubmittedInputId_ = 0;
    std::uint64_t lastAcknowledgedInputId_ = 0;
    std::uint64_t acknowledgmentDeadlineMilliseconds_ = 0;
    std::uint64_t pendingReplacements_ = 0;
    SecureRealtimeMovementInput pending_{};
    SecureRealtimeMovementInput unacknowledged_{};
    bool hasPending_ = false;
    bool hasUnacknowledged_ = false;
    bool hasSentUdpMovement_ = false;
};

} // namespace godswar::network
