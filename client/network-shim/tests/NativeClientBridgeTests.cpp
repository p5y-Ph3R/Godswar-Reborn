#include "NativeClientBridgeTests.h"
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

using godswar::network::BoundedChunkQueueLimits;
using godswar::network::EnsureWinSock;
using godswar::network::ILegacyNetClient;
using godswar::network::NativeBridgeFailure;
using godswar::network::NativeBridgeState;
using godswar::network::NativeClientBridge;
using godswar::network::SocketHandle;
using godswar::network::WinSocketByteStream;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (condition) {
        return;
    }

    std::fprintf(stderr, "FAIL: %s\n", message);
    ++Failures;
}

bool ConfigureSocket(SOCKET socketValue) {
    constexpr DWORD IoDeadlineMilliseconds = 5'000;
    return setsockopt(
               socketValue,
               SOL_SOCKET,
               SO_RCVTIMEO,
               reinterpret_cast<const char*>(
                   &IoDeadlineMilliseconds),
               sizeof(IoDeadlineMilliseconds)) != SOCKET_ERROR &&
        setsockopt(
               socketValue,
               SOL_SOCKET,
               SO_SNDTIMEO,
               reinterpret_cast<const char*>(
                   &IoDeadlineMilliseconds),
               sizeof(IoDeadlineMilliseconds)) != SOCKET_ERROR;
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

    const BOOL exclusive = TRUE;
    if (setsockopt(
            listener.Get(),
            SOL_SOCKET,
            SO_EXCLUSIVEADDRUSE,
            reinterpret_cast<const char*>(&exclusive),
            sizeof(exclusive)) == SOCKET_ERROR) {
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
    if (!inbound.IsValid() ||
        !ConfigureSocket(outbound.Get()) ||
        !ConfigureSocket(inbound.Get())) {
        return false;
    }

    *first = static_cast<SocketHandle&&>(outbound);
    *second = static_cast<SocketHandle&&>(inbound);
    return true;
}

bool SendAll(
    SOCKET socketValue,
    const std::uint8_t* bytes,
    std::size_t byteCount) {
    std::size_t offset = 0;
    while (offset < byteCount) {
        const auto remaining = byteCount - offset;
        const int sent = send(
            socketValue,
            reinterpret_cast<const char*>(bytes + offset),
            static_cast<int>(remaining),
            0);
        if (sent <= 0) {
            return false;
        }
        offset += static_cast<std::size_t>(sent);
    }

    return true;
}

bool ReceiveAll(
    SOCKET socketValue,
    std::uint8_t* bytes,
    std::size_t byteCount) {
    std::size_t offset = 0;
    while (offset < byteCount) {
        const auto remaining = byteCount - offset;
        const int received = recv(
            socketValue,
            reinterpret_cast<char*>(bytes + offset),
            static_cast<int>(remaining),
            0);
        if (received <= 0) {
            return false;
        }
        offset += static_cast<std::size_t>(received);
    }

    return true;
}

class BridgeLegacyClient final : public ILegacyNetClient {
public:
    std::uint32_t Release() override {
        ++releaseCalls;
        socket_.Shutdown();
        socket_.Reset();
        return 1;
    }

    void SetHost(const char* host, std::uint16_t port) override {
        ++setHostCalls;
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
        ++connectCalls;
        if (connectEntered != nullptr) {
            SetEvent(connectEntered);
        }
        if (connectDelayMilliseconds != 0) {
            Sleep(connectDelayMilliseconds);
        }
        if (failConnect || host_[0] == '\0' || port_ == 0) {
            return false;
        }

        SocketHandle connection(socket(
            AF_INET,
            SOCK_STREAM,
            IPPROTO_TCP));
        if (!connection.IsValid()) {
            return false;
        }

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = htons(port_);
        if (::connect(
                connection.Get(),
                reinterpret_cast<const sockaddr*>(&address),
                sizeof(address)) == SOCKET_ERROR ||
            !ConfigureSocket(connection.Get())) {
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

    bool SendMsg(const void* data, int size) override {
        return data != nullptr &&
            size > 0 &&
            SendAll(
                socket_.Get(),
                static_cast<const std::uint8_t*>(data),
                static_cast<std::size_t>(size));
    }

    long GetMsgNum() override {
        return 0;
    }

    bool Receive(std::uint8_t* bytes, std::size_t byteCount) {
        return ReceiveAll(socket_.Get(), bytes, byteCount);
    }

    bool failConnect = false;
    DWORD connectDelayMilliseconds = 0;
    HANDLE connectEntered = nullptr;
    int releaseCalls = 0;
    int setHostCalls = 0;
    int connectCalls = 0;
    int disconnectCalls = 0;
    char host_[32]{};
    std::uint16_t port_ = 0;

private:
    SocketHandle socket_;
};

void FillPayload(std::uint8_t* bytes, std::size_t byteCount, int salt) {
    for (std::size_t index = 0; index < byteCount; ++index) {
        bytes[index] = static_cast<std::uint8_t>(
            (index * 29U + static_cast<std::size_t>(salt)) & 0xFFU);
    }
}

void RunBidirectionalBridgeCheck() {
    SocketHandle remote;
    SocketHandle bridgeOuterSocket;
    Check(
        CreateLoopbackPair(&remote, &bridgeOuterSocket),
        "bridge outer socket pair creation failed");
    if (!remote.IsValid() || !bridgeOuterSocket.IsValid()) {
        return;
    }

    WinSocketByteStream outer(
        static_cast<SocketHandle&&>(bridgeOuterSocket));
    BridgeLegacyClient legacy;
    NativeClientBridge bridge;
    Check(
        bridge.Start(&legacy, &outer),
        "native client bridge did not start");
    Check(
        legacy.setHostCalls == 1 &&
            std::strcmp(legacy.host_, "127.0.0.1") == 0 &&
            legacy.port_ != 0 &&
            legacy.connectCalls == 1,
        "bridge did not use one ephemeral loopback stock connection");

    constexpr std::size_t PayloadBytes = 32 * 1024 + 731;
    std::uint8_t outbound[PayloadBytes]{};
    std::uint8_t received[PayloadBytes]{};
    FillPayload(outbound, sizeof(outbound), 17);

    Check(
        SendAll(remote.Get(), outbound, sizeof(outbound)) &&
            legacy.Receive(received, sizeof(received)) &&
            std::memcmp(outbound, received, sizeof(outbound)) == 0,
        "outer-to-stock bridge bytes changed");

    FillPayload(outbound, sizeof(outbound), 93);
    SecureZeroMemory(received, sizeof(received));
    Check(
        legacy.SendMsg(
            outbound,
            static_cast<int>(sizeof(outbound))) &&
            ReceiveAll(remote.Get(), received, sizeof(received)) &&
            std::memcmp(outbound, received, sizeof(outbound)) == 0,
        "stock-to-outer bridge bytes changed");

    const auto running = bridge.Snapshot();
    Check(
        running.state == NativeBridgeState::Running &&
            running.failure == NativeBridgeFailure::None &&
            running.hasPump &&
            running.pump.activeWorkers == 4,
        "running bridge lifecycle snapshot changed");

    Check(
        bridge.StopAndJoin(),
        "native bridge workers did not stop and join");
    legacy.DisConnect();
    const auto stopped = bridge.Snapshot();
    Check(
        stopped.state == NativeBridgeState::Stopped &&
            stopped.failure == NativeBridgeFailure::None &&
            !stopped.hasPump &&
            !stopped.stockDisconnectIssued &&
            legacy.disconnectCalls == 1,
        "stopped bridge retained runtime ownership");
    Check(
        bridge.StopAndJoin(),
        "repeated bridge stop was not idempotent");
}

void RunStockConnectFailureCheck() {
    SocketHandle remote;
    SocketHandle bridgeOuterSocket;
    Check(
        CreateLoopbackPair(&remote, &bridgeOuterSocket),
        "failure-test outer socket pair creation failed");
    if (!bridgeOuterSocket.IsValid()) {
        return;
    }

    WinSocketByteStream outer(
        static_cast<SocketHandle&&>(bridgeOuterSocket));
    BridgeLegacyClient legacy;
    legacy.failConnect = true;
    NativeClientBridge bridge;
    Check(
        !bridge.Start(&legacy, &outer, 1'000),
        "bridge accepted a failed stock connection");
    const auto failed = bridge.Snapshot();
    Check(
        failed.state == NativeBridgeState::Failed &&
            failed.failure == NativeBridgeFailure::StockConnect &&
            !failed.hasPump &&
            outer.IsStopped() &&
            failed.stockDisconnectIssued &&
            legacy.connectCalls == 1 &&
            legacy.disconnectCalls == 1,
        "stock-connect failure did not close both bridge legs");
    Check(
        bridge.StopAndJoin(),
        "failed bridge cleanup was not idempotent");
}

void RunInvalidQueueCheck() {
    SocketHandle remote;
    SocketHandle bridgeOuterSocket;
    Check(
        CreateLoopbackPair(&remote, &bridgeOuterSocket),
        "invalid-queue outer socket pair creation failed");
    if (!bridgeOuterSocket.IsValid()) {
        return;
    }

    WinSocketByteStream outer(
        static_cast<SocketHandle&&>(bridgeOuterSocket));
    BridgeLegacyClient legacy;
    BoundedChunkQueueLimits invalidLimits{};
    invalidLimits.itemCapacity = 0;
    NativeClientBridge bridge(invalidLimits);
    Check(
        !bridge.Start(&legacy, &outer),
        "bridge accepted invalid queue limits");
    const auto failed = bridge.Snapshot();
    Check(
        failed.failure == NativeBridgeFailure::PumpAllocation &&
            outer.IsStopped() &&
            legacy.disconnectCalls == 1,
        "invalid queue failure retained a bridge leg");
}

void RunArgumentChecks() {
    NativeClientBridge bridge;
    BridgeLegacyClient legacy;
    Check(
        !bridge.Start(nullptr, nullptr),
        "bridge accepted null endpoints");
    Check(
        bridge.Snapshot().failure ==
            NativeBridgeFailure::InvalidArgument,
        "bridge null-endpoint failure changed");
    Check(
        !bridge.Start(&legacy, nullptr),
        "bridge accepted a null outer stream");
}

void RunStartupDeadlineCheck() {
    SocketHandle remote;
    SocketHandle bridgeOuterSocket;
    Check(
        CreateLoopbackPair(&remote, &bridgeOuterSocket),
        "deadline-test outer socket pair creation failed");
    if (!bridgeOuterSocket.IsValid()) {
        return;
    }

    WinSocketByteStream outer(
        static_cast<SocketHandle&&>(bridgeOuterSocket));
    BridgeLegacyClient legacy;
    legacy.connectDelayMilliseconds = 40;
    NativeClientBridge bridge;
    const auto startedAt = GetTickCount64();
    Check(
        !bridge.Start(&legacy, &outer, 10),
        "late stock Connect produced a successful bridge");
    const auto elapsed = GetTickCount64() - startedAt;
    const auto failed = bridge.Snapshot();
    Check(
        elapsed >= 30 &&
            failed.state == NativeBridgeState::Failed &&
            failed.failure ==
                NativeBridgeFailure::OperationDeadline &&
            failed.acceptFailure ==
                godswar::network::LoopbackAcceptFailure::Deadline &&
            failed.stockDisconnectIssued &&
            legacy.disconnectCalls == 1,
        "late stock Connect was not rejected and cleaned up");
}

void RunSpontaneousTerminationCheck() {
    SocketHandle remote;
    SocketHandle bridgeOuterSocket;
    Check(
        CreateLoopbackPair(&remote, &bridgeOuterSocket),
        "termination-test outer socket pair creation failed");
    if (!remote.IsValid() || !bridgeOuterSocket.IsValid()) {
        return;
    }

    WinSocketByteStream outer(
        static_cast<SocketHandle&&>(bridgeOuterSocket));
    BridgeLegacyClient legacy;
    NativeClientBridge bridge;
    Check(
        bridge.Start(&legacy, &outer),
        "termination-test bridge did not start");
    Check(
        shutdown(remote.Get(), SD_SEND) != SOCKET_ERROR,
        "termination-test peer half-close failed");

    godswar::network::NativeClientBridgeSnapshot terminated{};
    for (int attempt = 0; attempt < 200; ++attempt) {
        terminated = bridge.Snapshot();
        if (terminated.failure ==
            NativeBridgeFailure::PumpTerminated) {
            break;
        }
        Sleep(5);
    }
    Check(
        terminated.state == NativeBridgeState::JoinPending &&
            terminated.failure ==
                NativeBridgeFailure::PumpTerminated,
        "terminal pump outcome remained reported as running");
    Check(
        bridge.StopAndJoin(),
        "terminated pump did not join");
    legacy.DisConnect();
}

void RunRepeatedLifecycleCheck() {
    for (int iteration = 0; iteration < 16; ++iteration) {
        SocketHandle remote;
        SocketHandle bridgeOuterSocket;
        if (!CreateLoopbackPair(&remote, &bridgeOuterSocket)) {
            Check(false, "repeated bridge socket pair failed");
            return;
        }

        WinSocketByteStream outer(
            static_cast<SocketHandle&&>(bridgeOuterSocket));
        BridgeLegacyClient legacy;
        NativeClientBridge bridge;
        Check(
            bridge.Start(&legacy, &outer),
            "repeated bridge start failed");
        Check(
            bridge.StopAndJoin(),
            "repeated bridge stop failed");
        legacy.DisConnect();
    }
}

} // namespace

int RunNativeClientBridgeTests() {
    Failures = 0;
    if (!EnsureWinSock()) {
        std::fprintf(stderr, "FAIL: WinSock bridge initialization failed\n");
        return 1;
    }

    RunBidirectionalBridgeCheck();
    RunStockConnectFailureCheck();
    RunInvalidQueueCheck();
    RunArgumentChecks();
    RunStartupDeadlineCheck();
    RunSpontaneousTerminationCheck();
    RunRepeatedLifecycleCheck();
    Failures += RunNativeClientBridgeLifecycleTests();
    return Failures;
}
