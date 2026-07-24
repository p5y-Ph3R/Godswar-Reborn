#include "OpaqueDuplexPumpLifecycleTests.h"

#include "../src/OpaqueDuplexPump.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::ByteStreamIoResult;
using godswar::network::ByteStreamIoStatus;
using godswar::network::IByteStream;
using godswar::network::OpaqueDuplexPump;
using godswar::network::OpaquePumpOutcome;
using godswar::network::OpaquePumpThreadHooks;

int Failures = 0;

void Check(bool condition, const char* message) noexcept {
    if (!condition) {
        std::printf("FAIL: %s\n", message);
        ++Failures;
    }
}

class LifecycleStream final : public IByteStream {
public:
    explicit LifecycleStream(bool emitOneByte = false) noexcept
        : stopEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
          writeEntered_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
          emitOneByte_(emitOneByte ? 1 : 0) {
    }

    ~LifecycleStream() noexcept {
        if (stopEvent_ != nullptr) {
            CloseHandle(stopEvent_);
        }
        if (writeEntered_ != nullptr) {
            CloseHandle(writeEntered_);
        }
    }

    ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept override {
        if (destination == nullptr || destinationCapacity == 0) {
            return {ByteStreamIoStatus::Failed, 0};
        }
        if (InterlockedCompareExchange(&emitOneByte_, 0, 1) == 1) {
            *static_cast<std::uint8_t*>(destination) = 0xA5;
            return {ByteStreamIoStatus::Success, 1};
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
        SetEvent(writeEntered_);
        static_cast<void>(WaitForSingleObject(stopEvent_, INFINITE));
        return {ByteStreamIoStatus::Failed, 0};
    }

    void Stop() noexcept override {
        if (InterlockedIncrement(&stopCalls_) == 1) {
            SetEvent(stopEvent_);
        }
    }

    bool WaitForBlockedWrite() noexcept {
        return WaitForSingleObject(writeEntered_, 1000) ==
            WAIT_OBJECT_0;
    }

    LONG StopCalls() noexcept {
        return InterlockedCompareExchange(&stopCalls_, 0, 0);
    }

private:
    HANDLE stopEvent_;
    HANDLE writeEntered_;
    volatile LONG emitOneByte_;
    volatile LONG stopCalls_ = 0;
};

struct ThreadHookState final {
    volatile LONG attempts = 0;
    volatile LONG closes = 0;
    LONG failAttempt = -1;
    LONG blockAttempt = -1;
    HANDLE createEntered = nullptr;
    HANDLE continueCreate = nullptr;
};

HANDLE CreateHookWorker(
    LPTHREAD_START_ROUTINE entry,
    void* parameter,
    void* rawContext) noexcept {
    auto* context = static_cast<ThreadHookState*>(rawContext);
    const LONG attempt = InterlockedIncrement(&context->attempts) - 1;
    if (attempt == context->blockAttempt) {
        SetEvent(context->createEntered);
        static_cast<void>(
            WaitForSingleObject(context->continueCreate, INFINITE));
    }
    if (attempt == context->failAttempt) {
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
    return CreateThread(nullptr, 0, entry, parameter, 0, nullptr);
}

void CloseHookWorker(HANDLE worker, void* rawContext) noexcept {
    auto* context = static_cast<ThreadHookState*>(rawContext);
    InterlockedIncrement(&context->closes);
    static_cast<void>(CloseHandle(worker));
}

OpaquePumpThreadHooks MakeHooks(ThreadHookState* state) noexcept {
    OpaquePumpThreadHooks hooks{};
    hooks.create = CreateHookWorker;
    hooks.close = CloseHookWorker;
    hooks.context = state;
    return hooks;
}

struct PumpCall final {
    OpaqueDuplexPump* pump = nullptr;
    bool start = false;
    bool result = false;
};

DWORD WINAPI PumpCallThread(void* rawContext) noexcept {
    auto* context = static_cast<PumpCall*>(rawContext);
    context->result = context->start
        ? context->pump->Start()
        : context->pump->StopAndJoin(2000);
    return 0;
}

bool Join(HANDLE thread) noexcept {
    const bool joined =
        thread != nullptr &&
        WaitForSingleObject(thread, 3000) == WAIT_OBJECT_0;
    if (thread != nullptr) {
        CloseHandle(thread);
    }
    return joined;
}

bool WaitForOutcome(
    OpaqueDuplexPump* pump,
    OpaquePumpOutcome outcome) noexcept {
    const ULONGLONG deadline = GetTickCount64() + 1000;
    do {
        if (pump->Snapshot().outcome == outcome) {
            return true;
        }
        SwitchToThread();
    } while (GetTickCount64() < deadline);
    return false;
}

void CheckConcurrentStartStop() noexcept {
    LifecycleStream first;
    LifecycleStream second;
    ThreadHookState state{};
    state.blockAttempt = 0;
    state.createEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    state.continueCreate = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const auto hooks = MakeHooks(&state);
    OpaqueDuplexPump pump(&first, &second, {}, &hooks);

    PumpCall start{&pump, true, false};
    HANDLE startThread =
        CreateThread(nullptr, 0, PumpCallThread, &start, 0, nullptr);
    Check(
        startThread != nullptr &&
            WaitForSingleObject(state.createEntered, 1000) ==
                WAIT_OBJECT_0,
        "Start did not reach blocked worker creation");

    PumpCall stop{&pump, false, false};
    HANDLE stopThread =
        CreateThread(nullptr, 0, PumpCallThread, &stop, 0, nullptr);
    Check(
        stopThread != nullptr &&
            WaitForOutcome(&pump, OpaquePumpOutcome::StopRequested),
        "concurrent Stop did not publish terminal state");
    SetEvent(state.continueCreate);

    Check(
        Join(startThread) &&
            Join(stopThread) &&
            !start.result &&
            stop.result,
        "concurrent Start/Stop did not converge");
    const auto snapshot = pump.Snapshot();
    Check(
        snapshot.joined &&
            snapshot.activeWorkers == 0 &&
            !snapshot.started &&
            state.attempts == 1 &&
            state.closes == 1,
        "concurrent Start/Stop leaked a worker");
    CloseHandle(state.createEntered);
    CloseHandle(state.continueCreate);
}

void CheckPartialStartFailure() noexcept {
    LifecycleStream first;
    LifecycleStream second;
    ThreadHookState state{};
    state.failAttempt = 2;
    const auto hooks = MakeHooks(&state);
    OpaqueDuplexPump pump(&first, &second, {}, &hooks);
    Check(!pump.Start(), "injected worker failure was accepted");
    const auto snapshot = pump.Snapshot();
    Check(
        snapshot.outcome == OpaquePumpOutcome::StartFailure &&
            snapshot.joined &&
            snapshot.activeWorkers == 0 &&
            state.attempts == 3 &&
            state.closes == 2,
        "partial-start workers were not joined and closed");
    Check(
        pump.StopAndJoin() &&
            state.closes == 2 &&
            first.StopCalls() == 1 &&
            second.StopCalls() == 1,
        "failed-start cleanup was not idempotent");
}

void CheckLifecycleBoundForBlockedWrite() noexcept {
    LifecycleStream source(true);
    LifecycleStream destination;
    OpaqueDuplexPump pump(&source, &destination);
    Check(
        pump.Start() && destination.WaitForBlockedWrite(),
        "single-chunk write did not block");
    Check(
        pump.Snapshot().outcome == OpaquePumpOutcome::None &&
            pump.StopAndJoin() &&
            pump.Snapshot().outcome ==
                OpaquePumpOutcome::StopRequested,
        "Stop did not bound an in-flight Slice 5 write");
}

} // namespace

int RunOpaqueDuplexPumpLifecycleTests() {
    Failures = 0;
    CheckConcurrentStartStop();
    CheckPartialStartFailure();
    CheckLifecycleBoundForBlockedWrite();
    return Failures;
}
