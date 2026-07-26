#include "SecureRealtimeMovementRouter.h"

#include <algorithm>
#include <limits>

namespace godswar::network {

SecureRealtimeMovementRouter::
SecureRealtimeMovementRouter() noexcept
    : wakeEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
    InitializeSRWLock(&lock_);
}

SecureRealtimeMovementRouter::
~SecureRealtimeMovementRouter() noexcept {
    Stop();
    if (wakeEvent_ != nullptr) {
        CloseHandle(wakeEvent_);
        wakeEvent_ = nullptr;
    }
}

bool SecureRealtimeMovementRouter::IsValid() const noexcept {
    return wakeEvent_ != nullptr;
}

bool SecureRealtimeMovementRouter::Configure(
    bool authoritativeCapability) noexcept {
    AcquireSRWLockExclusive(&lock_);
    const bool accepted =
        !configured_ && !stopped_ && wakeEvent_ != nullptr;
    if (accepted) {
        configured_ = true;
        authoritativeCapability_ =
            authoritativeCapability;
    }
    ReleaseSRWLockExclusive(&lock_);
    return accepted;
}

bool SecureRealtimeMovementRouter::
AcceptAuthenticatedSnapshot(
    const SecureRealtimePositionSnapshot& snapshot,
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    std::uint8_t validatedBytes[
        SecureRealtimePositionSnapshotBytes]{};
    if (!TryEncodeSecureRealtimePositionSnapshot(
            snapshot,
            validatedBytes,
            sizeof(validatedBytes))) {
        return false;
    }
    const bool keyframe = HasFlag(
        snapshot.flags,
        SecureRealtimePositionSnapshotFlag::Keyframe);
    AcquireSRWLockExclusive(&lock_);
    bool accepted = configured_ && !stopped_;
    if (accepted && !hasAuthenticatedBaseline_) {
        accepted =
            keyframe &&
            snapshot.transportEpoch == 1 &&
            snapshot.acknowledgedInputId == 0 &&
            snapshot.rejection ==
                SecureRealtimeMovementRejection::None;
        if (accepted) {
            hasAuthenticatedBaseline_ = true;
            baseline_ = snapshot;
            transportEpoch_ = snapshot.transportEpoch;
            lastAcknowledgedInputId_ = 0;
            owner_ = authoritativeCapability_
                ? SecureRealtimeMovementOwner::SecureUdp
                : SecureRealtimeMovementOwner::LegacyTls;
        }
    } else if (accepted) {
        accepted =
            snapshot.snapshotSequence >
                baseline_.snapshotSequence &&
            snapshot.transportEpoch == transportEpoch_ &&
            snapshot.acknowledgedInputId >=
                lastAcknowledgedInputId_ &&
            snapshot.acknowledgedInputId <=
                highestSubmittedInputId_;
        if (accepted) {
            const bool acknowledgmentAdvanced =
                snapshot.acknowledgedInputId >
                    lastAcknowledgedInputId_;
            baseline_ = snapshot;
            if (acknowledgmentAdvanced) {
                lastAcknowledgedInputId_ =
                    snapshot.acknowledgedInputId;
                if (hasUnacknowledged_ &&
                    unacknowledged_.inputId <=
                        lastAcknowledgedInputId_) {
                    hasUnacknowledged_ = false;
                    unacknowledged_ =
                        SecureRealtimeMovementInput{};
                    acknowledgmentDeadlineMilliseconds_ = 0;
                } else if (hasUnacknowledged_) {
                    acknowledgmentDeadlineMilliseconds_ =
                        DeadlineAfter(
                            nowMonotonicMilliseconds,
                            GameplayAcknowledgmentTimeoutMilliseconds);
                }
            }
        }
    }
    ReleaseSRWLockExclusive(&lock_);
    return accepted;
}

SecureRealtimeMovementRouteResult
SecureRealtimeMovementRouter::RouteLegacyPacket(
    const void* packet,
    int packetBytes,
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    SecureRealtimeLegacyMovement legacy{};
    if (packetBytes < 0 ||
        !TryParseSecureRealtimeLegacyMovement(
            packet,
            static_cast<std::size_t>(packetBytes),
            &legacy)) {
        return SecureRealtimeMovementRouteResult::PassThrough;
    }

    AcquireSRWLockExclusive(&lock_);
    if (!configured_ ||
        !authoritativeCapability_ ||
        !hasAuthenticatedBaseline_) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureRealtimeMovementRouteResult::PassThrough;
    }
    if (stopped_ ||
        nowMonotonicMilliseconds == 0 ||
        nextInputId_ == 0 ||
        (owner_ != SecureRealtimeMovementOwner::SecureUdp &&
            owner_ !=
                SecureRealtimeMovementOwner::SecureTls)) {
        ReleaseSRWLockExclusive(&lock_);
        return SecureRealtimeMovementRouteResult::Rejected;
    }

    SecureRealtimeMovementInput movement{};
    movement.flags =
        owner_ == SecureRealtimeMovementOwner::SecureTls
        ? static_cast<std::uint8_t>(
            SecureRealtimeMovementInputFlag::CurrentWorld)
        : 0;
    movement.transportEpoch = transportEpoch_;
    movement.inputId = nextInputId_;
    movement.clientMonotonicMilliseconds =
        nowMonotonicMilliseconds;
    movement.worldGeneration = baseline_.worldGeneration;
    movement.legacyState = legacy.legacyState;
    movement.x = legacy.x;
    movement.z = legacy.z;
    movement.auxiliary = legacy.auxiliary;
    movement.mapId = baseline_.mapId;

    highestSubmittedInputId_ = nextInputId_;
    nextInputId_ =
        nextInputId_ ==
            (std::numeric_limits<std::uint64_t>::max)()
        ? 0
        : nextInputId_ + 1;
    if (hasPending_) {
        IncrementSaturated(&pendingReplacements_);
    }
    pending_ = movement;
    hasPending_ = true;
    SetEvent(wakeEvent_);
    ReleaseSRWLockExclusive(&lock_);
    return SecureRealtimeMovementRouteResult::Accepted;
}

bool SecureRealtimeMovementRouter::TryTakePending(
    SecureRealtimeMovementInput* movement) noexcept {
    if (movement == nullptr) {
        return false;
    }
    *movement = SecureRealtimeMovementInput{};

    AcquireSRWLockExclusive(&lock_);
    const bool available =
        !stopped_ &&
        hasPending_ &&
        (owner_ == SecureRealtimeMovementOwner::SecureUdp ||
            owner_ == SecureRealtimeMovementOwner::SecureTls);
    if (available) {
        *movement = pending_;
        if (owner_ == SecureRealtimeMovementOwner::SecureTls) {
            PrepareForTlsLocked(movement);
        }
        pending_ = SecureRealtimeMovementInput{};
        hasPending_ = false;
        ResetEvent(wakeEvent_);
    }
    ReleaseSRWLockExclusive(&lock_);
    return available;
}

bool SecureRealtimeMovementRouter::PrepareForCurrentOwner(
    SecureRealtimeMovementInput* movement) const noexcept {
    if (movement == nullptr) {
        return false;
    }
    AcquireSRWLockShared(&lock_);
    const bool usable =
        !stopped_ &&
        (owner_ == SecureRealtimeMovementOwner::SecureUdp ||
            owner_ == SecureRealtimeMovementOwner::SecureTls);
    if (usable) {
        if (owner_ == SecureRealtimeMovementOwner::SecureTls) {
            PrepareForTlsLocked(movement);
        } else {
            movement->flags = 0;
            movement->transportEpoch = transportEpoch_;
        }
    }
    ReleaseSRWLockShared(&lock_);
    return usable;
}

bool SecureRealtimeMovementRouter::RecordUdpSent(
    const SecureRealtimeMovementInput& movement,
    std::uint64_t nowMonotonicMilliseconds) noexcept {
    AcquireSRWLockExclusive(&lock_);
    const bool accepted =
        !stopped_ &&
        owner_ == SecureRealtimeMovementOwner::SecureUdp &&
        movement.flags == 0 &&
        movement.transportEpoch == transportEpoch_ &&
        movement.inputId != 0 &&
        movement.inputId <= highestSubmittedInputId_;
    if (accepted) {
        hasSentUdpMovement_ = true;
        unacknowledged_ = movement;
        hasUnacknowledged_ = true;
        if (acknowledgmentDeadlineMilliseconds_ == 0) {
            acknowledgmentDeadlineMilliseconds_ =
                DeadlineAfter(
                    nowMonotonicMilliseconds,
                    GameplayAcknowledgmentTimeoutMilliseconds);
        }
    }
    ReleaseSRWLockExclusive(&lock_);
    return accepted;
}

bool SecureRealtimeMovementRouter::
UdpAcknowledgmentTimedOut(
    std::uint64_t nowMonotonicMilliseconds) const noexcept {
    AcquireSRWLockShared(&lock_);
    const bool timedOut =
        !stopped_ &&
        owner_ == SecureRealtimeMovementOwner::SecureUdp &&
        hasUnacknowledged_ &&
        acknowledgmentDeadlineMilliseconds_ != 0 &&
        nowMonotonicMilliseconds >=
            acknowledgmentDeadlineMilliseconds_;
    ReleaseSRWLockShared(&lock_);
    return timedOut;
}

bool SecureRealtimeMovementRouter::SwitchToTls(
    SecureRealtimeMovementInput* retry,
    bool* hasRetry) noexcept {
    if (retry == nullptr || hasRetry == nullptr) {
        return false;
    }
    *retry = SecureRealtimeMovementInput{};
    *hasRetry = false;

    AcquireSRWLockExclusive(&lock_);
    bool accepted =
        configured_ &&
        authoritativeCapability_ &&
        hasAuthenticatedBaseline_ &&
        !stopped_;
    if (accepted &&
        owner_ == SecureRealtimeMovementOwner::SecureUdp) {
        if (hasSentUdpMovement_ &&
            transportEpoch_ ==
            (std::numeric_limits<std::uint32_t>::max)()) {
            accepted = false;
        } else {
            if (hasSentUdpMovement_) {
                ++transportEpoch_;
            }
            owner_ = SecureRealtimeMovementOwner::SecureTls;
            if (hasUnacknowledged_) {
                *retry = unacknowledged_;
                PrepareForTlsLocked(retry);
                *hasRetry = true;
            }
            unacknowledged_ = SecureRealtimeMovementInput{};
            hasUnacknowledged_ = false;
            acknowledgmentDeadlineMilliseconds_ = 0;
            if (hasPending_) {
                SetEvent(wakeEvent_);
            }
        }
    } else if (accepted) {
        accepted =
            owner_ == SecureRealtimeMovementOwner::SecureTls;
    }
    ReleaseSRWLockExclusive(&lock_);
    return accepted;
}

HANDLE SecureRealtimeMovementRouter::WakeEvent() const noexcept {
    return wakeEvent_;
}

void SecureRealtimeMovementRouter::Stop() noexcept {
    AcquireSRWLockExclusive(&lock_);
    stopped_ = true;
    hasPending_ = false;
    hasUnacknowledged_ = false;
    pending_ = SecureRealtimeMovementInput{};
    unacknowledged_ = SecureRealtimeMovementInput{};
    acknowledgmentDeadlineMilliseconds_ = 0;
    if (wakeEvent_ != nullptr) {
        SetEvent(wakeEvent_);
    }
    ReleaseSRWLockExclusive(&lock_);
}

SecureRealtimeMovementRouterSnapshot
SecureRealtimeMovementRouter::Snapshot() const noexcept {
    AcquireSRWLockShared(&lock_);
    SecureRealtimeMovementRouterSnapshot snapshot{};
    snapshot.authoritativeCapability =
        authoritativeCapability_;
    snapshot.hasAuthenticatedBaseline =
        hasAuthenticatedBaseline_;
    snapshot.stopped = stopped_;
    snapshot.owner = owner_;
    snapshot.transportEpoch = transportEpoch_;
    snapshot.highestSubmittedInputId =
        highestSubmittedInputId_;
    snapshot.lastAcknowledgedInputId =
        lastAcknowledgedInputId_;
    snapshot.latestSnapshotSequence =
        baseline_.snapshotSequence;
    snapshot.pendingReplacements = pendingReplacements_;
    snapshot.acknowledgmentDeadlineMilliseconds =
        acknowledgmentDeadlineMilliseconds_;
    ReleaseSRWLockShared(&lock_);
    return snapshot;
}

bool SecureRealtimeMovementRouter::HasFlag(
    std::uint8_t flags,
    SecureRealtimePositionSnapshotFlag flag) noexcept {
    return (flags & static_cast<std::uint8_t>(flag)) != 0;
}

void SecureRealtimeMovementRouter::IncrementSaturated(
    std::uint64_t* value) noexcept {
    if (value != nullptr &&
        *value != (std::numeric_limits<std::uint64_t>::max)()) {
        ++*value;
    }
}

std::uint64_t SecureRealtimeMovementRouter::DeadlineAfter(
    std::uint64_t now,
    std::uint64_t interval) noexcept {
    return now >
        (std::numeric_limits<std::uint64_t>::max)() - interval
        ? (std::numeric_limits<std::uint64_t>::max)()
        : now + interval;
}

void SecureRealtimeMovementRouter::PrepareForTlsLocked(
    SecureRealtimeMovementInput* movement) const noexcept {
    movement->flags = static_cast<std::uint8_t>(
        SecureRealtimeMovementInputFlag::CurrentWorld);
    movement->transportEpoch = transportEpoch_;
}

} // namespace godswar::network
