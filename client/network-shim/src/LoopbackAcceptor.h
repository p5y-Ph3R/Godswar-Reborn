#pragma once

#include "SocketHandle.h"

#include <Windows.h>

#include <cstdint>

namespace godswar::network {

enum class LoopbackAcceptFailure : std::uint8_t {
    None = 0,
    InvalidState = 1,
    WinSockInitialization = 2,
    ListenerCreate = 3,
    Bind = 4,
    Listen = 5,
    AddressQuery = 6,
    WorkerCreate = 7,
    Accept = 8,
    NonLoopbackPeer = 9,
    Deadline = 10,
    Cancelled = 11,
    JoinDeadline = 12,
};

class LoopbackAcceptor final {
public:
    LoopbackAcceptor() noexcept;
    ~LoopbackAcceptor() noexcept;

    LoopbackAcceptor(const LoopbackAcceptor&) = delete;
    LoopbackAcceptor& operator=(const LoopbackAcceptor&) = delete;

    bool Open() noexcept;
    bool BeginAccept() noexcept;
    bool Complete(
        DWORD timeoutMilliseconds,
        SocketHandle* acceptedSocket) noexcept;
    bool CancelAndJoin(DWORD timeoutMilliseconds) noexcept;

    std::uint16_t Port() const noexcept;
    LoopbackAcceptFailure Failure() const noexcept;
    bool HasLiveWorker() const noexcept;

private:
    enum class State : std::uint8_t {
        Initial = 0,
        Opening,
        Open,
        Accepting,
        Accepted,
        Completed,
        Cancelled,
        Failed,
    };

    static unsigned __stdcall AcceptThreadEntry(void* context) noexcept;
    unsigned AcceptWorker() noexcept;
    bool JoinWorkerUntil(
        ULONGLONG deadline,
        bool infiniteDeadline) noexcept;
    void RequestStop(LoopbackAcceptFailure failure) noexcept;
    void SetFailure(LoopbackAcceptFailure failure) noexcept;
    void CloseRuntimeHandles() noexcept;

    mutable SRWLOCK lock_;
    mutable SRWLOCK joinLock_;
    SocketHandle listener_;
    SocketHandle accepted_;
    HANDLE worker_ = nullptr;
    HANDLE completed_ = nullptr;
    HANDLE joinCompleted_ = nullptr;
    WSAEVENT socketReady_ = WSA_INVALID_EVENT;
    WSAEVENT stopRequested_ = WSA_INVALID_EVENT;
    std::uint16_t port_ = 0;
    LoopbackAcceptFailure failure_ = LoopbackAcceptFailure::None;
    State state_ = State::Initial;
    bool joinInProgress_ = false;
    bool workerJoined_ = false;
    volatile LONG completionOwner_ = 0;
};

} // namespace godswar::network
