#include "LoopbackAcceptorTests.h"

#include "../src/LoopbackAcceptor.h"
#include "../src/WinSockRuntime.h"

#include <WinSock2.h>
#include <Windows.h>
#include <process.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::EnsureWinSock;
using godswar::network::LoopbackAcceptFailure;
using godswar::network::LoopbackAcceptor;
using godswar::network::SocketHandle;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

bool JoinTestThread(HANDLE thread, const char* message) {
    if (thread == nullptr) {
        Check(false, message);
        return false;
    }

    const auto waitResult = WaitForSingleObject(thread, 10'000);
    Check(waitResult == WAIT_OBJECT_0, message);
    if (waitResult != WAIT_OBJECT_0) {
        static_cast<void>(WaitForSingleObject(thread, INFINITE));
    }
    CloseHandle(thread);
    return waitResult == WAIT_OBJECT_0;
}

SocketHandle ConnectToLoopback(std::uint16_t port) {
    SocketHandle client(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
    if (!client.IsValid()) {
        return client;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);
    if (connect(
            client.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) == SOCKET_ERROR) {
        client.Reset();
    }

    return client;
}

struct WinSockThreadContext final {
    HANDLE start = nullptr;
    bool succeeded = false;
};

unsigned __stdcall EnsureWinSockWorker(void* rawContext) {
    auto* context = static_cast<WinSockThreadContext*>(rawContext);
    static_cast<void>(WaitForSingleObject(context->start, INFINITE));
    context->succeeded = EnsureWinSock();
    return 0;
}

void RunConcurrentWinSockCheck() {
    constexpr std::size_t WorkerCount = 16;
    WinSockThreadContext contexts[WorkerCount]{};
    HANDLE workers[WorkerCount]{};
    const auto start = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Check(start != nullptr, "WinSock start event creation failed");
    if (start == nullptr) {
        return;
    }

    std::size_t created = 0;
    for (; created < WorkerCount; ++created) {
        contexts[created].start = start;
        workers[created] = reinterpret_cast<HANDLE>(_beginthreadex(
            nullptr,
            0,
            EnsureWinSockWorker,
            &contexts[created],
            0,
            nullptr));
        if (workers[created] == nullptr) {
            Check(false, "WinSock worker creation failed");
            break;
        }
    }

    static_cast<void>(SetEvent(start));
    for (std::size_t index = 0; index < created; ++index) {
        static_cast<void>(JoinTestThread(
            workers[index],
            "WinSock worker did not finish"));
        Check(
            contexts[index].succeeded,
            "concurrent WinSock initialization failed");
    }
    CloseHandle(start);
}

void RunInvalidLifecycleCheck() {
    LoopbackAcceptor acceptor;
    Check(
        !acceptor.BeginAccept(),
        "accept began before listener open");
    Check(
        acceptor.Failure() == LoopbackAcceptFailure::InvalidState,
        "invalid begin did not report finite state failure");
    Check(
        acceptor.Open(),
        "listener could not recover from non-mutating invalid begin");
    Check(
        !acceptor.Open(),
        "listener opened twice");
    Check(
        acceptor.CancelAndJoin(5'000),
        "open listener did not cancel without a worker");
    Check(
        !acceptor.BeginAccept(),
        "accept began after cancellation");

    LoopbackAcceptor cancelled;
    Check(
        cancelled.CancelAndJoin(5'000),
        "pre-open cancellation failed");
    Check(
        !cancelled.Open(),
        "cancelled one-shot listener reopened");
}

void RunAcceptSuccessCheck() {
    LoopbackAcceptor acceptor;
    Check(acceptor.Open(), "loopback listener did not open");
    Check(acceptor.Port() != 0, "loopback listener did not choose a port");
    Check(acceptor.BeginAccept(), "loopback accept did not begin");

    auto client = ConnectToLoopback(acceptor.Port());
    Check(client.IsValid(), "test client did not connect to loopback");

    SocketHandle accepted;
    Check(
        acceptor.Complete(5'000, &accepted),
        "loopback accept did not complete");
    Check(accepted.IsValid(), "accepted loopback socket was invalid");
    Check(
        acceptor.Failure() == LoopbackAcceptFailure::None,
        "successful loopback accept retained a failure");
    Check(!acceptor.HasLiveWorker(), "accept worker remained live");

    const std::uint8_t sent[] = {0x10, 0x20, 0x30, 0x40};
    std::uint8_t received[sizeof(sent)]{};
    Check(
        send(
            client.Get(),
            reinterpret_cast<const char*>(sent),
            sizeof(sent),
            0) == sizeof(sent),
        "loopback test send failed");
    Check(
        recv(
            accepted.Get(),
            reinterpret_cast<char*>(received),
            sizeof(received),
            MSG_WAITALL) == sizeof(received),
        "accepted loopback socket did not receive exact bytes");
    Check(
        std::memcmp(sent, received, sizeof(sent)) == 0,
        "accepted loopback bytes changed");
    Check(
        acceptor.CancelAndJoin(5'000),
        "completed acceptor cleanup failed");
}

void RunDeadlineCheck() {
    LoopbackAcceptor acceptor;
    Check(acceptor.Open(), "deadline listener did not open");
    Check(acceptor.BeginAccept(), "deadline accept did not begin");

    SocketHandle accepted;
    const auto started = GetTickCount64();
    Check(
        !acceptor.Complete(100, &accepted),
        "accept without a peer ignored its deadline");
    const auto elapsed = GetTickCount64() - started;
    Check(
        elapsed >= 50 && elapsed <= 750,
        "accept deadline was reset or multiplied");
    Check(
        acceptor.Failure() == LoopbackAcceptFailure::Deadline,
        "accept timeout did not preserve deadline reason");
    Check(
        acceptor.CancelAndJoin(5'000),
        "deadline accept worker did not stop");
    Check(!acceptor.HasLiveWorker(), "deadline worker remained live");
}

struct OpenThreadContext final {
    LoopbackAcceptor* acceptor = nullptr;
    HANDLE start = nullptr;
    bool opened = false;
};

unsigned __stdcall OpenWorker(void* rawContext) {
    auto* context = static_cast<OpenThreadContext*>(rawContext);
    static_cast<void>(WaitForSingleObject(context->start, INFINITE));
    context->opened = context->acceptor->Open();
    return 0;
}

void RunConcurrentOpenCheck() {
    constexpr std::size_t WorkerCount = 8;
    LoopbackAcceptor acceptor;
    OpenThreadContext contexts[WorkerCount]{};
    HANDLE workers[WorkerCount]{};
    const auto start = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Check(start != nullptr, "concurrent-open event creation failed");
    if (start == nullptr) {
        return;
    }

    std::size_t created = 0;
    for (; created < WorkerCount; ++created) {
        contexts[created].acceptor = &acceptor;
        contexts[created].start = start;
        workers[created] = reinterpret_cast<HANDLE>(_beginthreadex(
            nullptr,
            0,
            OpenWorker,
            &contexts[created],
            0,
            nullptr));
        if (workers[created] == nullptr) {
            Check(false, "concurrent-open worker creation failed");
            break;
        }
    }

    static_cast<void>(SetEvent(start));
    std::size_t opened = 0;
    for (std::size_t index = 0; index < created; ++index) {
        static_cast<void>(JoinTestThread(
            workers[index],
            "concurrent-open worker did not finish"));
        if (contexts[index].opened) {
            ++opened;
        }
    }
    CloseHandle(start);

    if (created == WorkerCount) {
        Check(opened == 1, "concurrent Open did not have one owner");
        Check(
            acceptor.Port() != 0,
            "concurrent Open lost the committed listener");
    }
    Check(
        acceptor.CancelAndJoin(5'000),
        "concurrently opened listener did not clean up");
}

struct CompleteThreadContext final {
    LoopbackAcceptor* acceptor = nullptr;
    HANDLE start = nullptr;
    bool complete = false;
    SocketHandle accepted{};
};

struct CancelThreadContext final {
    LoopbackAcceptor* acceptor = nullptr;
    HANDLE start = nullptr;
    bool cancelled = false;
};

unsigned __stdcall CompleteWorker(void* rawContext) {
    auto* context = static_cast<CompleteThreadContext*>(rawContext);
    static_cast<void>(WaitForSingleObject(context->start, INFINITE));
    context->complete =
        context->acceptor->Complete(5'000, &context->accepted);
    return 0;
}

unsigned __stdcall CancelWorker(void* rawContext) {
    auto* context = static_cast<CancelThreadContext*>(rawContext);
    static_cast<void>(WaitForSingleObject(context->start, INFINITE));
    context->cancelled = context->acceptor->CancelAndJoin(5'000);
    return 0;
}

void RunConcurrentCancelCompleteCheck() {
    for (int iteration = 0; iteration < 32; ++iteration) {
        LoopbackAcceptor acceptor;
        Check(acceptor.Open(), "concurrent lifecycle listener did not open");
        Check(
            acceptor.BeginAccept(),
            "concurrent lifecycle accept did not begin");

        const auto start = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        Check(start != nullptr, "concurrent lifecycle event failed");
        if (start == nullptr) {
            return;
        }

        CompleteThreadContext complete{&acceptor, start};
        CancelThreadContext cancel{&acceptor, start};
        const auto completeWorker = reinterpret_cast<HANDLE>(_beginthreadex(
            nullptr,
            0,
            CompleteWorker,
            &complete,
            0,
            nullptr));
        const auto cancelWorker = reinterpret_cast<HANDLE>(_beginthreadex(
            nullptr,
            0,
            CancelWorker,
            &cancel,
            0,
            nullptr));
        Check(
            completeWorker != nullptr && cancelWorker != nullptr,
            "concurrent lifecycle worker creation failed");
        static_cast<void>(SetEvent(start));

        if (completeWorker != nullptr) {
            static_cast<void>(JoinTestThread(
                completeWorker,
                "concurrent Complete did not finish"));
        }
        if (cancelWorker != nullptr) {
            static_cast<void>(JoinTestThread(
                cancelWorker,
                "concurrent cancel did not finish"));
        }
        CloseHandle(start);

        Check(!complete.complete, "cancelled Complete reported success");
        Check(cancel.cancelled, "concurrent cancellation did not join");
        Check(
            !acceptor.HasLiveWorker(),
            "concurrent lifecycle left a worker live");
        Check(
            acceptor.Failure() == LoopbackAcceptFailure::Cancelled,
            "concurrent lifecycle lost cancellation reason");
        Check(
            acceptor.CancelAndJoin(5'000),
            "repeated concurrent cancellation was not idempotent");
    }
}

void RunRepeatedLifecycleCheck() {
    for (int iteration = 0; iteration < 64; ++iteration) {
        LoopbackAcceptor acceptor;
        Check(acceptor.Open(), "repeated loopback listener did not open");
        Check(
            acceptor.BeginAccept(),
            "repeated loopback accept did not begin");
        auto client = ConnectToLoopback(acceptor.Port());
        SocketHandle accepted;
        Check(
            client.IsValid() &&
                acceptor.Complete(5'000, &accepted),
            "repeated loopback accept did not complete");
    }
}

} // namespace

int RunLoopbackAcceptorTests() {
    Failures = 0;
    RunConcurrentWinSockCheck();
    RunInvalidLifecycleCheck();
    RunAcceptSuccessCheck();
    RunDeadlineCheck();
    RunConcurrentOpenCheck();
    RunConcurrentCancelCompleteCheck();
    RunRepeatedLifecycleCheck();
    return Failures;
}
