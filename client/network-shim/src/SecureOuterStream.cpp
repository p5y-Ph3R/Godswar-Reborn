#include "SecureOuterStream.h"

#include <algorithm>
#include <cstring>
#include <limits>
#include <utility>

namespace godswar::network {
namespace {

bool IsRole(SecureEndpointRole role) noexcept {
    return role == SecureEndpointRole::Login ||
        role == SecureEndpointRole::Game;
}

ULONGLONG DeadlineAfter(DWORD milliseconds) noexcept {
    return GetTickCount64() + milliseconds;
}

} // namespace

SecureOuterStream::SecureOuterStream(
    IDeadlinePlaintextStream* plaintextStream,
    SecureGameGrantRegistry* grantRegistry,
    SecurePendingOperationRegistry* operationRegistry) noexcept
    : plaintextStream_(plaintextStream),
      grantRegistry_(grantRegistry),
      operationRegistry_(operationRegistry) {
    InitializeSRWLock(&writeLock_);
    InitializeSRWLock(&snapshotLock_);
}

SecureOuterStream::~SecureOuterStream() noexcept {
    Stop();
    SecureZeroMemory(inboundPayload_, sizeof(inboundPayload_));
}

bool SecureOuterStream::Establish(
    SecureEndpointRole role,
    const std::uint8_t* clientInstanceId,
    std::size_t clientInstanceIdBytes,
    const std::uint8_t* originSha256,
    std::size_t originSha256Bytes) noexcept {
    if (plaintextStream_ == nullptr ||
        established_ ||
        IsStopped() ||
        !IsRole(role)) {
        Fail(SecureOuterFailure::InvalidArgument);
        return false;
    }

    std::uint8_t clientPreface[SecureClientPrefaceBytes]{};
    if (!TryEncodeSecureClientPreface(
            role,
            clientInstanceId,
            clientInstanceIdBytes,
            originSha256,
            originSha256Bytes,
            clientPreface,
            sizeof(clientPreface))) {
        Fail(SecureOuterFailure::InvalidArgument);
        return false;
    }

    const ULONGLONG deadline =
        DeadlineAfter(PrefaceDeadlineMilliseconds);
    if (!plaintextStream_->WriteAll(
            clientPreface,
            sizeof(clientPreface),
            deadline)) {
        SecureZeroMemory(clientPreface, sizeof(clientPreface));
        Fail(
            GetTickCount64() >= deadline
                ? SecureOuterFailure::OperationDeadline
                : SecureOuterFailure::PrefaceWrite);
        return false;
    }
    SecureZeroMemory(clientPreface, sizeof(clientPreface));

    std::uint8_t serverPreface[SecureServerPrefaceBytes]{};
    const DeadlineStreamResult read = ReadExact(
        serverPreface,
        sizeof(serverPreface),
        deadline,
        0);
    if (read.status != DeadlineStreamStatus::Success) {
        SecureZeroMemory(serverPreface, sizeof(serverPreface));
        Fail(
            read.status == DeadlineStreamStatus::TimedOut
                ? SecureOuterFailure::OperationDeadline
                : SecureOuterFailure::PrefaceRead);
        return false;
    }

    SecureServerPrefaceView decoded{};
    if (!TryDecodeSecureServerPreface(
            serverPreface,
            sizeof(serverPreface),
            role,
            &decoded) ||
        decoded.status != SecureServerPrefaceStatus::Ok) {
        SecureZeroMemory(serverPreface, sizeof(serverPreface));
        SecureZeroMemory(&decoded, sizeof(decoded));
        Fail(SecureOuterFailure::PrefaceRejected);
        return false;
    }

    AcquireSRWLockExclusive(&snapshotLock_);
    role_ = role;
    established_ = true;
    gameBound_ = role == SecureEndpointRole::Login;
    grantCommitted_ = false;
    grantExposed_ = false;
    committedGrantGeneration_ = 0;
    std::memcpy(
        connectionId_,
        decoded.connectionId,
        sizeof(connectionId_));
    connectionIdRetained_ = true;
    udpBindingGrantReceived_ = false;
    udpBindingGrantAvailable_ = false;
    ReleaseSRWLockExclusive(&snapshotLock_);
    SecureZeroMemory(serverPreface, sizeof(serverPreface));
    SecureZeroMemory(&decoded, sizeof(decoded));
    return true;
}

ByteStreamIoResult SecureOuterStream::Read(
    void* destination,
    std::size_t destinationCapacity) noexcept {
    SecureEndpointRole currentRole = SecureEndpointRole::Login;
    bool currentBound = false;
    AcquireSRWLockShared(&snapshotLock_);
    currentRole = role_;
    currentBound = gameBound_;
    ReleaseSRWLockShared(&snapshotLock_);
    if (destination == nullptr ||
        destinationCapacity == 0 ||
        !established_ ||
        (currentRole == SecureEndpointRole::Game && !currentBound) ||
        IsStopped()) {
        return {ByteStreamIoStatus::Failed, 0};
    }

    if (inboundBytes_ > 0) {
        const std::size_t copied = (std::min)(
            destinationCapacity,
            inboundBytes_);
        std::memcpy(
            destination,
            inboundPayload_ + inboundOffset_,
            copied);
        SecureZeroMemory(
            inboundPayload_ + inboundOffset_,
            copied);
        inboundOffset_ += copied;
        inboundBytes_ -= copied;
        if (inboundBytes_ == 0) {
            inboundOffset_ = 0;
        }
        return {ByteStreamIoStatus::Success, copied};
    }

    for (unsigned controlFrames = 0;
         controlFrames < 8;
         ++controlFrames) {
        std::uint8_t encodedHeader[SecureFrameHeaderBytes]{};
        const DeadlineStreamResult headerRead = ReadExact(
            encodedHeader,
            sizeof(encodedHeader),
            DeadlineAfter(IdleDeadlineMilliseconds),
            FrameHeaderDeadlineMilliseconds);
        if (headerRead.status == DeadlineStreamStatus::EndOfStream &&
            headerRead.bytesTransferred == 0) {
            return {ByteStreamIoStatus::EndOfStream, 0};
        }
        if (headerRead.status != DeadlineStreamStatus::Success) {
            Fail(
                headerRead.status == DeadlineStreamStatus::TimedOut
                    ? SecureOuterFailure::OperationDeadline
                    : SecureOuterFailure::FrameHeader);
            return {ByteStreamIoStatus::Failed, 0};
        }

        SecureEndpointRole role = SecureEndpointRole::Login;
        std::uint64_t expectedSequence = 0;
        AcquireSRWLockShared(&snapshotLock_);
        role = role_;
        expectedSequence = nextInboundSequence_;
        ReleaseSRWLockShared(&snapshotLock_);

        SecureFrameHeader header{};
        if (!TryDecodeSecureFrameHeader(
                encodedHeader,
                sizeof(encodedHeader),
                role,
                SecureFrameDirection::ServerToClient,
                expectedSequence,
                &header)) {
            Fail(SecureOuterFailure::FrameHeader);
            return {ByteStreamIoStatus::Failed, 0};
        }

        std::uint64_t followingSequence = 0;
        if (!TryGetNextSecureSequence(
                expectedSequence,
                &followingSequence)) {
            Fail(SecureOuterFailure::FrameSequenceExhausted);
            return {ByteStreamIoStatus::Failed, 0};
        }

        const DeadlineStreamResult bodyRead = ReadExact(
            inboundPayload_,
            header.payloadBytes,
            DeadlineAfter(FrameBodyDeadlineMilliseconds),
            0);
        if (bodyRead.status != DeadlineStreamStatus::Success) {
            Fail(
                bodyRead.status == DeadlineStreamStatus::TimedOut
                    ? SecureOuterFailure::OperationDeadline
                    : SecureOuterFailure::FrameBody);
            return {ByteStreamIoStatus::Failed, 0};
        }
        AcquireSRWLockExclusive(&snapshotLock_);
        nextInboundSequence_ = followingSequence;
        ReleaseSRWLockExclusive(&snapshotLock_);

        if (header.type == SecureFrameType::LegacyBytes) {
            AcquireSRWLockExclusive(&snapshotLock_);
            if (grantCommitted_) {
                grantExposed_ = true;
            }
            ReleaseSRWLockExclusive(&snapshotLock_);
            inboundOffset_ = 0;
            inboundBytes_ = header.payloadBytes;
            const std::size_t copied = (std::min)(
                destinationCapacity,
                inboundBytes_);
            std::memcpy(destination, inboundPayload_, copied);
            SecureZeroMemory(inboundPayload_, copied);
            inboundOffset_ = copied;
            inboundBytes_ -= copied;
            if (inboundBytes_ == 0) {
                inboundOffset_ = 0;
            }
            return {ByteStreamIoStatus::Success, copied};
        }

        if (header.type == SecureFrameType::Ping) {
            AcquireSRWLockExclusive(&writeLock_);
            const bool pongWritten = WriteFrame(
                SecureFrameType::Pong,
                inboundPayload_,
                header.payloadBytes,
                DeadlineAfter(WriteDeadlineMilliseconds));
            ReleaseSRWLockExclusive(&writeLock_);
            SecureZeroMemory(
                inboundPayload_,
                header.payloadBytes);
            if (!pongWritten) {
                Fail(SecureOuterFailure::PongWrite);
                return {ByteStreamIoStatus::Failed, 0};
            }
            continue;
        }

        if (header.type == SecureFrameType::GameGrant) {
            bool alreadyCommitted = false;
            AcquireSRWLockShared(&snapshotLock_);
            alreadyCommitted = grantCommitted_;
            ReleaseSRWLockShared(&snapshotLock_);
            if (alreadyCommitted) {
                SecureZeroMemory(
                    inboundPayload_,
                    header.payloadBytes);
                Fail(SecureOuterFailure::UnsupportedControl);
                return {ByteStreamIoStatus::Failed, 0};
            }
            SecureGameGrant grant;
            const bool decoded = TryDecodeSecureGameGrant(
                inboundPayload_,
                header.payloadBytes,
                &grant);
            SecureZeroMemory(
                inboundPayload_,
                header.payloadBytes);
            if (!decoded) {
                Fail(SecureOuterFailure::GrantDecode);
                return {ByteStreamIoStatus::Failed, 0};
            }
            std::uint64_t committedGeneration = 0;
            if (grantRegistry_ == nullptr ||
                grantRegistry_->Commit(
                    std::move(grant),
                    &committedGeneration) !=
                    SecureGameGrantResult::Success) {
                Fail(SecureOuterFailure::GrantCommit);
                return {ByteStreamIoStatus::Failed, 0};
            }
            AcquireSRWLockExclusive(&snapshotLock_);
            grantCommitted_ = true;
            grantExposed_ = false;
            committedGrantGeneration_ = committedGeneration;
            ReleaseSRWLockExclusive(&snapshotLock_);
            continue;
        }

        if (header.type == SecureFrameType::UdpBindingGrant) {
            SecureOuterFailure grantFailure =
                SecureOuterFailure::None;
            const bool retained = TryRetainUdpBindingGrant(
                inboundPayload_,
                header.payloadBytes,
                &grantFailure);
            SecureZeroMemory(
                inboundPayload_,
                header.payloadBytes);
            if (!retained) {
                Fail(grantFailure);
                return {ByteStreamIoStatus::Failed, 0};
            }
            continue;
        }

        if (header.type ==
            SecureFrameType::LegacyCommandResult) {
            SecureLegacyCommandResult result{};
            const bool decoded =
                TryDecodeSecureLegacyCommandResult(
                    inboundPayload_,
                    header.payloadBytes,
                    &result);
            SecureZeroMemory(
                inboundPayload_,
                header.payloadBytes);
            const bool resolved =
                decoded &&
                operationRegistry_ != nullptr &&
                operationRegistry_->Resolve(result) ==
                    SecureOperationRegistryResult::Success;
            SecureZeroMemory(&result, sizeof(result));
            if (!resolved) {
                Fail(
                    SecureOuterFailure::
                        LegacyCommandResult);
                return {ByteStreamIoStatus::Failed, 0};
            }
            continue;
        }

        SecureZeroMemory(
            inboundPayload_,
            header.payloadBytes);
        if (header.type == SecureFrameType::Close) {
            return {ByteStreamIoStatus::EndOfStream, 0};
        }

        // Control state and secret ownership remain outside the stock legacy
        // byte stream.
        Fail(SecureOuterFailure::UnsupportedControl);
        return {ByteStreamIoStatus::Failed, 0};
    }

    Fail(SecureOuterFailure::UnsupportedControl);
    return {ByteStreamIoStatus::Failed, 0};
}

void SecureOuterStream::Stop() noexcept {
    InvalidateUnexposedGrant();
    ClearUdpBindingState();
    if (InterlockedCompareExchange(&stopped_, 1, 0) == 0) {
        InterlockedCompareExchange(
            &failure_,
            static_cast<LONG>(SecureOuterFailure::Stopped),
            static_cast<LONG>(SecureOuterFailure::None));
        if (plaintextStream_ != nullptr) {
            plaintextStream_->Stop();
        }
    }
}

SecureOuterSnapshot SecureOuterStream::Snapshot() const noexcept {
    SecureOuterSnapshot snapshot{};
    AcquireSRWLockShared(&snapshotLock_);
    snapshot.established = established_;
    snapshot.gameBound = gameBound_;
    snapshot.hasUdpBindingGrant = udpBindingGrantAvailable_;
    snapshot.role = role_;
    snapshot.nextInboundSequence = nextInboundSequence_;
    snapshot.nextOutboundSequence = nextOutboundSequence_;
    ReleaseSRWLockShared(&snapshotLock_);
    snapshot.stopped = IsStopped();
    snapshot.failure = static_cast<SecureOuterFailure>(
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&failure_),
            0,
            0));
    return snapshot;
}

DeadlineStreamResult SecureOuterStream::ReadExact(
    void* destination,
    std::size_t bytes,
    ULONGLONG firstDeadline,
    DWORD partialDeadlineMilliseconds) noexcept {
    if (destination == nullptr ||
        bytes == 0 ||
        plaintextStream_ == nullptr) {
        return {DeadlineStreamStatus::Failed, 0};
    }

    auto* output = static_cast<std::uint8_t*>(destination);
    std::size_t offset = 0;
    ULONGLONG deadline = firstDeadline;
    while (offset < bytes) {
        const DeadlineStreamResult read = plaintextStream_->Read(
            output + offset,
            bytes - offset,
            deadline);
        if (read.status != DeadlineStreamStatus::Success) {
            return {read.status, offset};
        }
        if (read.bytesTransferred == 0 ||
            read.bytesTransferred > bytes - offset) {
            return {DeadlineStreamStatus::Failed, offset};
        }
        offset += read.bytesTransferred;
        if (partialDeadlineMilliseconds != 0 &&
            offset < bytes &&
            offset == read.bytesTransferred) {
            deadline = DeadlineAfter(partialDeadlineMilliseconds);
        }
    }

    return {DeadlineStreamStatus::Success, offset};
}

bool SecureOuterStream::WriteFrame(
    SecureFrameType type,
    const void* payload,
    std::size_t payloadBytes,
    ULONGLONG deadline) noexcept {
    if (payload == nullptr ||
        payloadBytes == 0 ||
        payloadBytes > SecureMaximumPayloadBytes ||
        plaintextStream_ == nullptr ||
        nextOutboundSequence_ == 0) {
        return false;
    }

    SecureEndpointRole role = SecureEndpointRole::Login;
    std::uint64_t currentSequence = 0;
    AcquireSRWLockShared(&snapshotLock_);
    role = role_;
    currentSequence = nextOutboundSequence_;
    ReleaseSRWLockShared(&snapshotLock_);

    std::uint8_t encodedHeader[SecureFrameHeaderBytes]{};
    std::uint64_t followingSequence = 0;
    if (!TryGetNextSecureSequence(
            currentSequence,
            &followingSequence)) {
        return false;
    }
    const SecureFrameHeader header{
        static_cast<std::uint32_t>(payloadBytes),
        type,
        currentSequence};
    if (!TryEncodeSecureFrameHeader(
            header,
            role,
            SecureFrameDirection::ClientToServer,
            encodedHeader,
            sizeof(encodedHeader)) ||
        !plaintextStream_->WriteAll(
            encodedHeader,
            sizeof(encodedHeader),
            deadline) ||
        !plaintextStream_->WriteAll(
            payload,
            payloadBytes,
            deadline)) {
        return false;
    }

    AcquireSRWLockExclusive(&snapshotLock_);
    nextOutboundSequence_ = followingSequence;
    ReleaseSRWLockExclusive(&snapshotLock_);
    return true;
}

void SecureOuterStream::Fail(
    SecureOuterFailure failure) noexcept {
    InterlockedCompareExchange(
        &failure_,
        static_cast<LONG>(failure),
        static_cast<LONG>(SecureOuterFailure::None));
    InvalidateUnexposedGrant();
    ClearUdpBindingState();
    if (InterlockedCompareExchange(&stopped_, 1, 0) == 0 &&
        plaintextStream_ != nullptr) {
        plaintextStream_->Stop();
    }
}

void SecureOuterStream::InvalidateUnexposedGrant() noexcept {
    SecureGameGrantRegistry* registry = nullptr;
    std::uint64_t generation = 0;
    AcquireSRWLockExclusive(&snapshotLock_);
    if (grantRegistry_ != nullptr &&
        grantCommitted_ &&
        !grantExposed_ &&
        committedGrantGeneration_ != 0) {
        registry = grantRegistry_;
        generation = committedGrantGeneration_;
        committedGrantGeneration_ = 0;
        grantCommitted_ = false;
    }
    ReleaseSRWLockExclusive(&snapshotLock_);
    if (registry != nullptr) {
        static_cast<void>(
            registry->EraseIfGeneration(generation));
    }
}

bool SecureOuterStream::IsStopped() const noexcept {
    return InterlockedCompareExchange(
        const_cast<volatile LONG*>(&stopped_),
        0,
        0) != 0;
}

} // namespace godswar::network
