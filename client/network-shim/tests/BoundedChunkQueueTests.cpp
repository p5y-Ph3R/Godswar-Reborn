#include "BoundedChunkQueueTests.h"

#include "../src/BoundedChunkQueue.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::BoundedChunkQueue;
using godswar::network::BoundedChunkQueueLimits;
using godswar::network::ChunkQueueMemoryHooks;
using godswar::network::ChunkQueueResult;

int Failures = 0;

void Check(bool condition, const char* message) noexcept {
    if (!condition) {
        std::printf("FAIL: %s\n", message);
        ++Failures;
    }
}

bool WaitForProducer(
    BoundedChunkQueue* queue,
    bool producer,
    DWORD timeoutMilliseconds = 1000) noexcept {
    const ULONGLONG deadline = GetTickCount64() + timeoutMilliseconds;
    do {
        const auto snapshot = queue->Snapshot();
        if ((producer && snapshot.producerWaiting) ||
            (!producer && snapshot.consumerWaiting)) {
            return true;
        }
        Sleep(1);
    } while (GetTickCount64() < deadline);

    return false;
}

struct MemoryProbe final {
    std::size_t allocations = 0;
    std::size_t releases = 0;
    std::size_t zeroedReleases = 0;
    bool failAllocation = false;
    HANDLE releaseEntered = nullptr;
    HANDLE continueRelease = nullptr;
};

void* ProbeAllocate(std::size_t bytes, void* context) noexcept {
    auto* probe = static_cast<MemoryProbe*>(context);
    ++probe->allocations;
    if (probe->failAllocation) {
        return nullptr;
    }
    return HeapAlloc(GetProcessHeap(), 0, bytes);
}

void ProbeRelease(
    void* memory,
    std::size_t bytes,
    void* context) noexcept {
    auto* probe = static_cast<MemoryProbe*>(context);
    ++probe->releases;

    bool zeroed = true;
    const auto* values = static_cast<const std::uint8_t*>(memory);
    for (std::size_t index = 0; index < bytes; ++index) {
        if (values[index] != 0) {
            zeroed = false;
            break;
        }
    }
    if (zeroed) {
        ++probe->zeroedReleases;
    }

    if (probe->releaseEntered != nullptr &&
        probe->continueRelease != nullptr) {
        SetEvent(probe->releaseEntered);
        static_cast<void>(
            WaitForSingleObject(probe->continueRelease, INFINITE));
    }

    static_cast<void>(HeapFree(GetProcessHeap(), 0, memory));
}

ChunkQueueMemoryHooks MakeMemoryHooks(MemoryProbe* probe) noexcept {
    ChunkQueueMemoryHooks hooks{};
    hooks.allocate = ProbeAllocate;
    hooks.release = ProbeRelease;
    hooks.context = probe;
    return hooks;
}

struct ProducerContext final {
    BoundedChunkQueue* queue = nullptr;
    const std::uint8_t* bytes = nullptr;
    std::size_t byteCount = 0;
    DWORD timeoutMilliseconds = 0;
    ChunkQueueResult result = ChunkQueueResult::InvalidArgument;
};

DWORD WINAPI ProducerThread(void* rawContext) noexcept {
    auto* context = static_cast<ProducerContext*>(rawContext);
    context->result = context->queue->EnqueueFor(
        context->bytes,
        context->byteCount,
        context->timeoutMilliseconds);
    return 0;
}

struct ConsumerContext final {
    BoundedChunkQueue* queue = nullptr;
    std::uint8_t bytes[BoundedChunkQueue::MaximumChunkBytes]{};
    std::size_t byteCount = 0;
    DWORD timeoutMilliseconds = INFINITE;
    ChunkQueueResult result = ChunkQueueResult::InvalidArgument;
};

DWORD WINAPI ConsumerThread(void* rawContext) noexcept {
    auto* context = static_cast<ConsumerContext*>(rawContext);
    context->result = context->queue->Dequeue(
        context->bytes,
        sizeof(context->bytes),
        &context->byteCount,
        context->timeoutMilliseconds);
    return 0;
}

struct DelayedSignalContext final {
    HANDLE event = nullptr;
    DWORD delayMilliseconds = 0;
};

DWORD WINAPI DelayedSignalThread(void* rawContext) noexcept {
    const auto* context =
        static_cast<const DelayedSignalContext*>(rawContext);
    Sleep(context->delayMilliseconds);
    SetEvent(context->event);
    return 0;
}

bool JoinAndClose(HANDLE thread) noexcept {
    if (thread == nullptr) {
        return false;
    }
    const bool joined =
        WaitForSingleObject(thread, 2000) == WAIT_OBJECT_0;
    static_cast<void>(CloseHandle(thread));
    return joined;
}

void TestConfigurationAndFifo() noexcept {
    BoundedChunkQueueLimits invalidLimits{};
    invalidLimits.itemCapacity = 0;
    BoundedChunkQueue invalid(invalidLimits);
    Check(!invalid.IsValid(), "zero item capacity was accepted");

    MemoryProbe probe{};
    const auto hooks = MakeMemoryHooks(&probe);
    BoundedChunkQueueLimits limits{};
    limits.itemCapacity = 2;
    limits.byteCapacity = 6;
    limits.maximumChunkBytes = 4;
    limits.producerAdmissionTimeoutMilliseconds = 20;
    BoundedChunkQueue queue(limits, &hooks);
    Check(queue.IsValid(), "valid small queue was rejected");

    const std::uint8_t first[] = {1, 2, 3};
    const std::uint8_t second[] = {4, 5};
    const std::uint8_t third[] = {6, 7, 8, 9};
    const std::uint8_t blocked[] = {10};
    Check(
        queue.Enqueue(first, sizeof(first)) == ChunkQueueResult::Success &&
            queue.Enqueue(second, sizeof(second)) ==
                ChunkQueueResult::Success,
        "initial FIFO chunks were not admitted");

    const auto full = queue.Snapshot();
    Check(
        full.itemCount == 2 &&
            full.byteCount == 5 &&
            full.highWaterItemCount == 2 &&
            full.highWaterByteCount == 5,
        "queue snapshot/high-water values are wrong");
    Check(
        queue.EnqueueFor(blocked, sizeof(blocked), 0) ==
            ChunkQueueResult::TimedOut,
        "item-capacity overflow did not time out");
    Check(
        probe.allocations == 2,
        "overflowing producer copied before capacity admission");

    std::uint8_t output[4]{};
    std::size_t outputBytes = 0;
    Check(
        queue.Dequeue(
            output,
            sizeof(output),
            &outputBytes,
            0) == ChunkQueueResult::Success &&
            outputBytes == sizeof(first) &&
            std::memcmp(output, first, sizeof(first)) == 0,
        "first FIFO chunk changed");
    Check(
        queue.Enqueue(third, sizeof(third)) ==
            ChunkQueueResult::Success,
        "released item/byte capacity was not reusable");

    outputBytes = 99;
    Check(
        queue.Dequeue(output, 1, &outputBytes, 0) ==
            ChunkQueueResult::DestinationTooSmall &&
            outputBytes == 0 &&
            queue.Snapshot().itemCount == 2,
        "small destination removed or changed the FIFO head");
    Check(
        queue.Dequeue(
            output,
            sizeof(output),
            &outputBytes,
            0) == ChunkQueueResult::Success &&
            outputBytes == sizeof(second) &&
            std::memcmp(output, second, sizeof(second)) == 0,
        "second FIFO chunk changed");
    Check(
        queue.Dequeue(
            output,
            sizeof(output),
            &outputBytes,
            0) == ChunkQueueResult::Success &&
            outputBytes == sizeof(third) &&
            std::memcmp(output, third, sizeof(third)) == 0,
        "third FIFO chunk changed");

    const auto drained = queue.Snapshot();
    Check(
        drained.itemCount == 0 &&
            drained.byteCount == 0 &&
            drained.highWaterItemCount == 2 &&
            drained.highWaterByteCount == 6,
        "drain reset or corrupted high-water values");
    Check(
        probe.releases == 3 && probe.zeroedReleases == 3,
        "dequeued queue-owned copies were not securely cleared");

    queue.Complete();
    queue.Complete();
    Check(
        queue.Enqueue(first, sizeof(first)) ==
            ChunkQueueResult::Completed,
        "completed queue accepted a producer");
    Check(
        queue.Dequeue(output, sizeof(output), &outputBytes, 0) ==
            ChunkQueueResult::Completed,
        "drained completed queue did not report completion");
    queue.Close();
    queue.Close();
    Check(
        queue.Snapshot().cancelled,
        "idempotent close did not leave terminal cancellation");
}

void TestIndependentBoundsAndTimeout() noexcept {
    BoundedChunkQueueLimits byteLimits{};
    byteLimits.itemCapacity = 3;
    byteLimits.byteCapacity = 5;
    byteLimits.maximumChunkBytes = 4;
    byteLimits.producerAdmissionTimeoutMilliseconds = 30;
    BoundedChunkQueue byteQueue(byteLimits);
    const std::uint8_t four[] = {1, 2, 3, 4};
    const std::uint8_t two[] = {5, 6};
    const std::uint8_t one[] = {7};
    Check(
        byteQueue.Enqueue(four, sizeof(four)) ==
            ChunkQueueResult::Success &&
            byteQueue.EnqueueFor(two, sizeof(two), 0) ==
                ChunkQueueResult::TimedOut &&
            byteQueue.Enqueue(one, sizeof(one)) ==
                ChunkQueueResult::Success,
        "byte capacity was not enforced independently");

    BoundedChunkQueueLimits itemLimits{};
    itemLimits.itemCapacity = 2;
    itemLimits.byteCapacity = 8;
    itemLimits.maximumChunkBytes = 4;
    itemLimits.producerAdmissionTimeoutMilliseconds = 40;
    BoundedChunkQueue itemQueue(itemLimits);
    Check(
        itemQueue.Enqueue(one, sizeof(one)) ==
            ChunkQueueResult::Success &&
            itemQueue.Enqueue(one, sizeof(one)) ==
                ChunkQueueResult::Success,
        "item-bound setup failed");

    const ULONGLONG started = GetTickCount64();
    const auto timeoutResult =
        itemQueue.Enqueue(one, sizeof(one));
    const ULONGLONG elapsed = GetTickCount64() - started;
    Check(
        timeoutResult == ChunkQueueResult::TimedOut &&
            elapsed >= 20 &&
            elapsed < 1000,
        "configured producer timeout was not bounded");

    const std::uint8_t oversized[] = {1, 2, 3, 4, 5};
    Check(
        itemQueue.EnqueueFor(
            oversized,
            sizeof(oversized),
            0) == ChunkQueueResult::ChunkTooLarge,
        "maximum chunk size was not enforced");
    byteQueue.Close();
    itemQueue.Close();
}

void TestProducerWakeAndWaiterBound() noexcept {
    BoundedChunkQueueLimits limits{};
    limits.itemCapacity = 1;
    limits.byteCapacity = 4;
    limits.maximumChunkBytes = 4;
    limits.producerAdmissionTimeoutMilliseconds = 1000;
    BoundedChunkQueue queue(limits);
    const std::uint8_t first[] = {1, 2};
    const std::uint8_t second[] = {3, 4, 5};
    Check(
        queue.Enqueue(first, sizeof(first)) ==
            ChunkQueueResult::Success,
        "producer-wake setup failed");

    ProducerContext producer{};
    producer.queue = &queue;
    producer.bytes = second;
    producer.byteCount = sizeof(second);
    producer.timeoutMilliseconds = 1000;
    HANDLE producerThread = CreateThread(
        nullptr,
        0,
        ProducerThread,
        &producer,
        0,
        nullptr);
    Check(producerThread != nullptr, "producer thread creation failed");
    Check(
        producerThread != nullptr && WaitForProducer(&queue, true),
        "producer did not enter bounded wait");
    Check(
        queue.EnqueueFor(second, sizeof(second), 10) ==
            ChunkQueueResult::ConcurrentWaiter,
        "second producer waiter was not rejected");

    std::uint8_t output[4]{};
    std::size_t outputBytes = 0;
    Check(
        queue.Dequeue(
            output,
            sizeof(output),
            &outputBytes,
            0) == ChunkQueueResult::Success &&
            std::memcmp(output, first, sizeof(first)) == 0,
        "consumer failed to release producer capacity");
    Check(
        JoinAndClose(producerThread),
        "admitted producer worker did not terminate");
    Check(
        producer.result == ChunkQueueResult::Success,
        "waiting producer did not wake after capacity release");
    Check(
        queue.Dequeue(
            output,
            sizeof(output),
            &outputBytes,
            0) == ChunkQueueResult::Success &&
            outputBytes == sizeof(second) &&
            std::memcmp(output, second, sizeof(second)) == 0,
        "woken producer chunk changed or reordered");
    queue.Close();
}

void TestConsumerTerminalWake() noexcept {
    BoundedChunkQueueLimits limits{};
    limits.itemCapacity = 1;
    limits.byteCapacity = 4;
    limits.maximumChunkBytes = 4;
    limits.producerAdmissionTimeoutMilliseconds = 20;

    BoundedChunkQueue cancelledQueue(limits);
    ConsumerContext cancelledConsumer{};
    cancelledConsumer.queue = &cancelledQueue;
    HANDLE cancelledThread = CreateThread(
        nullptr,
        0,
        ConsumerThread,
        &cancelledConsumer,
        0,
        nullptr);
    Check(cancelledThread != nullptr, "cancel consumer creation failed");
    Check(
        cancelledThread != nullptr &&
            WaitForProducer(&cancelledQueue, false),
        "consumer did not enter bounded wait");

    ConsumerContext secondConsumer{};
    secondConsumer.queue = &cancelledQueue;
    secondConsumer.timeoutMilliseconds = 10;
    secondConsumer.result = cancelledQueue.Dequeue(
        secondConsumer.bytes,
        sizeof(secondConsumer.bytes),
        &secondConsumer.byteCount,
        secondConsumer.timeoutMilliseconds);
    Check(
        secondConsumer.result == ChunkQueueResult::ConcurrentWaiter,
        "second consumer waiter was not rejected");

    cancelledQueue.Cancel();
    cancelledQueue.Cancel();
    Check(
        JoinAndClose(cancelledThread) &&
            cancelledConsumer.result == ChunkQueueResult::Cancelled,
        "cancel did not wake the consumer");

    BoundedChunkQueue completedQueue(limits);
    ConsumerContext completedConsumer{};
    completedConsumer.queue = &completedQueue;
    HANDLE completedThread = CreateThread(
        nullptr,
        0,
        ConsumerThread,
        &completedConsumer,
        0,
        nullptr);
    Check(completedThread != nullptr, "complete consumer creation failed");
    Check(
        completedThread != nullptr &&
            WaitForProducer(&completedQueue, false),
        "completion consumer did not wait");
    completedQueue.Complete();
    Check(
        JoinAndClose(completedThread) &&
            completedConsumer.result == ChunkQueueResult::Completed,
        "completion did not wake the consumer");
    completedQueue.Close();

    BoundedChunkQueue completedProducerQueue(limits);
    const std::uint8_t chunk[] = {1, 2, 3, 4};
    Check(
        completedProducerQueue.Enqueue(chunk, sizeof(chunk)) ==
            ChunkQueueResult::Success,
        "complete producer setup failed");
    ProducerContext completedProducer{};
    completedProducer.queue = &completedProducerQueue;
    completedProducer.bytes = chunk;
    completedProducer.byteCount = sizeof(chunk);
    completedProducer.timeoutMilliseconds = 1000;
    HANDLE completedProducerThread = CreateThread(
        nullptr,
        0,
        ProducerThread,
        &completedProducer,
        0,
        nullptr);
    Check(
        completedProducerThread != nullptr,
        "complete producer creation failed");
    Check(
        completedProducerThread != nullptr &&
            WaitForProducer(&completedProducerQueue, true),
        "completion producer did not wait");
    completedProducerQueue.Complete();
    Check(
        JoinAndClose(completedProducerThread) &&
            completedProducer.result == ChunkQueueResult::Completed,
        "completion did not wake the producer");
    completedProducerQueue.Close();
}

void TestCancelAndAllocationFailure() noexcept {
    MemoryProbe probe{};
    const auto hooks = MakeMemoryHooks(&probe);
    BoundedChunkQueueLimits limits{};
    limits.itemCapacity = 1;
    limits.byteCapacity = 4;
    limits.maximumChunkBytes = 4;
    limits.producerAdmissionTimeoutMilliseconds = 1000;
    BoundedChunkQueue queue(limits, &hooks);
    const std::uint8_t secret[] = {0xA5, 0x5A, 0xC3, 0x3C};
    Check(
        queue.Enqueue(secret, sizeof(secret)) ==
            ChunkQueueResult::Success,
        "cancel producer setup failed");

    ProducerContext producer{};
    producer.queue = &queue;
    producer.bytes = secret;
    producer.byteCount = sizeof(secret);
    producer.timeoutMilliseconds = 1000;
    HANDLE producerThread = CreateThread(
        nullptr,
        0,
        ProducerThread,
        &producer,
        0,
        nullptr);
    Check(producerThread != nullptr, "cancel producer creation failed");
    Check(
        producerThread != nullptr && WaitForProducer(&queue, true),
        "cancel producer did not wait");
    queue.Cancel();
    Check(
        JoinAndClose(producerThread) &&
            producer.result == ChunkQueueResult::Cancelled,
        "cancel did not wake the producer");
    Check(
        probe.allocations == 1 &&
            probe.releases == 1 &&
            probe.zeroedReleases == 1,
        "cancel copied a waiter or failed to zero the queued secret");

    MemoryProbe failingProbe{};
    failingProbe.failAllocation = true;
    const auto failingHooks = MakeMemoryHooks(&failingProbe);
    BoundedChunkQueue failingQueue(limits, &failingHooks);
    Check(
        failingQueue.Enqueue(secret, sizeof(secret)) ==
            ChunkQueueResult::OutOfMemory &&
            failingQueue.Snapshot().itemCount == 0,
        "allocation failure changed queue state");
    failingQueue.Close();

    MemoryProbe destructorProbe{};
    const auto destructorHooks = MakeMemoryHooks(&destructorProbe);
    {
        BoundedChunkQueue destructorQueue(limits, &destructorHooks);
        Check(
            destructorQueue.Enqueue(secret, sizeof(secret)) ==
                ChunkQueueResult::Success,
            "destructor cleanup setup failed");
    }
    Check(
        destructorProbe.releases == 1 &&
            destructorProbe.zeroedReleases == 1,
        "destructor did not securely release an admitted copy");
}

void TestAbsoluteAdmissionDeadline() noexcept {
    MemoryProbe probe{};
    probe.releaseEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    probe.continueRelease = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const auto hooks = MakeMemoryHooks(&probe);
    BoundedChunkQueueLimits limits{};
    limits.itemCapacity = 2;
    limits.byteCapacity = 8;
    limits.maximumChunkBytes = 4;
    limits.producerAdmissionTimeoutMilliseconds = 30;
    BoundedChunkQueue queue(limits, &hooks);
    const std::uint8_t bytes[] = {1, 2, 3, 4};
    Check(
        queue.Enqueue(bytes, sizeof(bytes)) ==
            ChunkQueueResult::Success,
        "deadline setup failed");
    ConsumerContext consumer{};
    consumer.queue = &queue;
    HANDLE consumerThread = CreateThread(
        nullptr,
        0,
        ConsumerThread,
        &consumer,
        0,
        nullptr);
    Check(
        consumerThread != nullptr &&
            WaitForSingleObject(probe.releaseEntered, 1000) ==
                WAIT_OBJECT_0,
        "consumer did not hold queue lock");
    DelayedSignalContext signal{probe.continueRelease, 80};
    HANDLE signalThread = CreateThread(
        nullptr,
        0,
        DelayedSignalThread,
        &signal,
        0,
        nullptr);
    const auto result = queue.EnqueueFor(bytes, sizeof(bytes), 30);
    Check(
        result == ChunkQueueResult::TimedOut &&
            probe.allocations == 1,
        "expired lock wait admitted a chunk");
    Check(
        JoinAndClose(signalThread) &&
            JoinAndClose(consumerThread) &&
            consumer.result == ChunkQueueResult::Success,
        "deadline workers did not terminate");
    queue.Close();
    CloseHandle(probe.releaseEntered);
    CloseHandle(probe.continueRelease);
}

} // namespace

int RunBoundedChunkQueueTests() {
    Failures = 0;
    TestConfigurationAndFifo();
    TestIndependentBoundsAndTimeout();
    TestProducerWakeAndWaiterBound();
    TestConsumerTerminalWake();
    TestCancelAndAllocationFailure();
    TestAbsoluteAdmissionDeadline();

    if (Failures == 0) {
        std::printf("Bounded chunk queue checks passed.\n");
    }
    return Failures;
}
