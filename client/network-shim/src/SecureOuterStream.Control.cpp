#include "SecureOuterStream.h"

#include <Windows.h>

#include <cstdint>

namespace godswar::network {
namespace {

ULONGLONG DeadlineAfter(DWORD milliseconds) noexcept {
    return GetTickCount64() + milliseconds;
}

} // namespace

bool SecureOuterStream::PresentGameBind(
    SecureGameGrant* grant) noexcept {
    SecureEndpointRole role = SecureEndpointRole::Login;
    bool established = false;
    bool alreadyBound = false;
    AcquireSRWLockShared(&snapshotLock_);
    role = role_;
    established = established_;
    alreadyBound = gameBound_;
    ReleaseSRWLockShared(&snapshotLock_);

    if (grant == nullptr ||
        !grant->IsValid() ||
        !established ||
        role != SecureEndpointRole::Game ||
        alreadyBound ||
        IsStopped()) {
        if (grant != nullptr) {
            grant->Clear();
        }
        Fail(SecureOuterFailure::InvalidState);
        return false;
    }

    std::uint8_t bindBytes[SecureGameBindBytes]{};
    const bool encoded = TryEncodeSecureGameBind(
        *grant,
        bindBytes,
        sizeof(bindBytes));
    // BeginPresentation is terminal. Never retain or return a ticket after a
    // bind attempt, even if the underlying write fails after partial output.
    grant->Clear();
    if (!encoded) {
        SecureZeroMemory(bindBytes, sizeof(bindBytes));
        Fail(SecureOuterFailure::InvalidArgument);
        return false;
    }

    const ULONGLONG bindDeadline =
        DeadlineAfter(GameBindDeadlineMilliseconds);
    AcquireSRWLockExclusive(&writeLock_);
    const bool written = WriteFrame(
        SecureFrameType::GameBind,
        bindBytes,
        sizeof(bindBytes),
        bindDeadline);
    ReleaseSRWLockExclusive(&writeLock_);
    SecureZeroMemory(bindBytes, sizeof(bindBytes));
    if (!written) {
        Fail(SecureOuterFailure::BindWrite);
        return false;
    }

    std::uint8_t encodedHeader[SecureFrameHeaderBytes]{};
    const DeadlineStreamResult headerRead = ReadExact(
        encodedHeader,
        sizeof(encodedHeader),
        bindDeadline,
        0);
    if (headerRead.status != DeadlineStreamStatus::Success) {
        Fail(
            headerRead.status == DeadlineStreamStatus::TimedOut
                ? SecureOuterFailure::OperationDeadline
                : SecureOuterFailure::BindResult);
        return false;
    }

    std::uint64_t expectedSequence = 0;
    AcquireSRWLockShared(&snapshotLock_);
    expectedSequence = nextInboundSequence_;
    ReleaseSRWLockShared(&snapshotLock_);
    SecureFrameHeader header{};
    if (!TryDecodeSecureFrameHeader(
            encodedHeader,
            sizeof(encodedHeader),
            SecureEndpointRole::Game,
            SecureFrameDirection::ServerToClient,
            expectedSequence,
            &header) ||
        header.type != SecureFrameType::BindResult) {
        Fail(SecureOuterFailure::BindResult);
        return false;
    }

    std::uint64_t followingSequence = 0;
    if (!TryGetNextSecureSequence(
            expectedSequence,
            &followingSequence)) {
        Fail(SecureOuterFailure::FrameSequenceExhausted);
        return false;
    }

    std::uint8_t resultBytes[SecureBindResultBytes]{};
    const DeadlineStreamResult resultRead = ReadExact(
        resultBytes,
        sizeof(resultBytes),
        bindDeadline,
        0);
    if (resultRead.status != DeadlineStreamStatus::Success) {
        SecureZeroMemory(resultBytes, sizeof(resultBytes));
        Fail(
            resultRead.status == DeadlineStreamStatus::TimedOut
                ? SecureOuterFailure::OperationDeadline
                : SecureOuterFailure::BindResult);
        return false;
    }

    SecureBindStatus status = SecureBindStatus::PolicyRejected;
    const bool decoded = TryDecodeSecureBindResult(
        resultBytes,
        sizeof(resultBytes),
        &status);
    SecureZeroMemory(resultBytes, sizeof(resultBytes));
    if (!decoded) {
        Fail(SecureOuterFailure::BindResult);
        return false;
    }

    AcquireSRWLockExclusive(&snapshotLock_);
    nextInboundSequence_ = followingSequence;
    if (status == SecureBindStatus::Accepted) {
        gameBound_ = true;
    }
    ReleaseSRWLockExclusive(&snapshotLock_);
    if (status != SecureBindStatus::Accepted) {
        Fail(SecureOuterFailure::BindRejected);
        return false;
    }
    return true;
}

} // namespace godswar::network
