#include "BoundedChunkQueue.h"

#include <cstring>

namespace godswar::network {
namespace {

class OperationGate final {
public:
    explicit OperationGate(volatile LONG* state) noexcept
        : state_(state),
          entered_(
              InterlockedCompareExchange(state_, 1, 0) == 0) {
    }

    ~OperationGate() noexcept {
        if (entered_) {
            InterlockedExchange(state_, 0);
        }
    }

    OperationGate(const OperationGate&) = delete;
    OperationGate& operator=(const OperationGate&) = delete;

    bool Entered() const noexcept {
        return entered_;
    }

private:
    volatile LONG* state_;
    bool entered_;
};

} // namespace

BoundedChunkQueue::BoundedChunkQueue(
    const BoundedChunkQueueLimits& limits,
    const ChunkQueueMemoryHooks* memoryHooks) noexcept
    : limits_(limits) {
    if (memoryHooks == nullptr) {
        memoryHooks_.allocate = DefaultAllocate;
        memoryHooks_.release = DefaultRelease;
    } else {
        memoryHooks_ = *memoryHooks;
    }

    valid_ =
        limits_.itemCapacity > 0 &&
        limits_.itemCapacity <= MaximumItemCapacity &&
        limits_.byteCapacity > 0 &&
        limits_.byteCapacity <= MaximumByteCapacity &&
        limits_.maximumChunkBytes > 0 &&
        limits_.maximumChunkBytes <= MaximumChunkBytes &&
        limits_.maximumChunkBytes <= limits_.byteCapacity &&
        limits_.producerAdmissionTimeoutMilliseconds != INFINITE &&
        memoryHooks_.allocate != nullptr &&
        memoryHooks_.release != nullptr;
}

BoundedChunkQueue::~BoundedChunkQueue() noexcept {
    Close();
}

bool BoundedChunkQueue::IsValid() const noexcept {
    AcquireSRWLockShared(&lock_);
    const bool result = valid_;
    ReleaseSRWLockShared(&lock_);
    return result;
}

ChunkQueueResult BoundedChunkQueue::Enqueue(
    const void* bytes,
    std::size_t byteCount) noexcept {
    return EnqueueFor(
        bytes,
        byteCount,
        limits_.producerAdmissionTimeoutMilliseconds);
}

ChunkQueueResult BoundedChunkQueue::EnqueueFor(
    const void* bytes,
    std::size_t byteCount,
    DWORD timeoutMilliseconds) noexcept {
    if (bytes == nullptr || byteCount == 0) {
        return ChunkQueueResult::InvalidArgument;
    }

    if (byteCount > limits_.maximumChunkBytes) {
        return ChunkQueueResult::ChunkTooLarge;
    }

    OperationGate operation(&producerOperation_);
    if (!operation.Entered()) {
        return ChunkQueueResult::ConcurrentWaiter;
    }

    const ULONGLONG deadline =
        timeoutMilliseconds == INFINITE
            ? 0
            : GetTickCount64() + timeoutMilliseconds;

    AcquireSRWLockExclusive(&lock_);
    if (!valid_) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::InvalidConfiguration;
    }

    if (timeoutMilliseconds != 0 &&
        timeoutMilliseconds != INFINITE &&
        RemainingWait(deadline, timeoutMilliseconds) == 0) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::TimedOut;
    }

    while (!HasCapacity(byteCount)) {
        if (cancelled_) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::Cancelled;
        }
        if (completed_) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::Completed;
        }
        if (timeoutMilliseconds == 0) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::TimedOut;
        }
        const DWORD remaining =
            RemainingWait(deadline, timeoutMilliseconds);
        if (remaining == 0) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::TimedOut;
        }

        producerWaiting_ = true;
        const BOOL woke = SleepConditionVariableSRW(
            &notFull_,
            &lock_,
            remaining,
            0);
        const DWORD waitError = woke ? ERROR_SUCCESS : GetLastError();
        producerWaiting_ = false;

        if (!woke && waitError == ERROR_TIMEOUT) {
            const ChunkQueueResult result =
                cancelled_
                    ? ChunkQueueResult::Cancelled
                    : completed_
                        ? ChunkQueueResult::Completed
                        : ChunkQueueResult::TimedOut;
            ReleaseSRWLockExclusive(&lock_);
            return result;
        }
        if (!woke) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::WaitFailed;
        }
    }

    if (cancelled_) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::Cancelled;
    }
    if (completed_) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::Completed;
    }
    if (timeoutMilliseconds != 0 &&
        timeoutMilliseconds != INFINITE &&
        RemainingWait(deadline, timeoutMilliseconds) == 0) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::TimedOut;
    }

    auto* copy = static_cast<std::uint8_t*>(
        memoryHooks_.allocate(byteCount, memoryHooks_.context));
    if (copy == nullptr) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::OutOfMemory;
    }

    std::memcpy(copy, bytes, byteCount);
    slots_[tail_].bytes = copy;
    slots_[tail_].byteCount = byteCount;
    tail_ = (tail_ + 1) % limits_.itemCapacity;
    ++itemCount_;
    byteCount_ += byteCount;
    if (itemCount_ > highWaterItemCount_) {
        highWaterItemCount_ = itemCount_;
    }
    if (byteCount_ > highWaterByteCount_) {
        highWaterByteCount_ = byteCount_;
    }

    ReleaseSRWLockExclusive(&lock_);
    WakeConditionVariable(&notEmpty_);
    return ChunkQueueResult::Success;
}

ChunkQueueResult BoundedChunkQueue::Dequeue(
    void* destination,
    std::size_t destinationCapacity,
    std::size_t* bytesWritten,
    DWORD timeoutMilliseconds) noexcept {
    if (destination == nullptr || bytesWritten == nullptr) {
        return ChunkQueueResult::InvalidArgument;
    }
    *bytesWritten = 0;

    OperationGate operation(&consumerOperation_);
    if (!operation.Entered()) {
        return ChunkQueueResult::ConcurrentWaiter;
    }

    const ULONGLONG deadline =
        timeoutMilliseconds == INFINITE
            ? 0
            : GetTickCount64() + timeoutMilliseconds;

    AcquireSRWLockExclusive(&lock_);
    if (!valid_) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::InvalidConfiguration;
    }

    while (itemCount_ == 0) {
        if (cancelled_) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::Cancelled;
        }
        if (completed_) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::Completed;
        }
        if (timeoutMilliseconds == 0) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::TimedOut;
        }
        const DWORD remaining =
            RemainingWait(deadline, timeoutMilliseconds);
        if (remaining == 0) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::TimedOut;
        }

        consumerWaiting_ = true;
        const BOOL woke = SleepConditionVariableSRW(
            &notEmpty_,
            &lock_,
            remaining,
            0);
        const DWORD waitError = woke ? ERROR_SUCCESS : GetLastError();
        consumerWaiting_ = false;

        if (!woke && waitError == ERROR_TIMEOUT) {
            const ChunkQueueResult result =
                cancelled_
                    ? ChunkQueueResult::Cancelled
                    : completed_
                        ? ChunkQueueResult::Completed
                        : ChunkQueueResult::TimedOut;
            ReleaseSRWLockExclusive(&lock_);
            return result;
        }
        if (!woke) {
            ReleaseSRWLockExclusive(&lock_);
            return ChunkQueueResult::WaitFailed;
        }
    }

    if (cancelled_) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::Cancelled;
    }

    Slot* slot = &slots_[head_];
    if (destinationCapacity < slot->byteCount) {
        ReleaseSRWLockExclusive(&lock_);
        return ChunkQueueResult::DestinationTooSmall;
    }

    const std::size_t copiedBytes = slot->byteCount;
    std::memcpy(destination, slot->bytes, copiedBytes);
    byteCount_ -= copiedBytes;
    --itemCount_;
    head_ = (head_ + 1) % limits_.itemCapacity;
    ReleaseSlot(slot);
    *bytesWritten = copiedBytes;

    ReleaseSRWLockExclusive(&lock_);
    WakeConditionVariable(&notFull_);
    return ChunkQueueResult::Success;
}

void BoundedChunkQueue::Complete() noexcept {
    AcquireSRWLockExclusive(&lock_);
    if (!completed_) {
        completed_ = true;
    }
    ReleaseSRWLockExclusive(&lock_);
    WakeAllConditionVariable(&notEmpty_);
    WakeAllConditionVariable(&notFull_);
}

void BoundedChunkQueue::Cancel() noexcept {
    AcquireSRWLockExclusive(&lock_);
    if (!cancelled_) {
        cancelled_ = true;
        completed_ = true;
        ClearQueuedChunks();
    }
    ReleaseSRWLockExclusive(&lock_);
    WakeAllConditionVariable(&notEmpty_);
    WakeAllConditionVariable(&notFull_);
}

void BoundedChunkQueue::Close() noexcept {
    Cancel();
}

BoundedChunkQueueSnapshot BoundedChunkQueue::Snapshot() const noexcept {
    AcquireSRWLockShared(&lock_);
    BoundedChunkQueueSnapshot snapshot{};
    snapshot.itemCapacity = limits_.itemCapacity;
    snapshot.byteCapacity = limits_.byteCapacity;
    snapshot.itemCount = itemCount_;
    snapshot.byteCount = byteCount_;
    snapshot.highWaterItemCount = highWaterItemCount_;
    snapshot.highWaterByteCount = highWaterByteCount_;
    snapshot.producerWaiting = producerWaiting_;
    snapshot.consumerWaiting = consumerWaiting_;
    snapshot.completed = completed_;
    snapshot.cancelled = cancelled_;
    snapshot.valid = valid_;
    ReleaseSRWLockShared(&lock_);
    return snapshot;
}

bool BoundedChunkQueue::HasCapacity(std::size_t bytes) const noexcept {
    return
        itemCount_ < limits_.itemCapacity &&
        bytes <= limits_.byteCapacity - byteCount_;
}

void BoundedChunkQueue::ReleaseSlot(Slot* slot) noexcept {
    if (slot->bytes != nullptr) {
        SecureZeroMemory(slot->bytes, slot->byteCount);
        memoryHooks_.release(
            slot->bytes,
            slot->byteCount,
            memoryHooks_.context);
    }
    slot->bytes = nullptr;
    slot->byteCount = 0;
}

void BoundedChunkQueue::ClearQueuedChunks() noexcept {
    while (itemCount_ > 0) {
        Slot* slot = &slots_[head_];
        byteCount_ -= slot->byteCount;
        --itemCount_;
        head_ = (head_ + 1) % limits_.itemCapacity;
        ReleaseSlot(slot);
    }
    tail_ = head_;
}

void* BoundedChunkQueue::DefaultAllocate(
    std::size_t bytes,
    void*) noexcept {
    return HeapAlloc(GetProcessHeap(), 0, bytes);
}

void BoundedChunkQueue::DefaultRelease(
    void* memory,
    std::size_t,
    void*) noexcept {
    static_cast<void>(HeapFree(GetProcessHeap(), 0, memory));
}

DWORD BoundedChunkQueue::RemainingWait(
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
