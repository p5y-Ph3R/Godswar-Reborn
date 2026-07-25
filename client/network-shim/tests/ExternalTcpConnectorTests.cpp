#include "ExternalTcpConnectorTests.h"

#include "../src/ExternalTcpConnector.h"
#include "../src/WinSockRuntime.h"

#include <WS2tcpip.h>

#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::ConnectExternalTcp;
using godswar::network::EnsureWinSock;
using godswar::network::ExternalTcpConnectFailure;
using godswar::network::ExternalTcpConnectSnapshot;
using godswar::network::SocketHandle;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

void CheckInvalidArguments() {
    SocketHandle socket;
    ExternalTcpConnectSnapshot snapshot{};
    Check(
        !ConnectExternalTcp(
            L"127.0.0.1",
            443,
            100,
            &socket,
            &snapshot) &&
            snapshot.failure ==
                ExternalTcpConnectFailure::InvalidArgument,
        "numeric TLS target was accepted");
    Check(
        !ConnectExternalTcp(
            L"localhost",
            0,
            100,
            &socket,
            &snapshot) &&
            !ConnectExternalTcp(
                L"localhost",
                443,
                0,
                &socket,
                &snapshot),
        "zero connector port/deadline was accepted");
}

void CheckLoopbackConnect() {
    if (!EnsureWinSock()) {
        Check(false, "WinSock initialization failed");
        return;
    }

    SocketHandle listener(socket(
        AF_INET,
        SOCK_STREAM,
        IPPROTO_TCP));
    Check(listener.IsValid(), "connector listener creation failed");
    if (!listener.IsValid()) {
        return;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    Check(
        bind(
            listener.Get(),
            reinterpret_cast<const sockaddr*>(&address),
            sizeof(address)) == 0 &&
            listen(listener.Get(), 1) == 0,
        "connector listener bind/listen failed");
    int addressBytes = sizeof(address);
    Check(
        getsockname(
            listener.Get(),
            reinterpret_cast<sockaddr*>(&address),
            &addressBytes) == 0,
        "connector listener address query failed");

    SocketHandle connected;
    ExternalTcpConnectSnapshot snapshot{};
    const bool result = ConnectExternalTcp(
        L"localhost",
        ntohs(address.sin_port),
        2'000,
        &connected,
        &snapshot);
    Check(
        result &&
            connected.IsValid() &&
            snapshot.failure ==
                ExternalTcpConnectFailure::None &&
            snapshot.resolvedAddresses >= 1 &&
            snapshot.attemptedAddresses >= 1,
        "bounded localhost TCP connection failed");
    if (!result) {
        std::fprintf(
            stderr,
            "connector failure=%u error=%d resolved=%u attempted=%u\n",
            static_cast<unsigned>(snapshot.failure),
            snapshot.nativeError,
            snapshot.resolvedAddresses,
            snapshot.attemptedAddresses);
        return;
    }

    SocketHandle accepted(accept(
        listener.Get(),
        nullptr,
        nullptr));
    Check(
        accepted.IsValid(),
        "connector listener did not accept the client");
}

} // namespace

int RunExternalTcpConnectorTests() {
    Failures = 0;
    CheckInvalidArguments();
    CheckLoopbackConnect();
    return Failures;
}
