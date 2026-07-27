#define SECURITY_WIN32
#define SCHANNEL_USE_BLACKLISTS

#include "SchannelClientStreamInternal.h"

#include "WinSockRuntime.h"

#include <algorithm>
#include <cstring>
#include <limits>

namespace godswar::network {
namespace {

constexpr unsigned MaximumEmptyRecordsPerRead = 16;

} // namespace

SchannelClientStream::State::State(SocketHandle&& value) noexcept
    : socket(static_cast<SocketHandle&&>(value)) {
    InitializeSRWLock(&readLock);
    InitializeSRWLock(&writeLock);
    InitializeSRWLock(&contextLock);
    InitializeSRWLock(&snapshotLock);
    SecInvalidateHandle(&credentials);
    SecInvalidateHandle(&context);

    u_long nonBlocking = 1;
    configured =
        socket.IsValid() &&
        EnsureWinSock() &&
        ioctlsocket(
            socket.Get(),
            FIONBIO,
            &nonBlocking) != SOCKET_ERROR;
}

SchannelClientStream::State::~State() noexcept {
    socket.Shutdown();
    if (SecIsValidHandle(&context)) {
        DeleteSecurityContext(&context);
        SecInvalidateHandle(&context);
    }
    if (SecIsValidHandle(&credentials)) {
        FreeCredentialsHandle(&credentials);
        SecInvalidateHandle(&credentials);
    }
    SecureZeroMemory(targetName, sizeof(targetName));
    SecureZeroMemory(encryptedInput, sizeof(encryptedInput));
    SecureZeroMemory(plaintext, sizeof(plaintext));
    SecureZeroMemory(encryptedOutput, sizeof(encryptedOutput));
}

bool SchannelClientStream::State::IsStopped() const noexcept {
    return InterlockedCompareExchange(
        const_cast<volatile LONG*>(&stopped),
        0,
        0) != 0;
}

bool SchannelClientStream::State::IsEstablished() const noexcept {
    AcquireSRWLockShared(&snapshotLock);
    const bool value = established;
    ReleaseSRWLockShared(&snapshotLock);
    return value;
}

void SchannelClientStream::State::MarkEstablished(
    DWORD protocol,
    DWORD cipherSuite,
    bool validatedAlpn) noexcept {
    AcquireSRWLockExclusive(&snapshotLock);
    negotiatedProtocol = protocol;
    negotiatedCipherSuite = cipherSuite;
    alpnValidated = validatedAlpn;
    established = true;
    ReleaseSRWLockExclusive(&snapshotLock);
}

void SchannelClientStream::State::RecordSecurityStatus(
    SECURITY_STATUS status) noexcept {
    AcquireSRWLockExclusive(&snapshotLock);
    securityStatus = status;
    ReleaseSRWLockExclusive(&snapshotLock);
}

void SchannelClientStream::State::Fail(
    SchannelClientFailure reason) noexcept {
    AcquireSRWLockExclusive(&snapshotLock);
    if (failure == SchannelClientFailure::None) {
        failure = reason;
    }
    InterlockedExchange(&stopped, 1);
    ReleaseSRWLockExclusive(&snapshotLock);
    socket.Shutdown();
}

SchannelClientSnapshot
SchannelClientStream::State::Snapshot() const noexcept {
    SchannelClientSnapshot snapshot{};
    AcquireSRWLockShared(&snapshotLock);
    snapshot.valid = configured;
    snapshot.established = established;
    snapshot.failure = failure;
    snapshot.securityStatus = securityStatus;
    snapshot.negotiatedProtocol = negotiatedProtocol;
    snapshot.negotiatedCipherSuite = negotiatedCipherSuite;
    ReleaseSRWLockShared(&snapshotLock);
    snapshot.stopped = IsStopped();
    return snapshot;
}

bool SchannelClientStream::State::WaitForReady(
    bool write,
    ULONGLONG deadline) noexcept {
    while (!IsStopped()) {
        const DWORD remaining =
            schannel_detail::RemainingMilliseconds(deadline);
        if (remaining == 0) {
            return false;
        }

        fd_set descriptors;
        FD_ZERO(&descriptors);
        FD_SET(socket.Get(), &descriptors);
        timeval timeout{};
        const DWORD pollMilliseconds =
            (std::min)(remaining, static_cast<DWORD>(100));
        timeout.tv_sec = 0;
        timeout.tv_usec = static_cast<long>(
            pollMilliseconds * 1000);
        const int selected = select(
            0,
            write ? nullptr : &descriptors,
            write ? &descriptors : nullptr,
            nullptr,
            &timeout);
        if (selected > 0) {
            return !IsStopped();
        }
        if (selected == SOCKET_ERROR) {
            return false;
        }
    }
    return false;
}

DeadlineStreamResult SchannelClientStream::State::RawRead(
    void* destination,
    std::size_t capacity,
    ULONGLONG deadline) noexcept {
    if (destination == nullptr ||
        capacity == 0 ||
        capacity >
            static_cast<std::size_t>(
                (std::numeric_limits<int>::max)()) ||
        IsStopped()) {
        return {DeadlineStreamStatus::Failed, 0};
    }

    for (;;) {
        if (schannel_detail::RemainingMilliseconds(deadline) == 0) {
            return {DeadlineStreamStatus::TimedOut, 0};
        }
        const int received = recv(
            socket.Get(),
            static_cast<char*>(destination),
            static_cast<int>(capacity),
            0);
        if (received > 0) {
            return {
                DeadlineStreamStatus::Success,
                static_cast<std::size_t>(received)};
        }
        if (received == 0 && !IsStopped()) {
            return {DeadlineStreamStatus::EndOfStream, 0};
        }
        if (received == SOCKET_ERROR &&
            WSAGetLastError() == WSAEWOULDBLOCK) {
            if (WaitForReady(false, deadline)) {
                continue;
            }
            return {
                schannel_detail::RemainingMilliseconds(deadline) == 0
                    ? DeadlineStreamStatus::TimedOut
                    : DeadlineStreamStatus::Failed,
                0};
        }
        return {DeadlineStreamStatus::Failed, 0};
    }
}

bool SchannelClientStream::State::RawWriteAll(
    const void* source,
    std::size_t bytes,
    ULONGLONG deadline) noexcept {
    if (source == nullptr || bytes == 0 || IsStopped()) {
        return false;
    }

    const auto* input = static_cast<const std::uint8_t*>(source);
    std::size_t offset = 0;
    while (offset < bytes) {
        if (schannel_detail::RemainingMilliseconds(deadline) == 0) {
            return false;
        }
        const std::size_t remaining = bytes - offset;
        const int bounded = static_cast<int>(
            (std::min)(
                remaining,
                static_cast<std::size_t>(
                    (std::numeric_limits<int>::max)())));
        const int sent = send(
            socket.Get(),
            reinterpret_cast<const char*>(input + offset),
            bounded,
            0);
        if (sent > 0) {
            offset += static_cast<std::size_t>(sent);
            continue;
        }
        if (sent == SOCKET_ERROR &&
            WSAGetLastError() == WSAEWOULDBLOCK &&
            WaitForReady(true, deadline)) {
            continue;
        }
        return false;
    }
    return true;
}

DeadlineStreamResult SchannelClientStream::Read(
    void* destination,
    std::size_t destinationBytes,
    ULONGLONG absoluteDeadline) noexcept {
    if (state_ == nullptr ||
        destination == nullptr ||
        destinationBytes == 0 ||
        !state_->IsEstablished() ||
        state_->IsStopped()) {
        return {DeadlineStreamStatus::Failed, 0};
    }

    AcquireSRWLockExclusive(&state_->readLock);
    DeadlineStreamResult result{DeadlineStreamStatus::Failed, 0};
    unsigned emptyRecords = 0;
    unsigned postHandshakeTransitions = 0;
    while (!state_->IsStopped()) {
        if (state_->plaintextBytes > 0) {
            const std::size_t copied = (std::min)(
                destinationBytes,
                state_->plaintextBytes);
            std::memcpy(
                destination,
                state_->plaintext + state_->plaintextOffset,
                copied);
            SecureZeroMemory(
                state_->plaintext + state_->plaintextOffset,
                copied);
            state_->plaintextOffset += copied;
            state_->plaintextBytes -= copied;
            if (state_->plaintextBytes == 0) {
                state_->plaintextOffset = 0;
            }
            result = {DeadlineStreamStatus::Success, copied};
            break;
        }

        if (state_->encryptedInputBytes == 0) {
            const auto read = state_->RawRead(
                state_->encryptedInput,
                sizeof(state_->encryptedInput),
                absoluteDeadline);
            if (read.status != DeadlineStreamStatus::Success) {
                if (read.status == DeadlineStreamStatus::EndOfStream) {
                    state_->Fail(SchannelClientFailure::TruncatedStream);
                    result = {DeadlineStreamStatus::Failed, 0};
                } else {
                    result = read;
                }
                break;
            }
            state_->encryptedInputBytes = read.bytesTransferred;
        }

        SecBuffer buffers[4]{};
        buffers[0].BufferType = SECBUFFER_DATA;
        buffers[0].cbBuffer = static_cast<unsigned long>(
            state_->encryptedInputBytes);
        buffers[0].pvBuffer = state_->encryptedInput;
        for (std::size_t index = 1; index < 4; ++index) {
            buffers[index].BufferType = SECBUFFER_EMPTY;
        }
        SecBufferDesc message{};
        message.ulVersion = SECBUFFER_VERSION;
        message.cBuffers = 4;
        message.pBuffers = buffers;

        AcquireSRWLockExclusive(&state_->contextLock);
        const SECURITY_STATUS status = DecryptMessage(
            &state_->context,
            &message,
            0,
            nullptr);
        if (status == SEC_I_RENEGOTIATE) {
            ++postHandshakeTransitions;
            bool continued = false;
            if (postHandshakeTransitions <=
                schannel_detail::
                    MaximumPostHandshakeTransitionsPerRead) {
                continued = state_->ContinueTls13PostHandshake(
                    buffers,
                    4,
                    absoluteDeadline);
            } else {
                state_->Fail(
                    SchannelClientFailure::PostHandshakeLimit);
            }
            ReleaseSRWLockExclusive(&state_->contextLock);
            if (!continued) {
                break;
            }
            continue;
        }
        ReleaseSRWLockExclusive(&state_->contextLock);
        if (status == SEC_E_INCOMPLETE_MESSAGE) {
            if (state_->encryptedInputBytes ==
                sizeof(state_->encryptedInput)) {
                state_->Fail(SchannelClientFailure::RecordRead);
                break;
            }
            const auto read = state_->RawRead(
                state_->encryptedInput + state_->encryptedInputBytes,
                sizeof(state_->encryptedInput) -
                    state_->encryptedInputBytes,
                absoluteDeadline);
            if (read.status != DeadlineStreamStatus::Success) {
                result = read.status == DeadlineStreamStatus::EndOfStream
                    ? DeadlineStreamResult{
                          DeadlineStreamStatus::Failed,
                          0}
                    : read;
                if (read.status == DeadlineStreamStatus::EndOfStream) {
                    state_->Fail(
                        SchannelClientFailure::TruncatedStream);
                }
                break;
            }
            state_->encryptedInputBytes += read.bytesTransferred;
            continue;
        }
        if (status == SEC_I_CONTEXT_EXPIRED) {
            SecureZeroMemory(
                state_->encryptedInput,
                state_->encryptedInputBytes);
            state_->encryptedInputBytes = 0;
            result = {DeadlineStreamStatus::EndOfStream, 0};
            break;
        }
        if (status != SEC_E_OK) {
            SecureZeroMemory(
                state_->encryptedInput,
                state_->encryptedInputBytes);
            state_->encryptedInputBytes = 0;
            state_->Fail(SchannelClientFailure::RecordRead);
            break;
        }

        SecBuffer* data = nullptr;
        std::size_t extraBytes = 0;
        for (auto& buffer : buffers) {
            if (buffer.BufferType == SECBUFFER_DATA) {
                data = &buffer;
            } else if (buffer.BufferType == SECBUFFER_EXTRA) {
                extraBytes = buffer.cbBuffer;
            }
        }
        if (extraBytes > state_->encryptedInputBytes ||
            (data != nullptr &&
                data->cbBuffer > sizeof(state_->plaintext))) {
            SecureZeroMemory(
                state_->encryptedInput,
                state_->encryptedInputBytes);
            state_->encryptedInputBytes = 0;
            state_->Fail(SchannelClientFailure::RecordRead);
            break;
        }

        if (data != nullptr && data->cbBuffer > 0) {
            std::memcpy(
                state_->plaintext,
                data->pvBuffer,
                data->cbBuffer);
            state_->plaintextOffset = 0;
            state_->plaintextBytes = data->cbBuffer;
        }
        const std::size_t processedBytes =
            state_->encryptedInputBytes;
        if (extraBytes > 0) {
            std::memmove(
                state_->encryptedInput,
                state_->encryptedInput + processedBytes - extraBytes,
                extraBytes);
        }
        if (processedBytes > extraBytes) {
            SecureZeroMemory(
                state_->encryptedInput + extraBytes,
                processedBytes - extraBytes);
        }
        state_->encryptedInputBytes = extraBytes;

        if (state_->plaintextBytes == 0) {
            ++emptyRecords;
            if (emptyRecords > MaximumEmptyRecordsPerRead) {
                state_->Fail(SchannelClientFailure::RecordRead);
                break;
            }
        }
    }
    ReleaseSRWLockExclusive(&state_->readLock);
    return result;
}

bool SchannelClientStream::WriteAll(
    const void* source,
    std::size_t sourceBytes,
    ULONGLONG absoluteDeadline) noexcept {
    if (state_ == nullptr ||
        source == nullptr ||
        sourceBytes == 0 ||
        !state_->IsEstablished() ||
        state_->IsStopped()) {
        return false;
    }

    AcquireSRWLockExclusive(&state_->writeLock);
    const auto* input = static_cast<const std::uint8_t*>(source);
    std::size_t offset = 0;
    bool succeeded = true;
    while (offset < sourceBytes && !state_->IsStopped()) {
        if (schannel_detail::RemainingMilliseconds(
                absoluteDeadline) == 0) {
            succeeded = false;
            break;
        }

        AcquireSRWLockExclusive(&state_->contextLock);
        const std::size_t chunk = (std::min)(
            sourceBytes - offset,
            static_cast<std::size_t>(
                state_->streamSizes.cbMaximumMessage));
        const std::size_t dataOffset =
            state_->streamSizes.cbHeader;
        const std::size_t trailerOffset = dataOffset + chunk;
        std::memcpy(
            state_->encryptedOutput + dataOffset,
            input + offset,
            chunk);

        SecBuffer buffers[4]{};
        buffers[0] = {
            state_->streamSizes.cbHeader,
            SECBUFFER_STREAM_HEADER,
            state_->encryptedOutput};
        buffers[1] = {
            static_cast<unsigned long>(chunk),
            SECBUFFER_DATA,
            state_->encryptedOutput + dataOffset};
        buffers[2] = {
            state_->streamSizes.cbTrailer,
            SECBUFFER_STREAM_TRAILER,
            state_->encryptedOutput + trailerOffset};
        buffers[3].BufferType = SECBUFFER_EMPTY;
        SecBufferDesc message{};
        message.ulVersion = SECBUFFER_VERSION;
        message.cBuffers = 4;
        message.pBuffers = buffers;

        const bool recordWritten = EncryptMessage(
                &state_->context,
                0,
                &message,
                0) == SEC_E_OK &&
            state_->RawWriteAll(
                buffers[0].pvBuffer,
                buffers[0].cbBuffer,
                absoluteDeadline) &&
            state_->RawWriteAll(
                buffers[1].pvBuffer,
                buffers[1].cbBuffer,
                absoluteDeadline) &&
            state_->RawWriteAll(
                buffers[2].pvBuffer,
                buffers[2].cbBuffer,
                absoluteDeadline);
        ReleaseSRWLockExclusive(&state_->contextLock);
        if (!recordWritten) {
            succeeded = false;
            break;
        }
        SecureZeroMemory(
            state_->encryptedOutput,
            trailerOffset + state_->streamSizes.cbTrailer);
        offset += chunk;
    }

    if (!succeeded) {
        SecureZeroMemory(
            state_->encryptedOutput,
            sizeof(state_->encryptedOutput));
        state_->Fail(SchannelClientFailure::RecordWrite);
    }
    ReleaseSRWLockExclusive(&state_->writeLock);
    return succeeded && offset == sourceBytes;
}

void SchannelClientStream::Stop() noexcept {
    if (state_ != nullptr) {
        state_->Fail(SchannelClientFailure::Stopped);
    }
}

SchannelClientSnapshot
SchannelClientStream::Snapshot() const noexcept {
    return state_ == nullptr
        ? SchannelClientSnapshot{}
        : state_->Snapshot();
}

} // namespace godswar::network
