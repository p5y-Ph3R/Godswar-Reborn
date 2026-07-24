#pragma once

#include "BoundedChunkQueue.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

enum class ByteStreamIoStatus : std::uint8_t {
    Success = 0,
    EndOfStream,
    Failed,
};

struct ByteStreamIoResult final {
    ByteStreamIoStatus status = ByteStreamIoStatus::Failed;
    std::size_t bytesTransferred = 0;
};

// The caller owns the stream. Slice 5 does not impose a transport write
// deadline: Stop is the lifecycle bound and must be idempotent, thread-safe,
// and unblock any concurrent Read or Write before it returns. TLS/write
// deadlines belong to Slice 6's secure transport.
class IByteStream {
public:
    virtual ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept = 0;
    virtual ByteStreamIoResult Write(
        const void* source,
        std::size_t sourceBytes) noexcept = 0;
    virtual void Stop() noexcept = 0;

protected:
    ~IByteStream() = default;
};

enum class OpaquePumpOutcome : std::uint8_t {
    None = 0,
    StopRequested,
    EndOfStream,
    QueueAdmissionTimedOut,
    QueueFailure,
    ReadFailure,
    WriteFailure,
    StartFailure,
};

struct OpaqueDuplexPumpSnapshot final {
    OpaquePumpOutcome outcome = OpaquePumpOutcome::None;
    LONG activeWorkers = 0;
    bool started = false;
    bool joined = false;
    bool joinTimedOut = false;
    BoundedChunkQueueSnapshot firstToSecond{};
    BoundedChunkQueueSnapshot secondToFirst{};
};

using CreateOpaquePumpWorker = HANDLE (*)(
    LPTHREAD_START_ROUTINE entry,
    void* parameter,
    void* context) noexcept;
using CloseOpaquePumpWorker = void (*)(
    HANDLE worker,
    void* context) noexcept;

struct OpaquePumpThreadHooks final {
    CreateOpaquePumpWorker create = nullptr;
    CloseOpaquePumpWorker close = nullptr;
    void* context = nullptr;
};

// Copies opaque bytes in both directions without interpreting or transforming
// the legacy stream. Each direction has one reader, one writer, and its own
// item-and-byte bounded queue.
class OpaqueDuplexPump final {
public:
    static constexpr DWORD DefaultJoinTimeoutMilliseconds = 5000;
    static constexpr std::size_t ReadBufferBytes =
        BoundedChunkQueue::MaximumChunkBytes;

    OpaqueDuplexPump(
        IByteStream* first,
        IByteStream* second,
        const BoundedChunkQueueLimits& queueLimits =
            BoundedChunkQueueLimits{},
        const OpaquePumpThreadHooks* threadHooks = nullptr) noexcept;
    ~OpaqueDuplexPump() noexcept;

    OpaqueDuplexPump(const OpaqueDuplexPump&) = delete;
    OpaqueDuplexPump& operator=(const OpaqueDuplexPump&) = delete;

    bool IsValid() const noexcept;
    bool Start() noexcept;

    // Signals both streams and queues, then waits no longer than the supplied
    // deadline. A false result leaves worker handles intact so a later call can
    // retry the join. Correct IByteStream implementations unblock on Stop.
    bool StopAndJoin(
        DWORD timeoutMilliseconds =
            DefaultJoinTimeoutMilliseconds) noexcept;

    OpaqueDuplexPumpSnapshot Snapshot() const noexcept;

private:
    struct Direction final {
        Direction(
            IByteStream* sourceValue,
            IByteStream* destinationValue,
            const BoundedChunkQueueLimits& limits) noexcept
            : source(sourceValue),
              destination(destinationValue),
              queue(limits) {
        }

        IByteStream* source;
        IByteStream* destination;
        BoundedChunkQueue queue;
    };

    struct WorkerContext final {
        OpaqueDuplexPump* owner = nullptr;
        Direction* direction = nullptr;
        bool reader = false;
    };

    static DWORD WINAPI WorkerEntry(void* context) noexcept;
    void RunReader(Direction* direction) noexcept;
    void RunWriter(Direction* direction) noexcept;
    void FailOnce(OpaquePumpOutcome outcome) noexcept;
    bool JoinCreatedWorkers(DWORD timeoutMilliseconds) noexcept;
    void CloseWorkerHandles() noexcept;
    static HANDLE DefaultCreateWorker(
        LPTHREAD_START_ROUTINE entry,
        void* parameter,
        void* context) noexcept;
    static void DefaultCloseWorker(
        HANDLE worker,
        void* context) noexcept;
    static DWORD RemainingWait(
        ULONGLONG deadline,
        DWORD timeoutMilliseconds) noexcept;

    IByteStream* first_;
    IByteStream* second_;
    Direction firstToSecond_;
    Direction secondToFirst_;
    OpaquePumpThreadHooks threadHooks_{};
    HANDLE startGate_ = nullptr;
    HANDLE startFinishedEvent_ = nullptr;
    HANDLE joinedEvent_ = nullptr;
    HANDLE joinOwnerReleasedEvent_ = nullptr;
    HANDLE workerHandles_[4]{};
    WorkerContext workerContexts_[4]{};
    volatile LONG workerCount_ = 0;
    volatile LONG activeWorkers_ = 0;
    volatile LONG outcome_ = 0;
    volatile LONG startState_ = 0;
    volatile LONG joinOwner_ = 0;
    volatile LONG joined_ = 0;
    volatile LONG joinTimedOut_ = 0;
    bool valid_ = false;

    static constexpr LONG StartNotAttempted = 0;
    static constexpr LONG StartInProgress = 1;
    static constexpr LONG StartSucceeded = 2;
    static constexpr LONG StartFailed = 3;
};

} // namespace godswar::network
