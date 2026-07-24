#include "OpaqueDuplexPump.h"

#include <cstring>

namespace godswar::network {
namespace {

bool IsQueueTerminal(ChunkQueueResult result) noexcept {
    return
        result == ChunkQueueResult::Cancelled ||
        result == ChunkQueueResult::Completed;
}

} // namespace

OpaqueDuplexPump::OpaqueDuplexPump(
    IByteStream* first,
    IByteStream* second,
    const BoundedChunkQueueLimits& queueLimits,
    const OpaquePumpThreadHooks* threadHooks) noexcept
    : first_(first),
      second_(second),
      firstToSecond_(first, second, queueLimits),
      secondToFirst_(second, first, queueLimits),
      startGate_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
      startFinishedEvent_(
          CreateEventW(nullptr, TRUE, FALSE, nullptr)),
      joinedEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
      joinOwnerReleasedEvent_(
          CreateEventW(nullptr, FALSE, FALSE, nullptr)) {
    if (threadHooks == nullptr) {
        threadHooks_.create = DefaultCreateWorker;
        threadHooks_.close = DefaultCloseWorker;
    } else {
        threadHooks_ = *threadHooks;
    }

    valid_ =
        first_ != nullptr &&
        second_ != nullptr &&
        first_ != second_ &&
        firstToSecond_.queue.IsValid() &&
        secondToFirst_.queue.IsValid() &&
        startGate_ != nullptr &&
        startFinishedEvent_ != nullptr &&
        joinedEvent_ != nullptr &&
        joinOwnerReleasedEvent_ != nullptr &&
        threadHooks_.create != nullptr &&
        threadHooks_.close != nullptr;

    workerContexts_[0] = {this, &firstToSecond_, true};
    workerContexts_[1] = {this, &firstToSecond_, false};
    workerContexts_[2] = {this, &secondToFirst_, true};
    workerContexts_[3] = {this, &secondToFirst_, false};
}

OpaqueDuplexPump::~OpaqueDuplexPump() noexcept {
    if (!StopAndJoin(DefaultJoinTimeoutMilliseconds)) {
        // Continuing would release state still referenced by a worker, while
        // an infinite fallback would hang Origin during unload. Compliant
        // streams must unblock from Stop within the bounded join.
        RaiseFailFastException(nullptr, nullptr, 0);
    }

    CloseWorkerHandles();
    if (startGate_ != nullptr) {
        CloseHandle(startGate_);
        startGate_ = nullptr;
    }
    if (startFinishedEvent_ != nullptr) {
        CloseHandle(startFinishedEvent_);
        startFinishedEvent_ = nullptr;
    }
    if (joinedEvent_ != nullptr) {
        CloseHandle(joinedEvent_);
        joinedEvent_ = nullptr;
    }
    if (joinOwnerReleasedEvent_ != nullptr) {
        CloseHandle(joinOwnerReleasedEvent_);
        joinOwnerReleasedEvent_ = nullptr;
    }
}

bool OpaqueDuplexPump::IsValid() const noexcept {
    return valid_;
}

bool OpaqueDuplexPump::Start() noexcept {
    if (!valid_ ||
        InterlockedCompareExchange(&outcome_, 0, 0) != 0 ||
        InterlockedCompareExchange(
            &startState_,
            StartInProgress,
            StartNotAttempted) != StartNotAttempted) {
        return false;
    }

    bool createdAllWorkers = true;
    for (LONG index = 0; index < 4; ++index) {
        if (InterlockedCompareExchange(&outcome_, 0, 0) != 0) {
            createdAllWorkers = false;
            break;
        }

        const HANDLE worker = threadHooks_.create(
            WorkerEntry,
            &workerContexts_[index],
            threadHooks_.context);
        if (worker == nullptr) {
            createdAllWorkers = false;
            break;
        }

        workerHandles_[index] = worker;
        InterlockedExchange(&workerCount_, index + 1);
    }

    const bool gateOpened = SetEvent(startGate_) != FALSE;
    if (!createdAllWorkers ||
        !gateOpened ||
        InterlockedCompareExchange(&outcome_, 0, 0) != 0) {
        if (InterlockedCompareExchange(&outcome_, 0, 0) == 0) {
            FailOnce(OpaquePumpOutcome::StartFailure);
        }
        InterlockedExchange(&startState_, StartFailed);
        SetEvent(startFinishedEvent_);
        static_cast<void>(
            StopAndJoin(DefaultJoinTimeoutMilliseconds));
        return false;
    }

    InterlockedExchange(&startState_, StartSucceeded);
    if (!SetEvent(startFinishedEvent_)) {
        FailOnce(OpaquePumpOutcome::StartFailure);
        InterlockedExchange(&startState_, StartFailed);
        static_cast<void>(
            StopAndJoin(DefaultJoinTimeoutMilliseconds));
        return false;
    }

    return true;
}

bool OpaqueDuplexPump::StopAndJoin(
    DWORD timeoutMilliseconds) noexcept {
    const ULONGLONG deadline =
        timeoutMilliseconds == INFINITE
            ? 0
            : GetTickCount64() + timeoutMilliseconds;

    FailOnce(OpaquePumpOutcome::StopRequested);
    if (startGate_ != nullptr) {
        SetEvent(startGate_);
    }

    if (InterlockedCompareExchange(
            &startState_,
            0,
            0) == StartInProgress) {
        if (startFinishedEvent_ == nullptr) {
            return false;
        }

        const DWORD startWait = WaitForSingleObject(
            startFinishedEvent_,
            RemainingWait(deadline, timeoutMilliseconds));
        if (startWait != WAIT_OBJECT_0) {
            if (startWait == WAIT_TIMEOUT) {
                InterlockedExchange(&joinTimedOut_, 1);
            }
            return false;
        }
    }

    for (;;) {
        if (InterlockedCompareExchange(&joined_, 0, 0) != 0) {
            return true;
        }

        if (InterlockedCompareExchange(&joinOwner_, 1, 0) == 0) {
            const bool joined = JoinCreatedWorkers(
                RemainingWait(deadline, timeoutMilliseconds));
            if (joined) {
                CloseWorkerHandles();
                InterlockedExchange(&joined_, 1);
                InterlockedExchange(&joinTimedOut_, 0);
                SetEvent(joinedEvent_);
            } else {
                InterlockedExchange(&joinTimedOut_, 1);
            }
            InterlockedExchange(&joinOwner_, 0);
            SetEvent(joinOwnerReleasedEvent_);
            return joined;
        }

        const HANDLE stateEvents[] = {
            joinedEvent_,
            joinOwnerReleasedEvent_};
        const DWORD changed = WaitForMultipleObjects(
            2,
            stateEvents,
            FALSE,
            RemainingWait(deadline, timeoutMilliseconds));
        if (changed == WAIT_OBJECT_0) {
            return true;
        }
        if (changed == WAIT_OBJECT_0 + 1) {
            continue;
        }
        if (changed == WAIT_TIMEOUT) {
            InterlockedExchange(&joinTimedOut_, 1);
            return false;
        }
        return false;
    }
}

OpaqueDuplexPumpSnapshot OpaqueDuplexPump::Snapshot() const noexcept {
    OpaqueDuplexPumpSnapshot snapshot{};
    snapshot.outcome = static_cast<OpaquePumpOutcome>(
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&outcome_),
            0,
            0));
    snapshot.activeWorkers = InterlockedCompareExchange(
        const_cast<volatile LONG*>(&activeWorkers_),
        0,
        0);
    snapshot.started =
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&startState_),
            0,
            0) == StartSucceeded;
    snapshot.joined =
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&joined_),
            0,
            0) != 0;
    snapshot.joinTimedOut =
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&joinTimedOut_),
            0,
            0) != 0;
    snapshot.firstToSecond = firstToSecond_.queue.Snapshot();
    snapshot.secondToFirst = secondToFirst_.queue.Snapshot();
    return snapshot;
}

DWORD WINAPI OpaqueDuplexPump::WorkerEntry(void* contextValue) noexcept {
    auto* context = static_cast<WorkerContext*>(contextValue);
    if (context == nullptr ||
        context->owner == nullptr ||
        context->direction == nullptr) {
        return ERROR_INVALID_PARAMETER;
    }

    OpaqueDuplexPump* owner = context->owner;
    InterlockedIncrement(&owner->activeWorkers_);
    const DWORD gateWait = WaitForSingleObject(
        owner->startGate_,
        INFINITE);
    if (gateWait == WAIT_OBJECT_0 &&
        InterlockedCompareExchange(&owner->outcome_, 0, 0) == 0) {
        if (context->reader) {
            owner->RunReader(context->direction);
        } else {
            owner->RunWriter(context->direction);
        }
    }
    InterlockedDecrement(&owner->activeWorkers_);
    return ERROR_SUCCESS;
}

void OpaqueDuplexPump::RunReader(Direction* direction) noexcept {
    std::uint8_t buffer[ReadBufferBytes]{};
    while (InterlockedCompareExchange(&outcome_, 0, 0) == 0) {
        const ByteStreamIoResult read = direction->source->Read(
            buffer,
            sizeof(buffer));
        if (read.status == ByteStreamIoStatus::EndOfStream) {
            direction->queue.Complete();
            SecureZeroMemory(buffer, sizeof(buffer));
            return;
        }
        if (read.status != ByteStreamIoStatus::Success ||
            read.bytesTransferred == 0 ||
            read.bytesTransferred > sizeof(buffer)) {
            SecureZeroMemory(buffer, sizeof(buffer));
            FailOnce(OpaquePumpOutcome::ReadFailure);
            return;
        }

        const ChunkQueueResult admitted = direction->queue.Enqueue(
            buffer,
            read.bytesTransferred);
        SecureZeroMemory(buffer, read.bytesTransferred);
        if (admitted == ChunkQueueResult::Success) {
            continue;
        }
        if (admitted == ChunkQueueResult::TimedOut) {
            FailOnce(OpaquePumpOutcome::QueueAdmissionTimedOut);
            return;
        }
        if (IsQueueTerminal(admitted)) {
            return;
        }

        FailOnce(OpaquePumpOutcome::QueueFailure);
        return;
    }
    SecureZeroMemory(buffer, sizeof(buffer));
}

void OpaqueDuplexPump::RunWriter(Direction* direction) noexcept {
    std::uint8_t buffer[ReadBufferBytes]{};
    while (InterlockedCompareExchange(&outcome_, 0, 0) == 0) {
        std::size_t queuedBytes = 0;
        const ChunkQueueResult dequeued = direction->queue.Dequeue(
            buffer,
            sizeof(buffer),
            &queuedBytes);
        if (dequeued == ChunkQueueResult::Completed) {
            SecureZeroMemory(buffer, sizeof(buffer));
            FailOnce(OpaquePumpOutcome::EndOfStream);
            return;
        }
        if (dequeued == ChunkQueueResult::Cancelled) {
            SecureZeroMemory(buffer, sizeof(buffer));
            return;
        }
        if (dequeued != ChunkQueueResult::Success ||
            queuedBytes == 0 ||
            queuedBytes > sizeof(buffer)) {
            SecureZeroMemory(buffer, sizeof(buffer));
            FailOnce(OpaquePumpOutcome::QueueFailure);
            return;
        }

        std::size_t offset = 0;
        while (offset < queuedBytes) {
            // In Slice 5, lifecycle cancellation is the bound for a blocking
            // write. IByteStream::Stop must unblock this call; Slice 6 adds
            // secure-transport write deadlines.
            const ByteStreamIoResult written =
                direction->destination->Write(
                    buffer + offset,
                    queuedBytes - offset);
            if (written.status != ByteStreamIoStatus::Success ||
                written.bytesTransferred == 0 ||
                written.bytesTransferred > queuedBytes - offset) {
                SecureZeroMemory(buffer, queuedBytes);
                FailOnce(OpaquePumpOutcome::WriteFailure);
                return;
            }
            offset += written.bytesTransferred;
        }
        SecureZeroMemory(buffer, queuedBytes);
    }
    SecureZeroMemory(buffer, sizeof(buffer));
}

void OpaqueDuplexPump::FailOnce(OpaquePumpOutcome outcome) noexcept {
    const LONG desired = static_cast<LONG>(outcome);
    if (InterlockedCompareExchange(&outcome_, desired, 0) != 0) {
        return;
    }

    firstToSecond_.queue.Cancel();
    secondToFirst_.queue.Cancel();
    if (first_ != nullptr) {
        first_->Stop();
    }
    if (second_ != nullptr) {
        second_->Stop();
    }
}

bool OpaqueDuplexPump::JoinCreatedWorkers(
    DWORD timeoutMilliseconds) noexcept {
    const LONG workerCount =
        InterlockedCompareExchange(&workerCount_, 0, 0);
    if (workerCount == 0) {
        return true;
    }

    const DWORD wait = WaitForMultipleObjects(
        static_cast<DWORD>(workerCount),
        workerHandles_,
        TRUE,
        timeoutMilliseconds);
    return wait == WAIT_OBJECT_0;
}

void OpaqueDuplexPump::CloseWorkerHandles() noexcept {
    const LONG workerCount =
        InterlockedExchange(&workerCount_, 0);
    for (LONG index = 0; index < workerCount; ++index) {
        if (workerHandles_[index] != nullptr) {
            threadHooks_.close(
                workerHandles_[index],
                threadHooks_.context);
            workerHandles_[index] = nullptr;
        }
    }
}

HANDLE OpaqueDuplexPump::DefaultCreateWorker(
    LPTHREAD_START_ROUTINE entry,
    void* parameter,
    void*) noexcept {
    return CreateThread(
        nullptr,
        0,
        entry,
        parameter,
        0,
        nullptr);
}

void OpaqueDuplexPump::DefaultCloseWorker(
    HANDLE worker,
    void*) noexcept {
    static_cast<void>(CloseHandle(worker));
}

DWORD OpaqueDuplexPump::RemainingWait(
    ULONGLONG deadline,
    DWORD timeoutMilliseconds) noexcept {
    if (timeoutMilliseconds == INFINITE) {
        return INFINITE;
    }

    const ULONGLONG now = GetTickCount64();
    if (now >= deadline) {
        return 0;
    }

    const ULONGLONG remaining = deadline - now;
    return remaining >= static_cast<ULONGLONG>(INFINITE)
        ? INFINITE - 1
        : static_cast<DWORD>(remaining);
}

} // namespace godswar::network
