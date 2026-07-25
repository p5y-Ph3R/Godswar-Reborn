#include "LoopbackAcceptor.h"
#include "LoopbackPeerOwner.h"
#include "WinSockRuntime.h"

#include <WS2tcpip.h>
#include <process.h>

#include <algorithm>
#include <limits>

namespace {

constexpr DWORD DestructorJoinMilliseconds = 5'000;
constexpr unsigned MaximumForeignPeerRejections = 8;

struct AbsoluteDeadline final {
    ULONGLONG value = 0;
    bool infinite = false;
};

AbsoluteDeadline MakeDeadline(DWORD timeoutMilliseconds) noexcept {
    if (timeoutMilliseconds == INFINITE) {
        return AbsoluteDeadline{0, true};
    }

    const auto now = GetTickCount64();
    const auto maximum = std::numeric_limits<ULONGLONG>::max();
    const auto timeout = static_cast<ULONGLONG>(timeoutMilliseconds);
    return AbsoluteDeadline{
        timeout > maximum - now ? maximum : now + timeout,
        false,
    };
}

DWORD Remaining(const AbsoluteDeadline& deadline) noexcept {
    if (deadline.infinite) {
        return INFINITE;
    }

    const auto now = GetTickCount64();
    if (now >= deadline.value) {
        return 0;
    }

    const auto remaining = deadline.value - now;
    return static_cast<DWORD>(
        std::min<ULONGLONG>(
            remaining,
            static_cast<ULONGLONG>(INFINITE - 1)));
}

bool IsLoopbackPeer(const sockaddr_in& address) noexcept {
    return address.sin_family == AF_INET &&
        ntohl(address.sin_addr.s_addr) == INADDR_LOOPBACK;
}

class CompletionOwnership final {
public:
    explicit CompletionOwnership(volatile LONG* owner) noexcept
        : owner_(owner),
          acquired_(
              InterlockedCompareExchange(owner_, 1, 0) == 0) {
    }

    ~CompletionOwnership() noexcept {
        if (acquired_) {
            static_cast<void>(InterlockedExchange(owner_, 0));
        }
    }

    bool Acquired() const noexcept {
        return acquired_;
    }

    CompletionOwnership(const CompletionOwnership&) = delete;
    CompletionOwnership& operator=(const CompletionOwnership&) = delete;

private:
    volatile LONG* owner_;
    bool acquired_;
};

} // namespace

namespace godswar::network {

LoopbackAcceptor::LoopbackAcceptor() noexcept {
    InitializeSRWLock(&lock_);
    InitializeSRWLock(&joinLock_);
}

LoopbackAcceptor::~LoopbackAcceptor() noexcept {
    if (!CancelAndJoin(DestructorJoinMilliseconds)) {
        // The worker waits only on an explicit stop event and performs a
        // nonblocking accept. Continuing destruction would detach a thread
        // that still owns this object, while an infinite wait could hang
        // Origin. Treat failure to stop within the invariant deadline as
        // unrecoverable process corruption.
        RaiseFailFastException(nullptr, nullptr, 0);
    }

    CloseRuntimeHandles();
}

bool LoopbackAcceptor::Open() noexcept {
    AcquireSRWLockExclusive(&lock_);
    if (state_ != State::Initial) {
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }
    state_ = State::Opening;
    failure_ = LoopbackAcceptFailure::None;
    ReleaseSRWLockExclusive(&lock_);

    if (!EnsureWinSock()) {
        SetFailure(LoopbackAcceptFailure::WinSockInitialization);
        return false;
    }

    SocketHandle listener(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
    if (!listener.IsValid()) {
        SetFailure(LoopbackAcceptFailure::ListenerCreate);
        return false;
    }

    const BOOL exclusive = TRUE;
    if (setsockopt(
            listener.Get(),
            SOL_SOCKET,
            SO_EXCLUSIVEADDRUSE,
            reinterpret_cast<const char*>(&exclusive),
            sizeof(exclusive)) == SOCKET_ERROR) {
        SetFailure(LoopbackAcceptFailure::ListenerCreate);
        return false;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    if (bind(
            listener.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) == SOCKET_ERROR) {
        SetFailure(LoopbackAcceptFailure::Bind);
        return false;
    }

    if (listen(listener.Get(), 1) == SOCKET_ERROR) {
        SetFailure(LoopbackAcceptFailure::Listen);
        return false;
    }

    int addressBytes = sizeof(address);
    if (getsockname(
            listener.Get(),
            reinterpret_cast<sockaddr*>(&address),
            &addressBytes) == SOCKET_ERROR ||
        address.sin_family != AF_INET ||
        ntohl(address.sin_addr.s_addr) != INADDR_LOOPBACK ||
        address.sin_port == 0) {
        SetFailure(LoopbackAcceptFailure::AddressQuery);
        return false;
    }

    const auto socketReady = WSACreateEvent();
    if (socketReady == WSA_INVALID_EVENT) {
        SetFailure(LoopbackAcceptFailure::WorkerCreate);
        return false;
    }

    const auto stopRequested = WSACreateEvent();
    if (stopRequested == WSA_INVALID_EVENT) {
        static_cast<void>(WSACloseEvent(socketReady));
        SetFailure(LoopbackAcceptFailure::WorkerCreate);
        return false;
    }

    const auto completed =
        CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const auto joinCompleted =
        CreateEventW(nullptr, TRUE, TRUE, nullptr);
    if (completed == nullptr || joinCompleted == nullptr) {
        if (completed != nullptr) {
            CloseHandle(completed);
        }
        if (joinCompleted != nullptr) {
            CloseHandle(joinCompleted);
        }
        static_cast<void>(WSACloseEvent(stopRequested));
        static_cast<void>(WSACloseEvent(socketReady));
        SetFailure(LoopbackAcceptFailure::WorkerCreate);
        return false;
    }

    if (WSAEventSelect(
            listener.Get(),
            socketReady,
            FD_ACCEPT | FD_CLOSE) == SOCKET_ERROR) {
        CloseHandle(joinCompleted);
        CloseHandle(completed);
        static_cast<void>(WSACloseEvent(stopRequested));
        static_cast<void>(WSACloseEvent(socketReady));
        SetFailure(LoopbackAcceptFailure::ListenerCreate);
        return false;
    }

    AcquireSRWLockExclusive(&lock_);
    if (state_ != State::Opening) {
        ReleaseSRWLockExclusive(&lock_);
        CloseHandle(joinCompleted);
        CloseHandle(completed);
        static_cast<void>(WSACloseEvent(stopRequested));
        static_cast<void>(WSACloseEvent(socketReady));
        return false;
    }

    listener_ = static_cast<SocketHandle&&>(listener);
    completed_ = completed;
    joinCompleted_ = joinCompleted;
    socketReady_ = socketReady;
    stopRequested_ = stopRequested;
    port_ = ntohs(address.sin_port);
    state_ = State::Open;
    failure_ = LoopbackAcceptFailure::None;
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

bool LoopbackAcceptor::BeginAccept() noexcept {
    AcquireSRWLockExclusive(&lock_);
    if (state_ != State::Open ||
        !listener_.IsValid() ||
        completed_ == nullptr ||
        socketReady_ == WSA_INVALID_EVENT ||
        stopRequested_ == WSA_INVALID_EVENT ||
        worker_ != nullptr) {
        if (state_ == State::Initial) {
            failure_ = LoopbackAcceptFailure::InvalidState;
        }
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }

    state_ = State::Accepting;
    const auto workerValue = _beginthreadex(
        nullptr,
        0,
        AcceptThreadEntry,
        this,
        0,
        nullptr);
    if (workerValue == 0) {
        state_ = State::Failed;
        failure_ = LoopbackAcceptFailure::WorkerCreate;
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }

    worker_ = reinterpret_cast<HANDLE>(workerValue);
    workerJoined_ = false;
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

bool LoopbackAcceptor::Complete(
    DWORD timeoutMilliseconds,
    SocketHandle* acceptedSocket) noexcept {
    if (acceptedSocket == nullptr) {
        SetFailure(LoopbackAcceptFailure::InvalidState);
        return false;
    }

    CompletionOwnership completionOwner(&completionOwner_);
    if (!completionOwner.Acquired()) {
        return false;
    }

    const auto deadline = MakeDeadline(timeoutMilliseconds);
    AcquireSRWLockShared(&lock_);
    const auto completed = completed_;
    const auto state = state_;
    ReleaseSRWLockShared(&lock_);
    if ((state != State::Accepting && state != State::Accepted) ||
        completed == nullptr) {
        SetFailure(LoopbackAcceptFailure::InvalidState);
        return false;
    }

    const auto waitResult =
        WaitForSingleObject(completed, Remaining(deadline));
    if (waitResult != WAIT_OBJECT_0) {
        RequestStop(
            waitResult == WAIT_TIMEOUT
                ? LoopbackAcceptFailure::Deadline
                : LoopbackAcceptFailure::Accept);
        static_cast<void>(
            JoinWorkerUntil(deadline.value, deadline.infinite));
        return false;
    }

    if (!JoinWorkerUntil(deadline.value, deadline.infinite)) {
        RequestStop(LoopbackAcceptFailure::JoinDeadline);
        return false;
    }

    AcquireSRWLockExclusive(&lock_);
    listener_.Reset();
    const auto succeeded =
        state_ == State::Accepted &&
        failure_ == LoopbackAcceptFailure::None &&
        accepted_.IsValid();
    if (succeeded) {
        *acceptedSocket = static_cast<SocketHandle&&>(accepted_);
        state_ = State::Completed;
    }
    ReleaseSRWLockExclusive(&lock_);
    return succeeded;
}

bool LoopbackAcceptor::CancelAndJoin(
    DWORD timeoutMilliseconds) noexcept {
    const auto deadline = MakeDeadline(timeoutMilliseconds);
    RequestStop(LoopbackAcceptFailure::Cancelled);
    const auto joined =
        JoinWorkerUntil(deadline.value, deadline.infinite);

    if (joined) {
        AcquireSRWLockExclusive(&lock_);
        listener_.Reset();
        accepted_.Shutdown();
        accepted_.Reset();
        ReleaseSRWLockExclusive(&lock_);
    }

    return joined;
}

std::uint16_t LoopbackAcceptor::Port() const noexcept {
    AcquireSRWLockShared(&lock_);
    const auto result = port_;
    ReleaseSRWLockShared(&lock_);
    return result;
}

LoopbackAcceptFailure LoopbackAcceptor::Failure() const noexcept {
    AcquireSRWLockShared(&lock_);
    const auto result = failure_;
    ReleaseSRWLockShared(&lock_);
    return result;
}

bool LoopbackAcceptor::HasLiveWorker() const noexcept {
    AcquireSRWLockShared(&lock_);
    const auto worker = worker_;
    ReleaseSRWLockShared(&lock_);
    return worker != nullptr &&
        WaitForSingleObject(worker, 0) == WAIT_TIMEOUT;
}

unsigned __stdcall LoopbackAcceptor::AcceptThreadEntry(
    void* context) noexcept {
    auto* acceptor = static_cast<LoopbackAcceptor*>(context);
    return acceptor == nullptr ? 1U : acceptor->AcceptWorker();
}

unsigned LoopbackAcceptor::AcceptWorker() noexcept {
    AcquireSRWLockShared(&lock_);
    const auto listener = listener_.Get();
    const auto socketReady = socketReady_;
    const auto stopRequested = stopRequested_;
    ReleaseSRWLockShared(&lock_);

    WSAEVENT events[] = {
        stopRequested,
        socketReady,
    };

    SocketHandle accepted;
    sockaddr_in peer{};
    LoopbackAcceptFailure terminalFailure =
        LoopbackAcceptFailure::None;
    unsigned foreignPeerRejections = 0;

    for (;;) {
        const auto waitResult = WSAWaitForMultipleEvents(
            static_cast<DWORD>(sizeof(events) / sizeof(events[0])),
            events,
            FALSE,
            WSA_INFINITE,
            FALSE);
        if (waitResult == WSA_WAIT_EVENT_0) {
            terminalFailure = LoopbackAcceptFailure::Cancelled;
            break;
        }
        if (waitResult != WSA_WAIT_EVENT_0 + 1) {
            terminalFailure = LoopbackAcceptFailure::Accept;
            break;
        }

        WSANETWORKEVENTS networkEvents{};
        if (WSAEnumNetworkEvents(
                listener,
                socketReady,
                &networkEvents) == SOCKET_ERROR) {
            terminalFailure = LoopbackAcceptFailure::Accept;
            break;
        }

        if ((networkEvents.lNetworkEvents & FD_ACCEPT) != 0) {
            if (networkEvents.iErrorCode[FD_ACCEPT_BIT] != 0) {
                terminalFailure = LoopbackAcceptFailure::Accept;
                break;
            }

            int peerBytes = sizeof(peer);
            accepted.Reset(accept(
                listener,
                reinterpret_cast<sockaddr*>(&peer),
                &peerBytes));
            if (accepted.IsValid()) {
                u_long nonblocking = 0;
                if (WSAEventSelect(
                        accepted.Get(),
                        WSA_INVALID_EVENT,
                        0) == SOCKET_ERROR ||
                    ioctlsocket(
                        accepted.Get(),
                        FIONBIO,
                        &nonblocking) == SOCKET_ERROR) {
                    accepted.Reset();
                    terminalFailure = LoopbackAcceptFailure::Accept;
                    break;
                }
                if (!IsLoopbackPeer(peer)) {
                    accepted.Reset();
                    terminalFailure =
                        LoopbackAcceptFailure::NonLoopbackPeer;
                    break;
                }
                if (!IsAcceptedLoopbackPeerOwnedByCurrentProcess(
                        accepted.Get())) {
                    accepted.Reset();
                    ++foreignPeerRejections;
                    if (foreignPeerRejections >=
                        MaximumForeignPeerRejections) {
                        terminalFailure =
                            LoopbackAcceptFailure::ForeignProcessPeer;
                        break;
                    }
                    continue;
                }
                break;
            }
            if (WSAGetLastError() != WSAEWOULDBLOCK) {
                terminalFailure = LoopbackAcceptFailure::Accept;
                break;
            }
        }

        if ((networkEvents.lNetworkEvents & FD_CLOSE) != 0) {
            terminalFailure = LoopbackAcceptFailure::Accept;
            break;
        }
    }

    AcquireSRWLockExclusive(&lock_);
    if (state_ == State::Cancelled) {
        // The owner already selected the terminal reason.
    } else if (accepted.IsValid()) {
        accepted_ = static_cast<SocketHandle&&>(accepted);
        state_ = State::Accepted;
        failure_ = LoopbackAcceptFailure::None;
    } else {
        failure_ = terminalFailure;
        state_ = State::Failed;
    }

    if (completed_ != nullptr) {
        static_cast<void>(SetEvent(completed_));
    }
    ReleaseSRWLockExclusive(&lock_);
    return 0;
}

bool LoopbackAcceptor::JoinWorkerUntil(
    ULONGLONG deadline,
    bool infiniteDeadline) noexcept {
    const AbsoluteDeadline absolute{deadline, infiniteDeadline};

    for (;;) {
        HANDLE worker = nullptr;
        HANDLE joinCompleted = nullptr;
        bool ownsJoin = false;

        AcquireSRWLockExclusive(&joinLock_);
        AcquireSRWLockShared(&lock_);
        worker = worker_;
        joinCompleted = joinCompleted_;
        const auto alreadyJoined = workerJoined_;
        ReleaseSRWLockShared(&lock_);

        if (worker == nullptr || alreadyJoined) {
            ReleaseSRWLockExclusive(&joinLock_);
            return true;
        }

        if (!joinInProgress_) {
            joinInProgress_ = true;
            ownsJoin = true;
            if (joinCompleted != nullptr) {
                static_cast<void>(ResetEvent(joinCompleted));
            }
        }
        ReleaseSRWLockExclusive(&joinLock_);

        if (!ownsJoin) {
            if (joinCompleted == nullptr ||
                WaitForSingleObject(
                    joinCompleted,
                    Remaining(absolute)) != WAIT_OBJECT_0) {
                return false;
            }
            continue;
        }

        const auto waitResult =
            WaitForSingleObject(worker, Remaining(absolute));

        AcquireSRWLockExclusive(&joinLock_);
        if (waitResult == WAIT_OBJECT_0) {
            AcquireSRWLockExclusive(&lock_);
            workerJoined_ = true;
            ReleaseSRWLockExclusive(&lock_);
        }
        joinInProgress_ = false;
        if (joinCompleted != nullptr) {
            static_cast<void>(SetEvent(joinCompleted));
        }
        ReleaseSRWLockExclusive(&joinLock_);
        return waitResult == WAIT_OBJECT_0;
    }
}

void LoopbackAcceptor::RequestStop(
    LoopbackAcceptFailure failure) noexcept {
    AcquireSRWLockExclusive(&lock_);
    if (state_ != State::Completed) {
        state_ = State::Cancelled;
        if (failure_ == LoopbackAcceptFailure::None ||
            failure_ == LoopbackAcceptFailure::InvalidState) {
            failure_ = failure;
        }
    }
    if (stopRequested_ != WSA_INVALID_EVENT) {
        static_cast<void>(WSASetEvent(stopRequested_));
    }
    if (completed_ != nullptr) {
        static_cast<void>(SetEvent(completed_));
    }
    ReleaseSRWLockExclusive(&lock_);
}

void LoopbackAcceptor::SetFailure(
    LoopbackAcceptFailure failure) noexcept {
    AcquireSRWLockExclusive(&lock_);
    if (state_ == State::Cancelled ||
        state_ == State::Completed) {
        ReleaseSRWLockExclusive(&lock_);
        return;
    }
    if (state_ == State::Opening) {
        state_ = State::Failed;
    }
    failure_ = failure;
    ReleaseSRWLockExclusive(&lock_);
}

void LoopbackAcceptor::CloseRuntimeHandles() noexcept {
    AcquireSRWLockExclusive(&joinLock_);
    AcquireSRWLockExclusive(&lock_);
    listener_.Reset();
    accepted_.Reset();
    if (worker_ != nullptr) {
        CloseHandle(worker_);
        worker_ = nullptr;
    }
    if (completed_ != nullptr) {
        CloseHandle(completed_);
        completed_ = nullptr;
    }
    if (joinCompleted_ != nullptr) {
        CloseHandle(joinCompleted_);
        joinCompleted_ = nullptr;
    }
    if (socketReady_ != WSA_INVALID_EVENT) {
        static_cast<void>(WSACloseEvent(socketReady_));
        socketReady_ = WSA_INVALID_EVENT;
    }
    if (stopRequested_ != WSA_INVALID_EVENT) {
        static_cast<void>(WSACloseEvent(stopRequested_));
        stopRequested_ = WSA_INVALID_EVENT;
    }
    ReleaseSRWLockExclusive(&lock_);
    ReleaseSRWLockExclusive(&joinLock_);
}

} // namespace godswar::network
