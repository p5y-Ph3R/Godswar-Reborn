#include "SecureUdpClientWorker.h"

#include "SecureOuterStream.h"

#include <WinSock2.h>
#include <Windows.h>

namespace godswar::network {

bool SecureUdpClientWorker::ProcessUdpMovement(
    std::uint64_t nowMilliseconds) noexcept {
    const auto channel = channel_.Snapshot();
    if (channel.state != SecureUdpClientChannelState::Active) {
        return true;
    }
    if (!hasPendingMovement_) {
        if (nowMilliseconds < channel.lastSendMilliseconds ||
            nowMilliseconds - channel.lastSendMilliseconds <
                SecureUdpClientChannel::
                    MinimumSendIntervalMilliseconds) {
            return true;
        }
        hasPendingMovement_ =
            movementRouter_.TryTakePending(
                &pendingMovement_);
    } else {
        SecureRealtimeMovementInput newer{};
        if (movementRouter_.TryTakePending(&newer)) {
            pendingMovement_ = newer;
        }
    }
    if (!hasPendingMovement_) {
        return true;
    }

    std::uint8_t datagram[
        SecureUdpProtectedMaximumBytes]{};
    std::size_t datagramBytes = 0;
    const bool built = channel_.TryBuildMovementInput(
        pendingMovement_,
        nowMilliseconds,
        datagram,
        sizeof(datagram),
        &datagramBytes);
    if (!built) {
        SecureZeroMemory(datagram, sizeof(datagram));
        return false;
    }
    const bool recorded = movementRouter_.RecordUdpSent(
        pendingMovement_,
        nowMilliseconds);
    pendingMovement_ = SecureRealtimeMovementInput{};
    hasPendingMovement_ = false;
    PublishMovement();
    if (!recorded ||
        !SendDatagram(datagram, datagramBytes)) {
        SecureZeroMemory(datagram, sizeof(datagram));
        return false;
    }
    SecureZeroMemory(datagram, sizeof(datagram));
    return true;
}

bool SecureUdpClientWorker::ProcessTlsMovement() noexcept {
    if (outerStream_ == nullptr) {
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::TlsMovementWrite);
        return false;
    }

    if (hasRetryMovement_) {
        // TLS framing is already reliable and ordered. Do not rebuild an ACK
        // loop here; authoritative rejection/correction returns through the
        // stock legacy 10194 stream, whose cipher state was never advanced by
        // the suppressed client movement.
        if (!outerStream_->WriteRealtimeMovementInput(
                retryMovement_)) {
            SetState(
                SecureUdpClientWorkerState::Failed,
                SecureUdpClientWorkerFailure::TlsMovementWrite);
            return false;
        }
        retryMovement_ = SecureRealtimeMovementInput{};
        hasRetryMovement_ = false;
    }

    if (hasPendingMovement_) {
        SecureRealtimeMovementInput newer{};
        if (movementRouter_.TryTakePending(&newer)) {
            pendingMovement_ = newer;
        }
        if (!movementRouter_.PrepareForCurrentOwner(
                &pendingMovement_) ||
            !outerStream_->WriteRealtimeMovementInput(
                pendingMovement_)) {
            SetState(
                SecureUdpClientWorkerState::Failed,
                SecureUdpClientWorkerFailure::TlsMovementWrite);
            return false;
        }
        pendingMovement_ = SecureRealtimeMovementInput{};
        hasPendingMovement_ = false;
    }

    SecureRealtimeMovementInput movement{};
    if (movementRouter_.TryTakePending(&movement) &&
        !outerStream_->WriteRealtimeMovementInput(movement)) {
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::TlsMovementWrite);
        return false;
    }
    PublishMovement();
    return true;
}

bool SecureUdpClientWorker::SwitchMovementToTls(
    SecureUdpClientWorkerFailure failure,
    int nativeError) noexcept {
    SecureRealtimeMovementInput retry{};
    bool hasRetry = false;
    if (outerStream_ == nullptr ||
        !movementRouter_.SwitchToTls(&retry, &hasRetry)) {
        return false;
    }
    if (hasRetry) {
        retryMovement_ = retry;
        hasRetryMovement_ = true;
    }
    if (hasPendingMovement_ &&
        !movementRouter_.PrepareForCurrentOwner(
            &pendingMovement_)) {
        return false;
    }
    EnterTlsFallback(failure, nativeError);
    PublishMovement();
    return true;
}

bool SecureUdpClientWorker::ContinueTlsFallbackLoop() noexcept {
    HANDLE waits[] = {
        stopEvent_,
        movementRouter_.WakeEvent(),
    };
    while (!ShouldStop()) {
        if (!ProcessTlsMovement()) {
            movementRouter_.Stop();
            PublishMovement();
            return false;
        }
        const DWORD waited = WaitForMultipleObjects(
            static_cast<DWORD>(
                sizeof(waits) / sizeof(waits[0])),
            waits,
            FALSE,
            50);
        if (waited == WAIT_FAILED) {
            movementRouter_.Stop();
            SetState(
                SecureUdpClientWorkerState::Failed,
                SecureUdpClientWorkerFailure::TlsMovementWrite,
                static_cast<int>(GetLastError()));
            PublishMovement();
            return false;
        }
    }
    return true;
}

void SecureUdpClientWorker::ConsumePositionSnapshot(
    std::uint64_t nowMilliseconds) noexcept {
    SecureRealtimePositionSnapshot snapshot{};
    if (channel_.TryTakePositionSnapshot(&snapshot)) {
        static_cast<void>(
            movementRouter_.AcceptAuthenticatedSnapshot(
                snapshot,
                nowMilliseconds));
        PublishMovement();
    }
}

} // namespace godswar::network
