#include "OpaqueDuplexPumpTests.h"
#include "OpaqueDuplexPumpLifecycleTests.h"

#include "../src/OpaqueDuplexPump.h"

#include <Windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <vector>

namespace {

using godswar::network::BoundedChunkQueueLimits;
using godswar::network::ByteStreamIoResult;
using godswar::network::ByteStreamIoStatus;
using godswar::network::IByteStream;
using godswar::network::OpaqueDuplexPump;
using godswar::network::OpaquePumpOutcome;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

std::vector<std::uint8_t> MakePattern(
    std::size_t byteCount,
    std::uint8_t seed) {
    std::vector<std::uint8_t> bytes(byteCount);
    for (std::size_t index = 0; index < bytes.size(); ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            seed + index * 37U + index / 251U);
    }
    return bytes;
}

class ScriptedByteStream final : public IByteStream {
public:
    explicit ScriptedByteStream(
        std::vector<std::uint8_t> inbound = {},
        std::vector<std::size_t> readSlices = {},
        std::size_t maximumWriteBytes =
            (std::numeric_limits<std::size_t>::max)()) noexcept
        : inbound_(static_cast<std::vector<std::uint8_t>&&>(inbound)),
          readSlices_(
              static_cast<std::vector<std::size_t>&&>(readSlices)),
          written_(MaximumCapturedBytes),
          maximumWriteBytes_(maximumWriteBytes),
          stopEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
          writeGate_(CreateEventW(nullptr, TRUE, TRUE, nullptr)),
          progressEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
    }

    ~ScriptedByteStream() {
        if (stopEvent_ != nullptr) {
            CloseHandle(stopEvent_);
        }
        if (writeGate_ != nullptr) {
            CloseHandle(writeGate_);
        }
        if (progressEvent_ != nullptr) {
            CloseHandle(progressEvent_);
        }
    }

    ScriptedByteStream(const ScriptedByteStream&) = delete;
    ScriptedByteStream& operator=(const ScriptedByteStream&) = delete;

    ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept override {
        if (destination == nullptr || destinationCapacity == 0) {
            return {ByteStreamIoStatus::Failed, 0};
        }

        AcquireSRWLockExclusive(&lock_);
        if (readOffset_ >= readFailureOffset_) {
            ReleaseSRWLockExclusive(&lock_);
            return {ByteStreamIoStatus::Failed, 0};
        }

        if (readOffset_ < inbound_.size()) {
            std::size_t count = destinationCapacity;
            if (readSliceIndex_ < readSlices_.size()) {
                count = (std::min)(count, readSlices_[readSliceIndex_]);
                ++readSliceIndex_;
            }
            count = (std::min)(count, inbound_.size() - readOffset_);
            count = (std::min)(count, readFailureOffset_ - readOffset_);
            if (count == 0) {
                ReleaseSRWLockExclusive(&lock_);
                return {ByteStreamIoStatus::Failed, 0};
            }

            std::memcpy(
                destination,
                inbound_.data() + readOffset_,
                count);
            readOffset_ += count;
            ReleaseSRWLockExclusive(&lock_);
            return {ByteStreamIoStatus::Success, count};
        }

        const bool endOfStream = endOfStreamWhenExhausted_;
        ReleaseSRWLockExclusive(&lock_);
        if (endOfStream) {
            return {ByteStreamIoStatus::EndOfStream, 0};
        }

        static_cast<void>(WaitForSingleObject(stopEvent_, INFINITE));
        return {ByteStreamIoStatus::Failed, 0};
    }

    ByteStreamIoResult Write(
        const void* source,
        std::size_t sourceBytes) noexcept override {
        if (source == nullptr || sourceBytes == 0) {
            return {ByteStreamIoStatus::Failed, 0};
        }

        const HANDLE gates[] = {stopEvent_, writeGate_};
        const DWORD gate = WaitForMultipleObjects(2, gates, FALSE, INFINITE);
        if (gate != WAIT_OBJECT_0 + 1) {
            return {ByteStreamIoStatus::Failed, 0};
        }

        AcquireSRWLockExclusive(&lock_);
        if (forceZeroWrite_ || writtenBytes_ >= writeFailureOffset_) {
            ReleaseSRWLockExclusive(&lock_);
            return {ByteStreamIoStatus::Success, 0};
        }

        std::size_t count = (std::min)(
            sourceBytes,
            maximumWriteBytes_);
        count = (std::min)(
            count,
            writeFailureOffset_ - writtenBytes_);
        count = (std::min)(
            count,
            written_.size() - writtenBytes_);
        if (count == 0) {
            ReleaseSRWLockExclusive(&lock_);
            return {ByteStreamIoStatus::Failed, 0};
        }
        const auto* begin = static_cast<const std::uint8_t*>(source);
        std::memcpy(written_.data() + writtenBytes_, begin, count);
        writtenBytes_ += count;
        ++writeCalls_;
        SetEvent(progressEvent_);
        ReleaseSRWLockExclusive(&lock_);
        return {ByteStreamIoStatus::Success, count};
    }

    void Stop() noexcept override {
        if (InterlockedIncrement(&stopCalls_) == 1) {
            SetEvent(stopEvent_);
            SetEvent(writeGate_);
            SetEvent(progressEvent_);
        }
    }

    void ReturnEofWhenExhausted() noexcept {
        AcquireSRWLockExclusive(&lock_);
        endOfStreamWhenExhausted_ = true;
        ReleaseSRWLockExclusive(&lock_);
    }

    void FailReadAt(std::size_t byteOffset) noexcept {
        AcquireSRWLockExclusive(&lock_);
        readFailureOffset_ = byteOffset;
        ReleaseSRWLockExclusive(&lock_);
    }

    void FailWriteAt(std::size_t byteOffset) noexcept {
        AcquireSRWLockExclusive(&lock_);
        writeFailureOffset_ = byteOffset;
        ReleaseSRWLockExclusive(&lock_);
    }

    void ReturnZeroFromWrite() noexcept {
        AcquireSRWLockExclusive(&lock_);
        forceZeroWrite_ = true;
        ReleaseSRWLockExclusive(&lock_);
    }

    void BlockWrites() noexcept {
        ResetEvent(writeGate_);
    }

    bool WaitForWrittenBytes(
        std::size_t expectedBytes,
        DWORD timeoutMilliseconds) noexcept {
        const ULONGLONG deadline =
            GetTickCount64() + timeoutMilliseconds;
        for (;;) {
            AcquireSRWLockExclusive(&lock_);
            if (writtenBytes_ >= expectedBytes) {
                ReleaseSRWLockExclusive(&lock_);
                return true;
            }
            ResetEvent(progressEvent_);
            ReleaseSRWLockExclusive(&lock_);

            const ULONGLONG now = GetTickCount64();
            if (now >= deadline) {
                return false;
            }
            const ULONGLONG remaining = deadline - now;
            const DWORD wait = WaitForSingleObject(
                progressEvent_,
                remaining >= INFINITE
                    ? INFINITE - 1
                    : static_cast<DWORD>(remaining));
            if (wait != WAIT_OBJECT_0) {
                return false;
            }
        }
    }

    std::vector<std::uint8_t> WrittenBytes() noexcept {
        AcquireSRWLockShared(&lock_);
        std::vector<std::uint8_t> copy(writtenBytes_);
        std::memcpy(copy.data(), written_.data(), writtenBytes_);
        ReleaseSRWLockShared(&lock_);
        return copy;
    }

    std::size_t WriteCalls() noexcept {
        AcquireSRWLockShared(&lock_);
        const std::size_t result = writeCalls_;
        ReleaseSRWLockShared(&lock_);
        return result;
    }

    LONG StopCalls() const noexcept {
        return InterlockedCompareExchange(
            const_cast<volatile LONG*>(&stopCalls_),
            0,
            0);
    }

private:
    static constexpr std::size_t MaximumCapturedBytes = 1024U * 1024U;

    std::vector<std::uint8_t> inbound_;
    std::vector<std::size_t> readSlices_;
    std::vector<std::uint8_t> written_;
    std::size_t readOffset_ = 0;
    std::size_t readSliceIndex_ = 0;
    std::size_t maximumWriteBytes_;
    std::size_t readFailureOffset_ =
        (std::numeric_limits<std::size_t>::max)();
    std::size_t writeFailureOffset_ =
        (std::numeric_limits<std::size_t>::max)();
    std::size_t writtenBytes_ = 0;
    std::size_t writeCalls_ = 0;
    SRWLOCK lock_ = SRWLOCK_INIT;
    HANDLE stopEvent_;
    HANDLE writeGate_;
    HANDLE progressEvent_;
    volatile LONG stopCalls_ = 0;
    bool endOfStreamWhenExhausted_ = false;
    bool forceZeroWrite_ = false;
};

bool WaitForOutcome(
    OpaqueDuplexPump* pump,
    OpaquePumpOutcome expected,
    DWORD timeoutMilliseconds = 2000) noexcept {
    const ULONGLONG deadline =
        GetTickCount64() + timeoutMilliseconds;
    do {
        if (pump->Snapshot().outcome == expected) {
            return true;
        }
        SwitchToThread();
    } while (GetTickCount64() < deadline);
    return false;
}

bool WaitForWorkers(
    OpaqueDuplexPump* pump,
    LONG expected,
    DWORD timeoutMilliseconds = 2000) noexcept {
    const ULONGLONG deadline =
        GetTickCount64() + timeoutMilliseconds;
    do {
        if (pump->Snapshot().activeWorkers == expected) {
            return true;
        }
        SwitchToThread();
    } while (GetTickCount64() < deadline);
    return false;
}

void CheckBidirectionalOpaqueParity() {
    const auto firstInbound = MakePattern(300, 0x19);
    const auto secondInbound = MakePattern(
        OpaqueDuplexPump::ReadBufferBytes * 2U + 257U,
        0xA4);
    ScriptedByteStream first(
        firstInbound,
        {1, 2, 17, 251, 29},
        7);
    ScriptedByteStream second(
        secondInbound,
        {OpaqueDuplexPump::ReadBufferBytes * 4U},
        3);
    OpaqueDuplexPump pump(&first, &second);

    Check(pump.IsValid(), "opaque parity pump configuration is valid");
    Check(pump.Start(), "opaque parity pump starts");
    Check(
        second.WaitForWrittenBytes(firstInbound.size(), 3000),
        "fragmented first stream reaches second");
    Check(
        first.WaitForWrittenBytes(secondInbound.size(), 3000),
        "coalesced second stream reaches first");
    Check(
        pump.StopAndJoin(),
        "opaque parity pump stops and joins");

    Check(
        second.WrittenBytes() == firstInbound,
        "300-byte XOR-wrap stream remains byte-identical");
    Check(
        first.WrittenBytes() == secondInbound,
        "multi-buffer stream remains byte-identical");
    Check(
        first.WriteCalls() > 2 && second.WriteCalls() > 2,
        "complete-write loops handle partial writes");
}

void CheckEofAndIoFailures() {
    const auto eofPayload = MakePattern(97, 0x44);
    ScriptedByteStream eofSource(eofPayload, {1, 13, 83});
    ScriptedByteStream eofDestination;
    eofSource.ReturnEofWhenExhausted();
    OpaqueDuplexPump eofPump(&eofSource, &eofDestination);
    Check(eofPump.Start(), "EOF pump starts");
    Check(
        WaitForOutcome(&eofPump, OpaquePumpOutcome::EndOfStream),
        "EOF becomes a finite terminal outcome");
    Check(eofPump.StopAndJoin(), "EOF workers join");
    Check(
        eofDestination.WrittenBytes() == eofPayload,
        "EOF drains already-admitted bytes before closing");

    ScriptedByteStream badReader(MakePattern(8, 0x10));
    ScriptedByteStream readPeer;
    badReader.FailReadAt(0);
    OpaqueDuplexPump readPump(&badReader, &readPeer);
    Check(readPump.Start(), "read-failure pump starts");
    Check(
        WaitForOutcome(&readPump, OpaquePumpOutcome::ReadFailure),
        "read failure stops both directions");
    Check(readPump.StopAndJoin(), "read-failure workers join");

    const auto writePayload = MakePattern(32, 0x70);
    ScriptedByteStream writeSource(writePayload);
    ScriptedByteStream badWriter({}, {}, 3);
    badWriter.FailWriteAt(5);
    OpaqueDuplexPump writePump(&writeSource, &badWriter);
    Check(writePump.Start(), "write-failure pump starts");
    Check(
        WaitForOutcome(&writePump, OpaquePumpOutcome::WriteFailure),
        "partial write failure is terminal");
    Check(writePump.StopAndJoin(), "write-failure workers join");
    const auto writtenPrefix = badWriter.WrittenBytes();
    Check(
        writtenPrefix.size() == 5 &&
            std::equal(
                writtenPrefix.begin(),
                writtenPrefix.end(),
                writePayload.begin()),
        "partial write preserves only the exact successful prefix");

    ScriptedByteStream zeroSource(MakePattern(4, 0x91));
    ScriptedByteStream zeroWriter;
    zeroWriter.ReturnZeroFromWrite();
    OpaqueDuplexPump zeroPump(&zeroSource, &zeroWriter);
    Check(zeroPump.Start(), "zero-write pump starts");
    Check(
        WaitForOutcome(&zeroPump, OpaquePumpOutcome::WriteFailure),
        "zero-progress write cannot spin");
    Check(zeroPump.StopAndJoin(), "zero-write workers join");
}

void CheckQueueOverflowAndStop() {
    BoundedChunkQueueLimits tiny{};
    tiny.itemCapacity = 2;
    tiny.byteCapacity = 2;
    tiny.maximumChunkBytes = 1;
    tiny.producerAdmissionTimeoutMilliseconds = 30;

    ScriptedByteStream source({1, 2, 3, 4}, {1, 1, 1, 1});
    ScriptedByteStream stalledWriter;
    stalledWriter.BlockWrites();
    OpaqueDuplexPump overflowPump(&source, &stalledWriter, tiny);
    Check(overflowPump.Start(), "queue-overflow pump starts");
    Check(
        WaitForOutcome(
            &overflowPump,
            OpaquePumpOutcome::QueueAdmissionTimedOut),
        "stalled writer reaches bounded admission timeout");
    const auto overflowSnapshot = overflowPump.Snapshot();
    Check(
        overflowSnapshot.firstToSecond.highWaterItemCount == 2 &&
            overflowSnapshot.firstToSecond.highWaterByteCount == 2,
        "queue overflow respects simultaneous item and byte bounds");
    Check(overflowPump.StopAndJoin(), "overflow workers join");

    ScriptedByteStream first;
    ScriptedByteStream second;
    OpaqueDuplexPump stoppedPump(&first, &second);
    Check(stoppedPump.Start(), "blocked pump starts");
    Check(
        WaitForWorkers(&stoppedPump, 4),
        "all four pump workers become active");
    Check(stoppedPump.StopAndJoin(), "stop unblocks and joins all workers");
    const auto stoppedSnapshot = stoppedPump.Snapshot();
    Check(
        stoppedSnapshot.activeWorkers == 0 &&
            stoppedSnapshot.joined &&
            stoppedSnapshot.outcome ==
                OpaquePumpOutcome::StopRequested,
        "joined stop exposes a finite clean snapshot");
    Check(
        stoppedPump.StopAndJoin(),
        "coordinated stop is idempotent");
    Check(
        first.StopCalls() == 1 && second.StopCalls() == 1,
        "idempotent stop signals each stream once");
}

void CheckIndependentPumps() {
    BoundedChunkQueueLimits slowLimits{};
    slowLimits.itemCapacity = 2;
    slowLimits.byteCapacity = 2;
    slowLimits.maximumChunkBytes = 1;
    slowLimits.producerAdmissionTimeoutMilliseconds = 5000;

    ScriptedByteStream slowSource({1, 2, 3, 4}, {1, 1, 1, 1});
    ScriptedByteStream slowDestination;
    slowDestination.BlockWrites();
    OpaqueDuplexPump slow(
        &slowSource,
        &slowDestination,
        slowLimits);

    const auto healthyPayload = MakePattern(65537, 0x2D);
    ScriptedByteStream healthySource(healthyPayload);
    ScriptedByteStream healthyDestination({}, {}, 11);
    OpaqueDuplexPump healthy(
        &healthySource,
        &healthyDestination);

    Check(slow.Start(), "slow isolated pump starts");
    Check(healthy.Start(), "healthy isolated pump starts");
    Check(
        healthyDestination.WaitForWrittenBytes(
            healthyPayload.size(),
            3000),
        "healthy pump progresses while another writer is stalled");
    Check(
        slow.Snapshot().outcome == OpaquePumpOutcome::None,
        "slow pump remains independently backpressured");
    Check(healthy.StopAndJoin(), "healthy isolated pump joins");
    Check(slow.StopAndJoin(), "slow isolated pump joins");
    Check(
        healthyDestination.WrittenBytes() == healthyPayload,
        "healthy isolated pump preserves every byte");
}

} // namespace

int RunOpaqueDuplexPumpTests() {
    Failures = 0;
    CheckBidirectionalOpaqueParity();
    CheckEofAndIoFailures();
    CheckQueueOverflowAndStop();
    CheckIndependentPumps();
    Failures += RunOpaqueDuplexPumpLifecycleTests();
    return Failures;
}
