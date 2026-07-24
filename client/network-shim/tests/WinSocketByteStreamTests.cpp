#include "WinSocketByteStreamTests.h"

#include "../src/OpaqueDuplexPump.h"
#include "../src/WinSocketByteStream.h"
#include "../src/WinSockRuntime.h"

#include <WinSock2.h>
#include <Windows.h>

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {

using godswar::network::ByteStreamIoResult;
using godswar::network::ByteStreamIoStatus;
using godswar::network::EnsureWinSock;
using godswar::network::OpaqueDuplexPump;
using godswar::network::SocketHandle;
using godswar::network::WinSocketByteStream;

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
            seed + index * 29U + index / 127U);
    }
    return bytes;
}

bool CreateConnectedSockets(
    SocketHandle* bridgeSocket,
    SocketHandle* peerSocket) noexcept {
    if (bridgeSocket == nullptr || peerSocket == nullptr) {
        return false;
    }
    bridgeSocket->Reset();
    peerSocket->Reset();

    SocketHandle listener(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
    if (!listener.IsValid()) {
        return false;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
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

    SocketHandle peer(socket(AF_INET, SOCK_STREAM, IPPROTO_TCP));
    if (!peer.IsValid() ||
        connect(
            peer.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) == SOCKET_ERROR) {
        return false;
    }

    SocketHandle bridge(accept(listener.Get(), nullptr, nullptr));
    if (!bridge.IsValid()) {
        return false;
    }

    const BOOL noDelay = TRUE;
    if (setsockopt(
            peer.Get(),
            IPPROTO_TCP,
            TCP_NODELAY,
            reinterpret_cast<const char*>(&noDelay),
            sizeof(noDelay)) == SOCKET_ERROR ||
        setsockopt(
            bridge.Get(),
            IPPROTO_TCP,
            TCP_NODELAY,
            reinterpret_cast<const char*>(&noDelay),
            sizeof(noDelay)) == SOCKET_ERROR) {
        return false;
    }

    const DWORD timeoutMilliseconds = 5000;
    static_cast<void>(setsockopt(
        peer.Get(),
        SOL_SOCKET,
        SO_RCVTIMEO,
        reinterpret_cast<const char*>(&timeoutMilliseconds),
        sizeof(timeoutMilliseconds)));
    static_cast<void>(setsockopt(
        peer.Get(),
        SOL_SOCKET,
        SO_SNDTIMEO,
        reinterpret_cast<const char*>(&timeoutMilliseconds),
        sizeof(timeoutMilliseconds)));

    *bridgeSocket = static_cast<SocketHandle&&>(bridge);
    *peerSocket = static_cast<SocketHandle&&>(peer);
    return true;
}

bool SendExactly(
    SOCKET socketValue,
    const std::vector<std::uint8_t>& bytes) noexcept {
    std::size_t offset = 0;
    while (offset < bytes.size()) {
        const std::size_t remaining = bytes.size() - offset;
        const int sent = send(
            socketValue,
            reinterpret_cast<const char*>(bytes.data() + offset),
            static_cast<int>(remaining),
            0);
        if (sent <= 0) {
            return false;
        }
        offset += static_cast<std::size_t>(sent);
    }
    return true;
}

bool ReceiveExactly(
    SOCKET socketValue,
    std::vector<std::uint8_t>* bytes) noexcept {
    if (bytes == nullptr) {
        return false;
    }

    std::size_t offset = 0;
    while (offset < bytes->size()) {
        const int received = recv(
            socketValue,
            reinterpret_cast<char*>(bytes->data() + offset),
            static_cast<int>(bytes->size() - offset),
            0);
        if (received <= 0) {
            return false;
        }
        offset += static_cast<std::size_t>(received);
    }
    return true;
}

bool ReadExactly(
    WinSocketByteStream* stream,
    std::vector<std::uint8_t>* bytes,
    std::size_t* maximumRead = nullptr) noexcept {
    if (stream == nullptr || bytes == nullptr) {
        return false;
    }

    std::size_t offset = 0;
    std::size_t observedMaximum = 0;
    while (offset < bytes->size()) {
        const ByteStreamIoResult read = stream->Read(
            bytes->data() + offset,
            bytes->size() - offset);
        if (read.status != ByteStreamIoStatus::Success ||
            read.bytesTransferred == 0 ||
            read.bytesTransferred > bytes->size() - offset) {
            return false;
        }
        observedMaximum = (std::max)(
            observedMaximum,
            read.bytesTransferred);
        offset += read.bytesTransferred;
    }
    if (maximumRead != nullptr) {
        *maximumRead = observedMaximum;
    }
    return true;
}

bool WriteExactly(
    WinSocketByteStream* stream,
    const std::vector<std::uint8_t>& bytes,
    std::size_t* maximumWrite = nullptr) noexcept {
    if (stream == nullptr) {
        return false;
    }

    std::size_t offset = 0;
    std::size_t observedMaximum = 0;
    while (offset < bytes.size()) {
        const ByteStreamIoResult written = stream->Write(
            bytes.data() + offset,
            bytes.size() - offset);
        if (written.status != ByteStreamIoStatus::Success ||
            written.bytesTransferred == 0 ||
            written.bytesTransferred > bytes.size() - offset) {
            return false;
        }
        observedMaximum = (std::max)(
            observedMaximum,
            written.bytesTransferred);
        offset += written.bytesTransferred;
    }
    if (maximumWrite != nullptr) {
        *maximumWrite = observedMaximum;
    }
    return true;
}

void RunExactAndBoundedIoCheck() {
    SocketHandle owned;
    SocketHandle peer;
    Check(
        CreateConnectedSockets(&owned, &peer),
        "WinSocket exact-I/O pair did not connect");
    WinSocketByteStream stream(static_cast<SocketHandle&&>(owned));
    Check(
        stream.IsValid() && !owned.IsValid(),
        "WinSocket stream move-owns its descriptor");

    const auto inbound = MakePattern(
        WinSocketByteStream::MaximumIoBytes + 257U,
        0x31);
    Check(
        SendExactly(peer.Get(), inbound),
        "WinSocket peer did not send bounded-read fixture");
    std::vector<std::uint8_t> received(inbound.size());
    std::size_t maximumRead = 0;
    Check(
        ReadExactly(&stream, &received, &maximumRead),
        "WinSocket stream did not read the exact fixture");
    Check(
        received == inbound,
        "WinSocket read changed opaque bytes");
    Check(
        maximumRead <= WinSocketByteStream::MaximumIoBytes,
        "WinSocket read exceeded the fixed bridge bound");

    const auto outbound = MakePattern(
        WinSocketByteStream::MaximumIoBytes * 2U + 257U,
        0xB8);
    std::size_t maximumWrite = 0;
    Check(
        WriteExactly(&stream, outbound, &maximumWrite),
        "WinSocket stream did not write the complete fixture");
    std::vector<std::uint8_t> peerReceived(outbound.size());
    Check(
        ReceiveExactly(peer.Get(), &peerReceived),
        "WinSocket peer did not receive the complete fixture");
    Check(
        peerReceived == outbound,
        "WinSocket write changed opaque bytes");
    Check(
        maximumWrite <= WinSocketByteStream::MaximumIoBytes,
        "WinSocket write exceeded the fixed bridge bound");
}

void RunFullPumpCompatibilityCheck() {
    SocketHandle firstOwned;
    SocketHandle firstPeer;
    SocketHandle secondOwned;
    SocketHandle secondPeer;
    Check(
        CreateConnectedSockets(&firstOwned, &firstPeer) &&
            CreateConnectedSockets(&secondOwned, &secondPeer),
        "WinSocket pump socket pairs did not connect");

    WinSocketByteStream first(static_cast<SocketHandle&&>(firstOwned));
    WinSocketByteStream second(static_cast<SocketHandle&&>(secondOwned));
    OpaqueDuplexPump pump(&first, &second);
    Check(pump.Start(), "WinSocket full duplex pump did not start");

    const auto firstBytes = MakePattern(300, 0x14);
    const auto secondBytes = MakePattern(
        WinSocketByteStream::MaximumIoBytes * 2U + 257U,
        0xD1);
    Check(
        SendExactly(firstPeer.Get(), firstBytes) &&
            SendExactly(secondPeer.Get(), secondBytes),
        "WinSocket pump peers did not send fixtures");

    std::vector<std::uint8_t> fromFirst(firstBytes.size());
    std::vector<std::uint8_t> fromSecond(secondBytes.size());
    Check(
        ReceiveExactly(secondPeer.Get(), &fromFirst) &&
            ReceiveExactly(firstPeer.Get(), &fromSecond),
        "WinSocket pump peers did not receive fixtures");
    Check(
        fromFirst == firstBytes && fromSecond == secondBytes,
        "WinSocket full duplex pump changed or reordered bytes");
    static_cast<void>(shutdown(firstPeer.Get(), SD_BOTH));
    static_cast<void>(shutdown(secondPeer.Get(), SD_BOTH));
    Check(pump.StopAndJoin(), "WinSocket full duplex workers did not join");
}

void RunEofAndInvalidCheck() {
    SocketHandle owned;
    SocketHandle peer;
    Check(
        CreateConnectedSockets(&owned, &peer),
        "WinSocket EOF pair did not connect");
    WinSocketByteStream stream(static_cast<SocketHandle&&>(owned));
    Check(
        shutdown(peer.Get(), SD_SEND) != SOCKET_ERROR,
        "WinSocket peer half-close failed");
    std::uint8_t byte = 0;
    const auto eof = stream.Read(&byte, sizeof(byte));
    Check(
        eof.status == ByteStreamIoStatus::EndOfStream &&
            eof.bytesTransferred == 0,
        "WinSocket peer half-close did not report EOF");

    SocketHandle invalid;
    WinSocketByteStream invalidStream(
        static_cast<SocketHandle&&>(invalid));
    Check(!invalidStream.IsValid(), "invalid WinSocket became valid");
    Check(
        invalidStream.Read(&byte, sizeof(byte)).status ==
                ByteStreamIoStatus::Failed &&
            invalidStream.Write(&byte, sizeof(byte)).status ==
                ByteStreamIoStatus::Failed,
        "invalid WinSocket did not fail finite I/O");
    invalidStream.Stop();
    invalidStream.Stop();
    Check(
        invalidStream.IsStopped(),
        "invalid WinSocket stop was not idempotent");
}

struct BlockedReadContext final {
    WinSocketByteStream* stream = nullptr;
    HANDLE entered = nullptr;
    HANDLE completed = nullptr;
    ByteStreamIoResult result{};
};

DWORD WINAPI BlockedRead(void* contextValue) noexcept {
    auto* context = static_cast<BlockedReadContext*>(contextValue);
    std::uint8_t byte = 0;
    SetEvent(context->entered);
    context->result = context->stream->Read(&byte, sizeof(byte));
    SetEvent(context->completed);
    return ERROR_SUCCESS;
}

void RunBlockedStopCheck() {
    SocketHandle owned;
    SocketHandle peer;
    Check(
        CreateConnectedSockets(&owned, &peer),
        "WinSocket blocked-read pair did not connect");
    WinSocketByteStream stream(static_cast<SocketHandle&&>(owned));
    HANDLE entered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE completed = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    BlockedReadContext context{&stream, entered, completed, {}};
    HANDLE worker = CreateThread(
        nullptr,
        0,
        BlockedRead,
        &context,
        0,
        nullptr);
    Check(
        worker != nullptr &&
            WaitForSingleObject(entered, 5000) == WAIT_OBJECT_0,
        "WinSocket blocked reader did not start");
    Check(
        WaitForSingleObject(completed, 0) == WAIT_TIMEOUT,
        "WinSocket read was not blocked before Stop");

    stream.Stop();
    stream.Stop();
    Check(
        WaitForSingleObject(completed, 5000) == WAIT_OBJECT_0,
        "WinSocket Stop did not unblock recv");
    Check(
        context.result.status == ByteStreamIoStatus::Failed &&
            context.result.bytesTransferred == 0 &&
            stream.IsStopped(),
        "WinSocket stopped read did not fail finitely");
    std::uint8_t byte = 0;
    Check(
        stream.Read(&byte, 1).status == ByteStreamIoStatus::Failed &&
            stream.Write(&byte, 1).status == ByteStreamIoStatus::Failed,
        "WinSocket accepted I/O after Stop");

    if (worker != nullptr) {
        static_cast<void>(WaitForSingleObject(worker, 5000));
        CloseHandle(worker);
    }
    if (entered != nullptr) {
        CloseHandle(entered);
    }
    if (completed != nullptr) {
        CloseHandle(completed);
    }
}

void RunRepeatedLifecycleCheck() {
    for (int iteration = 0; iteration < 16; ++iteration) {
        SocketHandle owned;
        SocketHandle peer;
        Check(
            CreateConnectedSockets(&owned, &peer),
            "repeated WinSocket pair did not connect");
        WinSocketByteStream stream(static_cast<SocketHandle&&>(owned));
        const std::vector<std::uint8_t> sent = {
            0xA5,
            static_cast<std::uint8_t>(iteration),
            static_cast<std::uint8_t>(iteration ^ 0x5A),
            0xC3};
        Check(
            SendExactly(peer.Get(), sent),
            "repeated WinSocket peer send failed");
        std::vector<std::uint8_t> received(sent.size());
        Check(
            ReadExactly(&stream, &received) && received == sent,
            "repeated WinSocket read changed bytes");
        stream.Stop();
        stream.Stop();
    }
}

} // namespace

int RunWinSocketByteStreamTests() {
    Failures = 0;
    if (!EnsureWinSock()) {
        std::fprintf(stderr, "FAIL: WinSock initialization failed\n");
        return 1;
    }

    RunExactAndBoundedIoCheck();
    RunFullPumpCompatibilityCheck();
    RunEofAndInvalidCheck();
    RunBlockedStopCheck();
    RunRepeatedLifecycleCheck();
    return Failures;
}
