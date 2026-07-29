#include "SecureOuterStream.h"

namespace godswar::network {
namespace {

ULONGLONG DeadlineAfter(DWORD milliseconds) noexcept {
    return GetTickCount64() + milliseconds;
}

} // namespace

bool SecureOuterStream::WriteLegacyCommandOperation(
    const SecureLegacyCommandOperation& operation) noexcept {
    std::uint8_t payload[
        SecureLegacyCommandOperationPayloadBytes]{};
    if (!TryEncodeSecureLegacyCommandOperation(
            operation,
            payload,
            sizeof(payload))) {
        return false;
    }

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
        SecureZeroMemory(payload, sizeof(payload));
        return false;
    }

    AcquireSRWLockExclusive(&writeLock_);
    const bool written = WriteFrame(
        SecureFrameType::LegacyCommandOperation,
        payload,
        sizeof(payload),
        DeadlineAfter(WriteDeadlineMilliseconds));
    ReleaseSRWLockExclusive(&writeLock_);
    SecureZeroMemory(payload, sizeof(payload));
    if (!written) {
        Fail(SecureOuterFailure::LegacyCommandOperationWrite);
        return false;
    }

    return true;
}

} // namespace godswar::network
