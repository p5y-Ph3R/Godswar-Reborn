#include "SecureUdpClientWorker.h"

#include "WinSockRuntime.h"

#include <WinSock2.h>
#include <Windows.h>

#include <cstring>

namespace godswar::network {
namespace {

constexpr std::uint64_t BindingBudgetMilliseconds = 3'500;
constexpr unsigned MaximumBindingAttempts = 5;
constexpr long SelectSliceMicroseconds = 10'000;

} // namespace

DWORD SecureUdpClientWorker::Run() noexcept {
    if (!EnsureWinSock()) {
        EnterTlsFallback(
            SecureUdpClientWorkerFailure::WinSock,
            WSAGetLastError());
        return 0;
    }
    if (!OpenSocket()) {
        EnterTlsFallback(
            SecureUdpClientWorkerFailure::Socket,
            WSAGetLastError());
        return 0;
    }

    const auto start = GetTickCount64();
    nextBindingSendMilliseconds_ = start;
    bindingDeadlineMilliseconds_ =
        start + BindingBudgetMilliseconds;
    SetState(SecureUdpClientWorkerState::Binding);

    while (!ShouldStop()) {
        const auto now = GetTickCount64();
        if (movementRouter_.UdpAcknowledgmentTimedOut(now)) {
            if (SwitchMovementToTls(
                    SecureUdpClientWorkerFailure::
                        GameplayAcknowledgmentTimeout)) {
                static_cast<void>(ContinueTlsFallbackLoop());
                return 0;
            }
            EnterTlsFallback(
                SecureUdpClientWorkerFailure::
                    GameplayAcknowledgmentTimeout);
            return 0;
        }
        const auto channel = channel_.Snapshot();
        if (channel.state ==
                SecureUdpClientChannelState::AwaitingChallenge ||
            channel.state ==
                SecureUdpClientChannelState::AwaitingConfirmation) {
            const auto worker = Snapshot();
            if (now >= bindingDeadlineMilliseconds_) {
                if (SwitchMovementToTls(
                        SecureUdpClientWorkerFailure::
                            PeerTimeout)) {
                    static_cast<void>(
                        ContinueTlsFallbackLoop());
                    return 0;
                }
                EnterTlsFallback(
                    hasReachedActive_
                        ? SecureUdpClientWorkerFailure::PeerTimeout
                        : SecureUdpClientWorkerFailure::
                            HandshakeTimeout);
                return 0;
            }
            if (worker.bindingAttempts <
                    MaximumBindingAttempts &&
                now >= nextBindingSendMilliseconds_ &&
                !SendBindingHello(now)) {
                const int sendError = WSAGetLastError();
                if (SwitchMovementToTls(
                        SecureUdpClientWorkerFailure::Send,
                        sendError)) {
                    static_cast<void>(
                        ContinueTlsFallbackLoop());
                    return 0;
                }
                EnterTlsFallback(
                    SecureUdpClientWorkerFailure::Send,
                    sendError);
                return 0;
            }
        } else if (
            channel.state ==
                SecureUdpClientChannelState::Active) {
            if (!hasReachedActive_) {
                hasReachedActive_ = true;
                SetState(SecureUdpClientWorkerState::Active);
            }
            if (channel_.PeerTimedOut(now)) {
                if (SwitchMovementToTls(
                        SecureUdpClientWorkerFailure::
                            PeerTimeout)) {
                    static_cast<void>(
                        ContinueTlsFallbackLoop());
                    return 0;
                }
                if (!BeginRebind(now)) {
                    EnterTlsFallback(
                        SecureUdpClientWorkerFailure::PeerTimeout);
                    return 0;
                }
            } else if (channel_.KeepaliveDue(now) &&
                !SendKeepalive(now)) {
                const int sendError = WSAGetLastError();
                if (SwitchMovementToTls(
                        SecureUdpClientWorkerFailure::Send,
                        sendError)) {
                    static_cast<void>(
                        ContinueTlsFallbackLoop());
                    return 0;
                }
                if (!BeginRebind(now)) {
                    EnterTlsFallback(
                        SecureUdpClientWorkerFailure::Send,
                        sendError);
                    return 0;
                }
            } else if (!ProcessUdpMovement(now)) {
                const int sendError = WSAGetLastError();
                if (SwitchMovementToTls(
                        SecureUdpClientWorkerFailure::Send,
                        sendError)) {
                    static_cast<void>(
                        ContinueTlsFallbackLoop());
                    return 0;
                }
                EnterTlsFallback(
                    SecureUdpClientWorkerFailure::Send,
                    sendError);
                return 0;
            }
        } else {
            if (SwitchMovementToTls(
                    SecureUdpClientWorkerFailure::Channel)) {
                static_cast<void>(ContinueTlsFallbackLoop());
                return 0;
            }
            EnterTlsFallback(
                SecureUdpClientWorkerFailure::Channel);
            return 0;
        }

        if (!DrainIncoming(now)) {
            const int error = WSAGetLastError();
            if (SwitchMovementToTls(
                    SecureUdpClientWorkerFailure::Socket,
                    error)) {
                static_cast<void>(ContinueTlsFallbackLoop());
                return 0;
            }
            if (hasReachedActive_ && BeginRebind(now)) {
                continue;
            }
            EnterTlsFallback(
                SecureUdpClientWorkerFailure::Socket,
                error);
            return 0;
        }
        PublishChannel();
    }

    CloseSocket();
    if (Snapshot().state !=
            SecureUdpClientWorkerState::TlsFallback &&
        Snapshot().state !=
            SecureUdpClientWorkerState::Failed) {
        SetState(SecureUdpClientWorkerState::Stopped);
    }
    PublishChannel();
    return 0;
}

bool SecureUdpClientWorker::OpenSocket() noexcept {
    CloseSocket();
    socket_ = socket(
        remote_.ss_family,
        SOCK_DGRAM,
        IPPROTO_UDP);
    if (socket_ == INVALID_SOCKET) {
        return false;
    }
    u_long nonblocking = 1;
    int receiveBufferBytes = 64 * 1024;
    int sendBufferBytes = 32 * 1024;
    if (ioctlsocket(socket_, FIONBIO, &nonblocking) ==
            SOCKET_ERROR ||
        setsockopt(
            socket_,
            SOL_SOCKET,
            SO_RCVBUF,
            reinterpret_cast<const char*>(
                &receiveBufferBytes),
            sizeof(receiveBufferBytes)) == SOCKET_ERROR ||
        setsockopt(
            socket_,
            SOL_SOCKET,
            SO_SNDBUF,
            reinterpret_cast<const char*>(&sendBufferBytes),
            sizeof(sendBufferBytes)) == SOCKET_ERROR ||
        connect(
            socket_,
            reinterpret_cast<const sockaddr*>(&remote_),
            remoteBytes_) == SOCKET_ERROR) {
        CloseSocket();
        return false;
    }
    return true;
}

void SecureUdpClientWorker::CloseSocket() noexcept {
    const auto value = socket_;
    socket_ = INVALID_SOCKET;
    if (value != INVALID_SOCKET) {
        closesocket(value);
    }
}

bool SecureUdpClientWorker::SendDatagram(
    const void* datagram,
    std::size_t datagramBytes) noexcept {
    if (socket_ == INVALID_SOCKET ||
        datagram == nullptr ||
        datagramBytes == 0 ||
        datagramBytes > SecureUdpProtectedMaximumBytes) {
        WSASetLastError(WSAEINVAL);
        return false;
    }
    const int sent = send(
        socket_,
        static_cast<const char*>(datagram),
        static_cast<int>(datagramBytes),
        0);
    if (sent != static_cast<int>(datagramBytes)) {
        if (sent >= 0) {
            WSASetLastError(WSAEMSGSIZE);
        }
        return false;
    }
    AcquireSRWLockExclusive(&lock_);
    if (published_.datagramsSent != UINT64_MAX) {
        ++published_.datagramsSent;
    }
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

bool SecureUdpClientWorker::DrainIncoming(
    std::uint64_t nowMilliseconds) noexcept {
    if (socket_ == INVALID_SOCKET) {
        return false;
    }

    fd_set readable;
    FD_ZERO(&readable);
    FD_SET(socket_, &readable);
    timeval timeout{};
    timeout.tv_usec = SelectSliceMicroseconds;
    const int selected = select(
        0,
        &readable,
        nullptr,
        nullptr,
        &timeout);
    if (selected == SOCKET_ERROR) {
        return false;
    }
    if (selected == 0 || !FD_ISSET(socket_, &readable)) {
        return true;
    }

    std::uint8_t datagram[
        SecureUdpProtectedMaximumBytes + 1]{};
    for (unsigned drained = 0; drained < 16; ++drained) {
        const int received = recv(
            socket_,
            reinterpret_cast<char*>(datagram),
            sizeof(datagram),
            0);
        if (received == SOCKET_ERROR) {
            const int error = WSAGetLastError();
            SecureZeroMemory(datagram, sizeof(datagram));
            // WinSock discards an oversized unreliable datagram after
            // reporting WSAEMSGSIZE. It is untrusted packet input, not a
            // socket failure, so it must not trigger rebinding or TLS
            // fallback.
            if (error == WSAEMSGSIZE) {
                AcquireSRWLockExclusive(&lock_);
                if (published_.oversizedDatagramsDropped !=
                    UINT64_MAX) {
                    ++published_.oversizedDatagramsDropped;
                }
                ReleaseSRWLockExclusive(&lock_);
                continue;
            }
            if (error == WSAEWOULDBLOCK) {
                return true;
            }
            WSASetLastError(error);
            return false;
        }
        if (received == 0) {
            SecureZeroMemory(datagram, sizeof(datagram));
            return true;
        }

        AcquireSRWLockExclusive(&lock_);
        if (published_.datagramsReceived != UINT64_MAX) {
            ++published_.datagramsReceived;
        }
        ReleaseSRWLockExclusive(&lock_);
        if (received <=
            static_cast<int>(
                SecureUdpProtectedMaximumBytes)) {
            static_cast<void>(HandleIncoming(
                datagram,
                static_cast<std::size_t>(received),
                nowMilliseconds));
        }
        SecureZeroMemory(datagram, sizeof(datagram));
    }
    return true;
}

bool SecureUdpClientWorker::HandleIncoming(
    const void* datagram,
    std::size_t datagramBytes,
    std::uint64_t nowMilliseconds) noexcept {
    const auto state = channel_.Snapshot().state;
    if ((state ==
            SecureUdpClientChannelState::AwaitingChallenge ||
         state ==
            SecureUdpClientChannelState::AwaitingConfirmation) &&
        datagramBytes == SecureUdpBindingDatagramBytes) {
        std::uint8_t proof[SecureUdpBindingDatagramBytes]{};
        std::size_t proofBytes = 0;
        const bool created = channel_.TryHandleBindingChallenge(
            datagram,
            datagramBytes,
            proof,
            sizeof(proof),
            &proofBytes);
        const bool sent = created &&
            SendDatagram(proof, proofBytes);
        SecureZeroMemory(proof, sizeof(proof));
        if (sent) {
            return true;
        }
    }

    const bool accepted =
        channel_.TryHandleProtectedDatagram(
            datagram,
            datagramBytes,
            nowMilliseconds);
    if (accepted &&
        channel_.Snapshot().state ==
            SecureUdpClientChannelState::Active) {
        hasReachedActive_ = true;
        SetState(SecureUdpClientWorkerState::Active);
    }
    if (accepted &&
        channel_.Snapshot().state ==
            SecureUdpClientChannelState::Active) {
        ConsumePositionSnapshot(nowMilliseconds);
    }
    return accepted;
}

bool SecureUdpClientWorker::SendBindingHello(
    std::uint64_t nowMilliseconds) noexcept {
    std::uint8_t hello[SecureUdpBindingDatagramBytes]{};
    std::size_t helloBytes = 0;
    if (!channel_.TryBuildBindingHello(
            hello,
            sizeof(hello),
            &helloBytes) ||
        !SendDatagram(hello, helloBytes)) {
        SecureZeroMemory(hello, sizeof(hello));
        return false;
    }
    SecureZeroMemory(hello, sizeof(hello));
    nextBindingSendMilliseconds_ =
        nowMilliseconds +
        BindingRetryDelayMilliseconds(
            Snapshot().bindingAttempts + 1);
    AcquireSRWLockExclusive(&lock_);
    ++published_.bindingAttempts;
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

bool SecureUdpClientWorker::SendKeepalive(
    std::uint64_t nowMilliseconds) noexcept {
    std::uint64_t pingId = 0;
    std::uint8_t datagram[
        SecureUdpProtectedMaximumBytes]{};
    std::size_t datagramBytes = 0;
    const bool built = GeneratePingId(&pingId) &&
        channel_.TryBuildPing(
            pingId,
            nowMilliseconds,
            datagram,
            sizeof(datagram),
            &datagramBytes);
    pingId = 0;
    const bool sent = built &&
        SendDatagram(datagram, datagramBytes);
    SecureZeroMemory(datagram, sizeof(datagram));
    return sent;
}

bool SecureUdpClientWorker::BeginRebind(
    std::uint64_t nowMilliseconds) noexcept {
    std::uint8_t nonce[SecureUdpClientNonceBytes]{};
    if (!hasReachedActive_ ||
        !GenerateNonce(nonce) ||
        !channel_.BeginRebind(nonce, sizeof(nonce))) {
        SecureZeroMemory(nonce, sizeof(nonce));
        return false;
    }
    SecureZeroMemory(nonce, sizeof(nonce));
    if (!OpenSocket()) {
        return false;
    }

    nextBindingSendMilliseconds_ = nowMilliseconds;
    bindingDeadlineMilliseconds_ =
        nowMilliseconds + BindingBudgetMilliseconds;
    AcquireSRWLockExclusive(&lock_);
    published_.bindingAttempts = 0;
    ++published_.rebinds;
    published_.state = SecureUdpClientWorkerState::Binding;
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

} // namespace godswar::network
