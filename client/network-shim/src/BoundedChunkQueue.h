#pragma once

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class ChunkQueueResult : std::uint8_t {
    Success = 0,
    Completed,
    Cancelled,
    TimedOut,
    InvalidArgument,
    InvalidConfiguration,
    ChunkTooLarge,
    DestinationTooSmall,
    OutOfMemory,
    ConcurrentWaiter,
    WaitFailed,
};

struct BoundedChunkQueueLimits final {
    std::size_t itemCapacity = 128;
    std::size_t byteCapacity = 512U * 1024U;
    std::size_t maximumChunkBytes = 16U * 1024U;
    DWORD producerAdmissionTimeoutMilliseconds = 250;
};

using AllocateChunkMemory =
    void* (*)(std::size_t bytes, void* context) noexcept;
using ReleaseChunkMemory =
    void (*)(void* memory, std::size_t bytes, void* context) noexcept;

struct ChunkQueueMemoryHooks final {
    AllocateChunkMemory allocate = nullptr;
    ReleaseChunkMemory release = nullptr;
    void* context = nullptr;
};

struct BoundedChunkQueueSnapshot final {
    std::size_t itemCapacity = 0;
    std::size_t byteCapacity = 0;
    std::size_t itemCount = 0;
    std::size_t byteCount = 0;
    std::size_t highWaterItemCount = 0;
    std::size_t highWaterByteCount = 0;
    bool producerWaiting = false;
    bool consumerWaiting = false;
    bool completed = false;
    bool cancelled = false;
    bool valid = false;
};

// A bounded one-producer/one-consumer FIFO for opaque network chunks.
//
// The queue owns every admitted copy until Dequeue copies it into the caller's
// fixed buffer. Concurrent producers or concurrent consumers are deliberately
// rejected instead of allocating an unbounded waiter list. Close or Cancel must
// be followed by joining external workers before the queue itself is destroyed.
class BoundedChunkQueue final {
public:
    static constexpr std::size_t MaximumItemCapacity = 128;
    static constexpr std::size_t MaximumByteCapacity = 512U * 1024U;
    static constexpr std::size_t MaximumChunkBytes = 16U * 1024U;

    explicit BoundedChunkQueue(
        const BoundedChunkQueueLimits& limits,
        const ChunkQueueMemoryHooks* memoryHooks = nullptr) noexcept;
    ~BoundedChunkQueue() noexcept;

    BoundedChunkQueue(const BoundedChunkQueue&) = delete;
    BoundedChunkQueue& operator=(const BoundedChunkQueue&) = delete;

    bool IsValid() const noexcept;

    ChunkQueueResult Enqueue(
        const void* bytes,
        std::size_t byteCount) noexcept;
    ChunkQueueResult EnqueueFor(
        const void* bytes,
        std::size_t byteCount,
        DWORD timeoutMilliseconds) noexcept;

    ChunkQueueResult Dequeue(
        void* destination,
        std::size_t destinationCapacity,
        std::size_t* bytesWritten,
        DWORD timeoutMilliseconds = INFINITE) noexcept;

    // Complete rejects producers but lets the consumer drain admitted chunks.
    void Complete() noexcept;

    // Cancel is terminal and securely discards all admitted chunks.
    void Cancel() noexcept;

    // Close is an idempotent cancellation alias for lifecycle cleanup.
    void Close() noexcept;

    BoundedChunkQueueSnapshot Snapshot() const noexcept;

private:
    struct Slot final {
        std::uint8_t* bytes = nullptr;
        std::size_t byteCount = 0;
    };

    bool HasCapacity(std::size_t byteCount) const noexcept;
    void ReleaseSlot(Slot* slot) noexcept;
    void ClearQueuedChunks() noexcept;

    static void* DefaultAllocate(
        std::size_t bytes,
        void* context) noexcept;
    static void DefaultRelease(
        void* memory,
        std::size_t bytes,
        void* context) noexcept;
    static DWORD RemainingWait(
        ULONGLONG deadline,
        DWORD timeoutMilliseconds) noexcept;

    BoundedChunkQueueLimits limits_{};
    ChunkQueueMemoryHooks memoryHooks_{};
    mutable SRWLOCK lock_ = SRWLOCK_INIT;
    CONDITION_VARIABLE notEmpty_ = CONDITION_VARIABLE_INIT;
    CONDITION_VARIABLE notFull_ = CONDITION_VARIABLE_INIT;
    Slot slots_[MaximumItemCapacity]{};
    std::size_t head_ = 0;
    std::size_t tail_ = 0;
    std::size_t itemCount_ = 0;
    std::size_t byteCount_ = 0;
    std::size_t highWaterItemCount_ = 0;
    std::size_t highWaterByteCount_ = 0;
    bool producerWaiting_ = false;
    bool consumerWaiting_ = false;
    bool completed_ = false;
    bool cancelled_ = false;
    bool valid_ = false;
    volatile LONG producerOperation_ = 0;
    volatile LONG consumerOperation_ = 0;
};

} // namespace godswar::network
