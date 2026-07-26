#include "SecureUdpClientWorker.h"

#include "SecureClientRuntimeInternal.h"
#include "SecureOuterStream.h"
#include "WinSockRuntime.h"

#include <WS2tcpip.h>

#include <algorithm>
#include <cstring>
#include <new>

namespace godswar::network {
namespace {

DWORD RemainingWait(
    ULONGLONG deadline,
    DWORD timeoutMilliseconds) noexcept {
    if (timeoutMilliseconds == INFINITE) {
        return INFINITE;
    }
    const auto now = GetTickCount64();
    if (now >= deadline) {
        return 0;
    }
    return static_cast<DWORD>((std::min)(
        deadline - now,
        static_cast<ULONGLONG>(MAXDWORD)));
}

} // namespace

SecureUdpClientWorker::SecureUdpClientWorker() noexcept
    : stopEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
    InitializeSRWLock(&lock_);
}

SecureUdpClientWorker::~SecureUdpClientWorker() noexcept {
    if (!StopAndJoin(StopDeadlineMilliseconds)) {
        RaiseFailFastException(nullptr, nullptr, 0);
    }
    if (stopEvent_ != nullptr) {
        CloseHandle(stopEvent_);
        stopEvent_ = nullptr;
    }
}

bool SecureUdpClientWorker::Start(
    SecureUdpBindingGrant* grant,
    const sockaddr* tlsPeer,
    int tlsPeerBytes,
    SecureOuterStream* outerStream) noexcept {
    const bool authoritativeMovement =
        grant != nullptr &&
        grant->HasCapability(
            SecureUdpBindingCapability::
                AuthoritativeMovement);
    if (grant == nullptr ||
        !grant->IsValid() ||
        tlsPeer == nullptr ||
        (authoritativeMovement && outerStream == nullptr) ||
        stopEvent_ == nullptr ||
        !movementRouter_.IsValid() ||
        thread_ != nullptr ||
        published_.state != SecureUdpClientWorkerState::Idle ||
        !CopyPeer(
            tlsPeer,
            tlsPeerBytes,
            grant->UdpPort(),
            &remote_,
            &remoteBytes_)) {
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::InvalidArgument);
        return false;
    }
    if (!movementRouter_.Configure(authoritativeMovement)) {
        grant->Clear();
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::InvalidArgument);
        return false;
    }
    outerStream_ = outerStream;

    std::uint64_t nowUnix = 0;
    std::uint8_t nonce[SecureUdpClientNonceBytes]{};
    if (!ReadSystemUnixMilliseconds(&nowUnix)) {
        grant->Clear();
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::Clock);
        return false;
    }
    if (!GenerateNonce(nonce)) {
        grant->Clear();
        SecureZeroMemory(nonce, sizeof(nonce));
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::Random);
        return false;
    }

    const auto now = GetTickCount64();
    if (!channel_.Initialize(
            grant,
            nonce,
            sizeof(nonce),
            nowUnix,
            now)) {
        SecureZeroMemory(nonce, sizeof(nonce));
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::Channel);
        PublishChannel();
        return false;
    }
    SecureZeroMemory(nonce, sizeof(nonce));
    ResetEvent(stopEvent_);
    SetState(SecureUdpClientWorkerState::Starting);
    PublishChannel();
    PublishMovement();

    thread_ = CreateThread(
        nullptr,
        0,
        ThreadEntry,
        this,
        0,
        nullptr);
    if (thread_ == nullptr) {
        channel_.Stop();
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::Thread,
            static_cast<int>(GetLastError()));
        PublishChannel();
        return false;
    }
    return true;
}

bool SecureUdpClientWorker::StopAndJoin(
    DWORD timeoutMilliseconds) noexcept {
    if (timeoutMilliseconds == 0 ||
        timeoutMilliseconds == INFINITE) {
        return false;
    }

    movementRouter_.Stop();
    HANDLE thread = nullptr;
    AcquireSRWLockExclusive(&lock_);
    thread = thread_;
    if (thread == nullptr) {
        if (published_.state != SecureUdpClientWorkerState::Failed &&
            published_.state !=
                SecureUdpClientWorkerState::TlsFallback) {
            published_.state = SecureUdpClientWorkerState::Stopped;
        }
        ReleaseSRWLockExclusive(&lock_);
        channel_.Stop();
        outerStream_ = nullptr;
        return true;
    }
    if (published_.state !=
            SecureUdpClientWorkerState::TlsFallback &&
        published_.state != SecureUdpClientWorkerState::Failed) {
        published_.state = SecureUdpClientWorkerState::Stopping;
    }
    SetEvent(stopEvent_);
    ReleaseSRWLockExclusive(&lock_);

    const auto deadline =
        GetTickCount64() + timeoutMilliseconds;
    if (WaitForSingleObject(
            thread,
            RemainingWait(deadline, timeoutMilliseconds)) !=
        WAIT_OBJECT_0) {
        SetState(
            SecureUdpClientWorkerState::Failed,
            SecureUdpClientWorkerFailure::StopDeadline);
        return false;
    }

    CloseHandle(thread);
    AcquireSRWLockExclusive(&lock_);
    if (thread_ == thread) {
        thread_ = nullptr;
    }
    if (published_.state != SecureUdpClientWorkerState::Failed &&
        published_.state !=
            SecureUdpClientWorkerState::TlsFallback) {
        published_.state = SecureUdpClientWorkerState::Stopped;
    }
    ReleaseSRWLockExclusive(&lock_);
    channel_.Stop();
    outerStream_ = nullptr;
    PublishChannel();
    PublishMovement();
    return true;
}

SecureRealtimeMovementRouteResult
SecureUdpClientWorker::RouteLegacyMovement(
    const void* packet,
    int packetBytes) noexcept {
    const auto result = movementRouter_.RouteLegacyPacket(
        packet,
        packetBytes,
        GetTickCount64());
    PublishMovement();
    return result;
}

SecureUdpClientWorkerSnapshot
SecureUdpClientWorker::Snapshot() const noexcept {
    AcquireSRWLockShared(&lock_);
    const auto snapshot = published_;
    ReleaseSRWLockShared(&lock_);
    return snapshot;
}

DWORD SecureUdpClientWorker::BindingRetryDelayMilliseconds(
    unsigned completedAttempts) noexcept {
    constexpr DWORD delays[] = {250, 500, 1'000, 1'000};
    if (completedAttempts == 0) {
        return 0;
    }
    const auto index = (std::min)(
        completedAttempts - 1,
        static_cast<unsigned>(
            sizeof(delays) / sizeof(delays[0]) - 1));
    return delays[index];
}

DWORD WINAPI SecureUdpClientWorker::ThreadEntry(
    void* context) noexcept {
    auto* worker = static_cast<SecureUdpClientWorker*>(context);
    return worker != nullptr ? worker->Run() : ERROR_INVALID_PARAMETER;
}

bool SecureUdpClientWorker::GenerateNonce(
    std::uint8_t* nonce) noexcept {
    if (nonce == nullptr) {
        return false;
    }
    for (unsigned attempt = 0; attempt < 4; ++attempt) {
        if (!GenerateSystemSecureRandom(
                nonce,
                SecureUdpClientNonceBytes)) {
            continue;
        }
        std::uint8_t combined = 0;
        for (std::size_t index = 0;
             index < SecureUdpClientNonceBytes;
             ++index) {
            combined |= nonce[index];
        }
        if (combined != 0) {
            return true;
        }
    }
    SecureZeroMemory(nonce, SecureUdpClientNonceBytes);
    return false;
}

bool SecureUdpClientWorker::GeneratePingId(
    std::uint64_t* pingId) noexcept {
    if (pingId == nullptr) {
        return false;
    }
    *pingId = 0;
    for (unsigned attempt = 0; attempt < 4; ++attempt) {
        if (GenerateSystemSecureRandom(
                pingId,
                sizeof(*pingId)) &&
            *pingId != 0) {
            return true;
        }
    }
    *pingId = 0;
    return false;
}

bool SecureUdpClientWorker::ShouldStop() const noexcept {
    return stopEvent_ == nullptr ||
        WaitForSingleObject(stopEvent_, 0) == WAIT_OBJECT_0;
}

void SecureUdpClientWorker::SetState(
    SecureUdpClientWorkerState state,
    SecureUdpClientWorkerFailure failure,
    int nativeError) noexcept {
    AcquireSRWLockExclusive(&lock_);
    published_.state = state;
    if (failure != SecureUdpClientWorkerFailure::None &&
        published_.failure ==
            SecureUdpClientWorkerFailure::None) {
        published_.failure = failure;
        published_.nativeError = nativeError;
    }
    ReleaseSRWLockExclusive(&lock_);
}

void SecureUdpClientWorker::PublishChannel() noexcept {
    const auto channel = channel_.Snapshot();
    AcquireSRWLockExclusive(&lock_);
    published_.channel = channel;
    ReleaseSRWLockExclusive(&lock_);
}

void SecureUdpClientWorker::PublishMovement() noexcept {
    const auto movement = movementRouter_.Snapshot();
    AcquireSRWLockExclusive(&lock_);
    published_.movement = movement;
    ReleaseSRWLockExclusive(&lock_);
}

void SecureUdpClientWorker::EnterTlsFallback(
    SecureUdpClientWorkerFailure failure,
    int nativeError) noexcept {
    channel_.Stop();
    CloseSocket();
    if (movementRouter_.Snapshot().owner ==
        SecureRealtimeMovementOwner::SecureUdp) {
        movementRouter_.Stop();
    }
    SetState(
        SecureUdpClientWorkerState::TlsFallback,
        failure,
        nativeError);
    PublishChannel();
    PublishMovement();
}

bool SecureUdpClientWorker::CopyPeer(
    const sockaddr* source,
    int sourceBytes,
    std::uint16_t udpPort,
    sockaddr_storage* destination,
    int* destinationBytes) noexcept {
    if (source == nullptr ||
        destination == nullptr ||
        destinationBytes == nullptr ||
        udpPort == 0) {
        return false;
    }
    int required = 0;
    if (source->sa_family == AF_INET) {
        required = sizeof(sockaddr_in);
    } else if (source->sa_family == AF_INET6) {
        required = sizeof(sockaddr_in6);
    } else {
        return false;
    }
    if (sourceBytes < required) {
        return false;
    }

    *destination = sockaddr_storage{};
    std::memcpy(destination, source, required);
    if (source->sa_family == AF_INET) {
        reinterpret_cast<sockaddr_in*>(destination)->sin_port =
            htons(udpPort);
    } else {
        reinterpret_cast<sockaddr_in6*>(destination)->sin6_port =
            htons(udpPort);
    }
    *destinationBytes = required;
    return true;
}

} // namespace godswar::network
