#pragma once

#include "SocketHandle.h"

#include <Windows.h>

#include <cstdint>

namespace godswar::network {

enum class ExternalTcpConnectFailure : std::uint8_t {
    None = 0,
    InvalidArgument,
    WinSockInitialization,
    ResolutionFailed,
    ResolutionDeadline,
    NoSupportedAddress,
    SocketCreate,
    ConnectFailed,
    ConnectDeadline,
};

struct ExternalTcpConnectSnapshot final {
    ExternalTcpConnectFailure failure =
        ExternalTcpConnectFailure::None;
    unsigned resolvedAddresses = 0;
    unsigned attemptedAddresses = 0;
    int nativeError = 0;
};

// Resolves and connects within one absolute budget. The returned socket is
// nonblocking and ready to transfer to SchannelClientStream.
bool ConnectExternalTcp(
    const wchar_t* tlsDnsHost,
    std::uint16_t port,
    DWORD timeoutMilliseconds,
    SocketHandle* connectedSocket,
    ExternalTcpConnectSnapshot* snapshot) noexcept;

} // namespace godswar::network
