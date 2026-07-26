#include "SecureOuterStream.h"

#include <Windows.h>

namespace godswar::network {

bool SecureOuterStream::WriteRealtimeMovementInput(
    const SecureRealtimeMovementInput& movement) noexcept {
    SecureEndpointRole currentRole = SecureEndpointRole::Login;
    bool currentBound = false;
    AcquireSRWLockShared(&snapshotLock_);
    currentRole = role_;
    currentBound = gameBound_;
    ReleaseSRWLockShared(&snapshotLock_);
    if (!established_ ||
        currentRole != SecureEndpointRole::Game ||
        !currentBound ||
        IsStopped()) {
        return false;
    }

    std::uint8_t payload[SecureRealtimeMovementInputBytes]{};
    if (!TryEncodeSecureRealtimeMovementInput(
            movement,
            SecureRealtimeMovementSource::TlsFallback,
            payload,
            sizeof(payload))) {
        SecureZeroMemory(payload, sizeof(payload));
        return false;
    }

    const auto deadline =
        GetTickCount64() + WriteDeadlineMilliseconds;
    AcquireSRWLockExclusive(&writeLock_);
    const bool written = WriteFrame(
        SecureFrameType::RealtimeMovementInput,
        payload,
        sizeof(payload),
        deadline);
    ReleaseSRWLockExclusive(&writeLock_);
    SecureZeroMemory(payload, sizeof(payload));
    if (!written) {
        Fail(SecureOuterFailure::RealtimeMovementWrite);
    }
    return written;
}

} // namespace godswar::network
