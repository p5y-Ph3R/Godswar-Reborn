#pragma once

#include "SocketHandle.h"

#include <cstdint>

namespace godswar::network {

struct LoopbackConnectionTuple final {
    std::uint32_t serverAddress = 0;
    std::uint16_t serverPort = 0;
    std::uint32_t peerAddress = 0;
    std::uint16_t peerPort = 0;
};

struct LoopbackTcpOwnerEntry final {
    std::uint32_t localAddress = 0;
    std::uint16_t localPort = 0;
    std::uint32_t remoteAddress = 0;
    std::uint16_t remotePort = 0;
    DWORD state = 0;
    DWORD processId = 0;
};

bool IsMatchingLoopbackPeerOwner(
    const LoopbackConnectionTuple& connection,
    const LoopbackTcpOwnerEntry& entry,
    DWORD expectedProcessId) noexcept;

// The reverse client-side TCP tuple must belong to this process in the kernel
// owner table; merely connecting from 127.0.0.1 is insufficient.
bool IsAcceptedLoopbackPeerOwnedByCurrentProcess(
    SOCKET acceptedSocket) noexcept;

} // namespace godswar::network
