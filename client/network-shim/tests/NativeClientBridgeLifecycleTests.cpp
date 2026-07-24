#include "NativeClientBridgeLifecycleTests.h"

#include "../src/NativeClientBridge.h"
#include "../src/WinSockRuntime.h"

#include <WinSock2.h>
#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::ByteStreamIoResult;
using godswar::network::ByteStreamIoStatus;
using godswar::network::EnsureWinSock;
using godswar::network::IByteStream;
using godswar::network::ILegacyNetClient;
using godswar::network::NativeBridgeFailure;
using godswar::network::NativeBridgeState;
using godswar::network::NativeClientBridge;
using godswar::network::SocketHandle;
using godswar::network::WinSocketByteStream;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

bool CreateLoopbackPair(
    SocketHandle* first,
    SocketHandle* second) {
    if (first == nullptr || second == nullptr || !EnsureWinSock()) {
        return false;
    }

    SocketHandle listener(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
    if (!listener.IsValid()) {
        return false;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(
            listener.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) == SOCKET_ERROR ||
        listen(listener.Get(), 1) == SOCKET_ERROR) {
        return false;
    }

    int addressBytes = sizeof(address);
    if (getsockname(
            listener.Get(),
            reinterpret_cast<sockaddr*>(&address),
            &addressBytes) == SOCKET_ERROR) {
        return false;
    }

    SocketHandle outbound(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
    if (!outbound.IsValid() ||
        connect(
            outbound.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) == SOCKET_ERROR) {
        return false;
    }

    SocketHandle inbound(accept(listener.Get(), nullptr, nullptr));
    if (!inbound.IsValid()) {
        return false;
    }

    *first = static_cast<SocketHandle&&>(outbound);
    *second = static_cast<SocketHandle&&>(inbound);
    return true;
}

class LifecycleLegacyClient final : public ILegacyNetClient {
public:
    std::uint32_t Release() override {
        socket_.Shutdown();
        socket_.Reset();
        return 1;
    }

    void SetHost(const char* host, std::uint16_t port) override {
        std::size_t length = 0;
        if (host != nullptr) {
            while (length + 1 < sizeof(host_) &&
                   host[length] != '\0') {
                ++length;
            }
            std::memcpy(host_, host, length);
        }
        host_[length] = '\0';
        port_ = port;
    }

    bool Connect() override {
        if (connectEntered != nullptr) {
            SetEvent(connectEntered);
        }
        if (connectDelayMilliseconds != 0) {
            Sleep(connectDelayMilliseconds);
        }

        SocketHandle connection(socket(
            AF_INET,
            SOCK_STREAM,
            IPPROTO_TCP));
        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(port_);
        if (!connection.IsValid() ||
            ::connect(
                connection.Get(),
                reinterpret_cast<const sockaddr*>(&address),
                sizeof(address)) == SOCKET_ERROR) {
            return false;
        }

        socket_ = static_cast<SocketHandle&&>(connection);
        return true;
    }

    void DisConnect() override {
        ++disconnectCalls;
        socket_.Shutdown();
        socket_.Reset();
    }

    void Process() override {
    }

    std::uint32_t GetStatus() const override {
        return socket_.IsValid() ? 1U : 0U;
    }

    void* PickMsg() override {
        return nullptr;
    }

    bool SendMsg(const void*, int) override {
        return false;
    }

    long GetMsgNum() override {
        return 0;
    }

    DWORD connectDelayMilliseconds = 0;
    HANDLE connectEntered = nullptr;
    int disconnectCalls = 0;

private:
    SocketHandle socket_;
    char host_[32]{};
    std::uint16_t port_ = 0;
};

class HeldOuterStream final : public IByteStream {
public:
    HeldOuterStream()
        : entered_(CreateEventW(nullptr, TRUE, FALSE, nullptr)),
          release_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
    }

    ~HeldOuterStream() {
        if (release_ != nullptr) {
            SetEvent(release_);
            CloseHandle(release_);
        }
        if (entered_ != nullptr) {
            CloseHandle(entered_);
        }
    }

    ByteStreamIoResult Read(
        void* destination,
        std::size_t destinationCapacity) noexcept override {
        if (destination == nullptr ||
            destinationCapacity == 0 ||
            entered_ == nullptr ||
            release_ == nullptr) {
            return {ByteStreamIoStatus::Failed, 0};
        }

        SetEvent(entered_);
        static_cast<void>(WaitForSingleObject(release_, INFINITE));
        return {ByteStreamIoStatus::Failed, 0};
    }

    ByteStreamIoResult Write(
        const void*,
        std::size_t) noexcept override {
        return {ByteStreamIoStatus::Failed, 0};
    }

    void Stop() noexcept override {
        // Deliberately retain Read for the join-timeout recovery check.
    }

    bool WaitUntilRead() const {
        return entered_ != nullptr &&
            WaitForSingleObject(entered_, 5'000) == WAIT_OBJECT_0;
    }

    void ReleaseRead() {
        if (release_ != nullptr) {
            SetEvent(release_);
        }
    }

private:
    HANDLE entered_ = nullptr;
    HANDLE release_ = nullptr;
};

void RunJoinTimeoutRecoveryCheck() {
    HeldOuterStream outer;
    LifecycleLegacyClient legacy;
    NativeClientBridge bridge;
    Check(
        bridge.Start(&legacy, &outer),
        "join-timeout bridge did not start");
    Check(
        outer.WaitUntilRead(),
        "join-timeout outer read did not start");
    Check(
        !bridge.StopAndJoin(10),
        "noncompliant outer stream did not hit join deadline");
    const auto pending = bridge.Snapshot();
    Check(
        pending.state == NativeBridgeState::JoinPending &&
            pending.failure == NativeBridgeFailure::JoinDeadline &&
            pending.hasPump,
        "join timeout falsely reported a running bridge");

    outer.ReleaseRead();
    Check(
        bridge.StopAndJoin(5'000),
        "bridge join did not recover after blocked read released");
    legacy.DisConnect();
}

struct StartContext final {
    NativeClientBridge* bridge = nullptr;
    LifecycleLegacyClient* legacy = nullptr;
    IByteStream* outer = nullptr;
    bool result = false;
};

DWORD WINAPI StartWorker(void* contextValue) noexcept {
    auto* context = static_cast<StartContext*>(contextValue);
    context->result = context->bridge->Start(
        context->legacy,
        context->outer);
    return ERROR_SUCCESS;
}

struct StopContext final {
    NativeClientBridge* bridge = nullptr;
    HANDLE gate = nullptr;
    bool result = false;
};

DWORD WINAPI StopWorker(void* contextValue) noexcept {
    auto* context = static_cast<StopContext*>(contextValue);
    if (context->gate != nullptr) {
        static_cast<void>(
            WaitForSingleObject(context->gate, INFINITE));
    }
    context->result = context->bridge->StopAndJoin();
    return ERROR_SUCCESS;
}

void RunStartStopOverlapCheck() {
    SocketHandle remote;
    SocketHandle bridgeSocket;
    Check(
        CreateLoopbackPair(&remote, &bridgeSocket),
        "concurrent-start socket pair creation failed");
    if (!bridgeSocket.IsValid()) {
        return;
    }

    WinSocketByteStream outer(
        static_cast<SocketHandle&&>(bridgeSocket));
    LifecycleLegacyClient legacy;
    legacy.connectDelayMilliseconds = 40;
    legacy.connectEntered =
        CreateEventW(nullptr, TRUE, FALSE, nullptr);
    NativeClientBridge bridge;
    StartContext context{&bridge, &legacy, &outer, false};
    HANDLE worker = CreateThread(
        nullptr,
        0,
        StartWorker,
        &context,
        0,
        nullptr);
    Check(
        worker != nullptr &&
            WaitForSingleObject(
                legacy.connectEntered,
                5'000) == WAIT_OBJECT_0,
        "concurrent bridge Start did not enter stock Connect");
    Check(
        bridge.StopAndJoin(5'000),
        "Stop did not coordinate with in-flight Start");
    Check(
        worker != nullptr &&
            WaitForSingleObject(worker, 5'000) == WAIT_OBJECT_0 &&
            context.result,
        "in-flight bridge Start did not finish safely");
    if (worker != nullptr) {
        CloseHandle(worker);
    }
    if (legacy.connectEntered != nullptr) {
        CloseHandle(legacy.connectEntered);
        legacy.connectEntered = nullptr;
    }
    legacy.DisConnect();
}

void RunTwoStopOwnersCheck() {
    SocketHandle remote;
    SocketHandle bridgeSocket;
    Check(
        CreateLoopbackPair(&remote, &bridgeSocket),
        "concurrent-stop socket pair creation failed");
    if (!bridgeSocket.IsValid()) {
        return;
    }

    WinSocketByteStream outer(
        static_cast<SocketHandle&&>(bridgeSocket));
    LifecycleLegacyClient legacy;
    NativeClientBridge bridge;
    Check(
        bridge.Start(&legacy, &outer),
        "concurrent-stop bridge did not start");
    HANDLE gate = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    StopContext contexts[] = {
        {&bridge, gate, false},
        {&bridge, gate, false},
    };
    HANDLE workers[] = {
        CreateThread(
            nullptr,
            0,
            StopWorker,
            &contexts[0],
            0,
            nullptr),
        CreateThread(
            nullptr,
            0,
            StopWorker,
            &contexts[1],
            0,
            nullptr),
    };
    SetEvent(gate);
    Check(
        workers[0] != nullptr &&
            workers[1] != nullptr &&
            WaitForMultipleObjects(
                2,
                workers,
                TRUE,
                5'000) == WAIT_OBJECT_0 &&
            contexts[0].result &&
            contexts[1].result,
        "concurrent bridge Stop callers did not converge");
    for (const auto worker : workers) {
        if (worker != nullptr) {
            CloseHandle(worker);
        }
    }
    if (gate != nullptr) {
        CloseHandle(gate);
    }
    legacy.DisConnect();
}

} // namespace

int RunNativeClientBridgeLifecycleTests() {
    Failures = 0;
    RunJoinTimeoutRecoveryCheck();
    RunStartStopOverlapCheck();
    RunTwoStopOwnersCheck();
    return Failures;
}
