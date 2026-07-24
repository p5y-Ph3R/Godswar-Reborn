#include "NativeClientBridge.h"

#include <new>

namespace godswar::network {

NativeClientBridge::NativeClientBridge(
    const BoundedChunkQueueLimits& queueLimits) noexcept
    : queueLimits_(queueLimits),
      transitionEvent_(
          CreateEventW(nullptr, TRUE, TRUE, nullptr)) {
    InitializeSRWLock(&lock_);
}

NativeClientBridge::~NativeClientBridge() noexcept {
    if (!StopAndJoin(DefaultOperationDeadlineMilliseconds)) {
        // A compliant IByteStream::Stop unblocks all I/O. Continuing after a
        // missed final join would let workers retain this object, while an
        // unbounded wait would hang Origin during unload.
        RaiseFailFastException(nullptr, nullptr, 0);
    }

    if (transitionEvent_ != nullptr) {
        CloseHandle(transitionEvent_);
        transitionEvent_ = nullptr;
    }
}

bool NativeClientBridge::Start(
    ILegacyNetClient* legacyClient,
    IByteStream* outerStream,
    DWORD startupDeadlineMilliseconds) noexcept {
    if (legacyClient == nullptr ||
        outerStream == nullptr ||
        startupDeadlineMilliseconds == 0 ||
        startupDeadlineMilliseconds == INFINITE ||
        transitionEvent_ == nullptr) {
        AcquireSRWLockExclusive(&lock_);
        if (state_ != NativeBridgeState::Starting &&
            state_ != NativeBridgeState::Running &&
            state_ != NativeBridgeState::Stopping &&
            state_ != NativeBridgeState::JoinPending) {
            failure_ = NativeBridgeFailure::InvalidArgument;
        }
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }

    AcquireSRWLockExclusive(&lock_);
    if (state_ == NativeBridgeState::Starting ||
        state_ == NativeBridgeState::Running ||
        state_ == NativeBridgeState::Stopping ||
        state_ == NativeBridgeState::JoinPending ||
        pump_ != nullptr ||
        localStream_ != nullptr) {
        ReleaseSRWLockExclusive(&lock_);
        return false;
    }

    state_ = NativeBridgeState::Starting;
    failure_ = NativeBridgeFailure::None;
    acceptFailure_ = LoopbackAcceptFailure::None;
    stockDisconnectIssued_ = false;
    ResetEvent(transitionEvent_);
    ReleaseSRWLockExclusive(&lock_);

    const auto deadline =
        GetTickCount64() + startupDeadlineMilliseconds;
    LoopbackAcceptor acceptor;
    if (!acceptor.Open()) {
        RecordStartFailure(
            NativeBridgeFailure::ListenerOpen,
            acceptor.Failure(),
            legacyClient,
            outerStream,
            false);
        return false;
    }

    if (!acceptor.BeginAccept()) {
        RecordStartFailure(
            NativeBridgeFailure::AcceptStart,
            acceptor.Failure(),
            legacyClient,
            outerStream,
            false);
        return false;
    }

    legacyClient->SetHost("127.0.0.1", acceptor.Port());
    if (!legacyClient->Connect()) {
        static_cast<void>(acceptor.CancelAndJoin(
            RemainingWait(
                deadline,
                startupDeadlineMilliseconds)));
        RecordStartFailure(
            NativeBridgeFailure::StockConnect,
            acceptor.Failure(),
            legacyClient,
            outerStream,
            true);
        return false;
    }

    SocketHandle accepted;
    if (RemainingWait(
            deadline,
            startupDeadlineMilliseconds) == 0) {
        static_cast<void>(acceptor.CancelAndJoin(0));
        RecordStartFailure(
            NativeBridgeFailure::OperationDeadline,
            LoopbackAcceptFailure::Deadline,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    if (!acceptor.Complete(
            RemainingWait(
                deadline,
                startupDeadlineMilliseconds),
            &accepted)) {
        RecordStartFailure(
            NativeBridgeFailure::AcceptComplete,
            acceptor.Failure(),
            legacyClient,
            outerStream,
            true);
        return false;
    }

    if (RemainingWait(
            deadline,
            startupDeadlineMilliseconds) == 0) {
        RecordStartFailure(
            NativeBridgeFailure::OperationDeadline,
            LoopbackAcceptFailure::Deadline,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    auto* localStream = new (std::nothrow) WinSocketByteStream(
        static_cast<SocketHandle&&>(accepted));
    if (localStream == nullptr || !localStream->IsValid()) {
        delete localStream;
        RecordStartFailure(
            NativeBridgeFailure::LocalStreamAllocation,
            LoopbackAcceptFailure::None,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    if (RemainingWait(
            deadline,
            startupDeadlineMilliseconds) == 0) {
        localStream->Stop();
        delete localStream;
        RecordStartFailure(
            NativeBridgeFailure::OperationDeadline,
            LoopbackAcceptFailure::Deadline,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    auto* pump = new (std::nothrow) OpaqueDuplexPump(
        outerStream,
        localStream,
        queueLimits_);
    if (pump == nullptr || !pump->IsValid()) {
        delete pump;
        localStream->Stop();
        delete localStream;
        RecordStartFailure(
            NativeBridgeFailure::PumpAllocation,
            LoopbackAcceptFailure::None,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    if (RemainingWait(
            deadline,
            startupDeadlineMilliseconds) == 0) {
        delete pump;
        delete localStream;
        RecordStartFailure(
            NativeBridgeFailure::OperationDeadline,
            LoopbackAcceptFailure::Deadline,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    if (!pump->Start()) {
        delete pump;
        delete localStream;
        RecordStartFailure(
            NativeBridgeFailure::PumpStart,
            LoopbackAcceptFailure::None,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    if (RemainingWait(
            deadline,
            startupDeadlineMilliseconds) == 0) {
        delete pump;
        delete localStream;
        RecordStartFailure(
            NativeBridgeFailure::OperationDeadline,
            LoopbackAcceptFailure::Deadline,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    AcquireSRWLockExclusive(&lock_);
    if (RemainingWait(
            deadline,
            startupDeadlineMilliseconds) == 0) {
        ReleaseSRWLockExclusive(&lock_);
        delete pump;
        delete localStream;
        RecordStartFailure(
            NativeBridgeFailure::OperationDeadline,
            LoopbackAcceptFailure::Deadline,
            legacyClient,
            outerStream,
            true);
        return false;
    }

    outerStream_ = outerStream;
    localStream_ = localStream;
    pump_ = pump;
    state_ = NativeBridgeState::Running;
    SetEvent(transitionEvent_);
    ReleaseSRWLockExclusive(&lock_);
    return true;
}

bool NativeClientBridge::StopAndJoin(
    DWORD timeoutMilliseconds) noexcept {
    const auto deadline =
        timeoutMilliseconds == INFINITE
            ? 0ULL
            : GetTickCount64() + timeoutMilliseconds;

    for (;;) {
        AcquireSRWLockExclusive(&lock_);
        if (state_ == NativeBridgeState::Starting ||
            state_ == NativeBridgeState::Stopping) {
            const auto transitionEvent = transitionEvent_;
            ReleaseSRWLockExclusive(&lock_);
            if (transitionEvent == nullptr ||
                WaitForSingleObject(
                    transitionEvent,
                    RemainingWait(deadline, timeoutMilliseconds)) !=
                    WAIT_OBJECT_0) {
                return false;
            }
            continue;
        }

        if (pump_ == nullptr) {
            if (state_ != NativeBridgeState::Failed) {
                state_ = NativeBridgeState::Stopped;
            }
            ReleaseSRWLockExclusive(&lock_);
            return true;
        }

        auto* pump = pump_;
        state_ = NativeBridgeState::Stopping;
        ResetEvent(transitionEvent_);
        ReleaseSRWLockExclusive(&lock_);

        const bool joined = pump->StopAndJoin(
            RemainingWait(deadline, timeoutMilliseconds));

        AcquireSRWLockExclusive(&lock_);
        if (!joined) {
            state_ = NativeBridgeState::JoinPending;
            failure_ = NativeBridgeFailure::JoinDeadline;
            SetEvent(transitionEvent_);
            ReleaseSRWLockExclusive(&lock_);
            return false;
        }

        auto* localStream = localStream_;
        pump_ = nullptr;
        localStream_ = nullptr;
        outerStream_ = nullptr;
        state_ = NativeBridgeState::Stopped;
        SetEvent(transitionEvent_);
        ReleaseSRWLockExclusive(&lock_);

        delete pump;
        delete localStream;
        return true;
    }
}

NativeClientBridgeSnapshot NativeClientBridge::Snapshot() const noexcept {
    NativeClientBridgeSnapshot snapshot{};
    AcquireSRWLockShared(&lock_);
    snapshot.state = state_;
    snapshot.failure = failure_;
    snapshot.acceptFailure = acceptFailure_;
    snapshot.stockDisconnectIssued = stockDisconnectIssued_;
    snapshot.hasPump = pump_ != nullptr;
    if (pump_ != nullptr) {
        snapshot.pump = pump_->Snapshot();
        if (snapshot.state == NativeBridgeState::Running &&
            snapshot.pump.outcome != OpaquePumpOutcome::None) {
            snapshot.state = NativeBridgeState::JoinPending;
            snapshot.failure = NativeBridgeFailure::PumpTerminated;
        }
    }
    ReleaseSRWLockShared(&lock_);
    return snapshot;
}

void NativeClientBridge::RecordStartFailure(
    NativeBridgeFailure failure,
    LoopbackAcceptFailure acceptFailure,
    ILegacyNetClient* legacyClient,
    IByteStream* outerStream,
    bool stockConnectAttempted) noexcept {
    if (outerStream != nullptr) {
        outerStream->Stop();
    }
    if (stockConnectAttempted && legacyClient != nullptr) {
        legacyClient->DisConnect();
    }

    AcquireSRWLockExclusive(&lock_);
    state_ = NativeBridgeState::Failed;
    failure_ = failure;
    acceptFailure_ = acceptFailure;
    stockDisconnectIssued_ = stockConnectAttempted;
    SetEvent(transitionEvent_);
    ReleaseSRWLockExclusive(&lock_);
}

DWORD NativeClientBridge::RemainingWait(
    ULONGLONG deadline,
    DWORD timeoutMilliseconds) noexcept {
    if (timeoutMilliseconds == INFINITE) {
        return INFINITE;
    }

    const auto now = GetTickCount64();
    if (now >= deadline) {
        return 0;
    }

    const auto remaining = deadline - now;
    return remaining > MAXDWORD
        ? MAXDWORD
        : static_cast<DWORD>(remaining);
}

} // namespace godswar::network
