#include "SecureOuterStream.h"

namespace godswar::network {
namespace {

ULONGLONG DeadlineAfter(DWORD milliseconds) noexcept {
    return GetTickCount64() + milliseconds;
}

} // namespace

ByteStreamIoResult SecureOuterStream::Write(
    const void* source,
    std::size_t sourceBytes) noexcept {
    return WriteDescribedLegacyBytes(
        nullptr,
        source,
        sourceBytes);
}

ByteStreamIoResult SecureOuterStream::WriteDescribedLegacyBytes(
    const SecureLegacyCommandOperation* operation,
    const void* source,
    std::size_t sourceBytes) noexcept {
    SecureEndpointRole currentRole = SecureEndpointRole::Login;
    bool currentBound = false;
    AcquireSRWLockShared(&snapshotLock_);
    currentRole = role_;
    currentBound = gameBound_;
    ReleaseSRWLockShared(&snapshotLock_);
    if (source == nullptr ||
        sourceBytes == 0 ||
        sourceBytes > SecureMaximumPayloadBytes ||
        !established_ ||
        (currentRole == SecureEndpointRole::Game && !currentBound) ||
        IsStopped()) {
        return {ByteStreamIoStatus::Failed, 0};
    }

    std::uint8_t operationPayload[
        SecureLegacyCommandOperationPayloadBytes]{};
    if (operation != nullptr &&
        !TryEncodeSecureLegacyCommandOperation(
            *operation,
            operationPayload,
            sizeof(operationPayload))) {
        return {ByteStreamIoStatus::Failed, 0};
    }

    AcquireSRWLockExclusive(&writeLock_);
    const ULONGLONG deadline =
        DeadlineAfter(WriteDeadlineMilliseconds);
    const bool markerWritten =
        operation == nullptr ||
        WriteFrame(
            SecureFrameType::LegacyCommandOperation,
            operationPayload,
            sizeof(operationPayload),
            deadline);
    const bool written =
        markerWritten &&
        WriteFrame(
            SecureFrameType::LegacyBytes,
            source,
            sourceBytes,
            deadline);
    ReleaseSRWLockExclusive(&writeLock_);
    SecureZeroMemory(
        operationPayload,
        sizeof(operationPayload));
    if (!written) {
        Fail(
            markerWritten
                ? SecureOuterFailure::LegacyWrite
                : SecureOuterFailure::
                    LegacyCommandOperationWrite);
        return {ByteStreamIoStatus::Failed, 0};
    }

    return {ByteStreamIoStatus::Success, sourceBytes};
}

} // namespace godswar::network
