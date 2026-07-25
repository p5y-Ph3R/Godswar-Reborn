#include "LoopbackPeerOwner.h"

#include <WS2tcpip.h>
#include <iphlpapi.h>

#include <cstddef>
#include <cstdint>
#include <limits>

#pragma comment(lib, "iphlpapi.lib")

namespace godswar::network {
namespace {

constexpr DWORD MaximumTcpOwnerTableBytes = 1024U * 1024U;
constexpr unsigned MaximumTcpOwnerSnapshotAttempts = 3;

bool ReadConnectionTuple(
    SOCKET socket,
    LoopbackConnectionTuple* connection) noexcept {
    if (socket == INVALID_SOCKET || connection == nullptr) {
        return false;
    }

    sockaddr_in server{};
    sockaddr_in peer{};
    int serverBytes = sizeof(server);
    int peerBytes = sizeof(peer);
    if (getsockname(
            socket,
            reinterpret_cast<sockaddr*>(&server),
            &serverBytes) == SOCKET_ERROR ||
        getpeername(
            socket,
            reinterpret_cast<sockaddr*>(&peer),
            &peerBytes) == SOCKET_ERROR ||
        server.sin_family != AF_INET ||
        peer.sin_family != AF_INET ||
        ntohl(server.sin_addr.s_addr) != INADDR_LOOPBACK ||
        ntohl(peer.sin_addr.s_addr) != INADDR_LOOPBACK) {
        return false;
    }

    *connection = LoopbackConnectionTuple{
        ntohl(server.sin_addr.s_addr),
        ntohs(server.sin_port),
        ntohl(peer.sin_addr.s_addr),
        ntohs(peer.sin_port),
    };
    return connection->serverPort != 0 &&
        connection->peerPort != 0;
}

LoopbackTcpOwnerEntry ConvertRow(
    const MIB_TCPROW_OWNER_PID& row) noexcept {
    return LoopbackTcpOwnerEntry{
        ntohl(row.dwLocalAddr),
        ntohs(static_cast<u_short>(row.dwLocalPort)),
        ntohl(row.dwRemoteAddr),
        ntohs(static_cast<u_short>(row.dwRemotePort)),
        row.dwState,
        row.dwOwningPid,
    };
}

} // namespace

bool IsMatchingLoopbackPeerOwner(
    const LoopbackConnectionTuple& connection,
    const LoopbackTcpOwnerEntry& entry,
    DWORD expectedProcessId) noexcept {
    return expectedProcessId != 0 &&
        connection.serverAddress == INADDR_LOOPBACK &&
        connection.peerAddress == INADDR_LOOPBACK &&
        connection.serverPort != 0 &&
        connection.peerPort != 0 &&
        entry.localAddress == connection.peerAddress &&
        entry.localPort == connection.peerPort &&
        entry.remoteAddress == connection.serverAddress &&
        entry.remotePort == connection.serverPort &&
        entry.state == MIB_TCP_STATE_ESTAB &&
        entry.processId == expectedProcessId;
}

bool IsAcceptedLoopbackPeerOwnedByCurrentProcess(
    SOCKET acceptedSocket) noexcept {
    LoopbackConnectionTuple connection{};
    if (!ReadConnectionTuple(acceptedSocket, &connection)) {
        return false;
    }

    for (unsigned attempt = 0;
         attempt < MaximumTcpOwnerSnapshotAttempts;
         ++attempt) {
        DWORD bytes = 0;
        if (GetExtendedTcpTable(
                nullptr,
                &bytes,
                FALSE,
                AF_INET,
                TCP_TABLE_OWNER_PID_ALL,
                0) != ERROR_INSUFFICIENT_BUFFER ||
            bytes < sizeof(DWORD) ||
            bytes > MaximumTcpOwnerTableBytes) {
            return false;
        }

        void* raw = HeapAlloc(
            GetProcessHeap(),
            HEAP_ZERO_MEMORY,
            bytes);
        if (raw == nullptr) {
            return false;
        }

        DWORD actualBytes = bytes;
        const DWORD queried = GetExtendedTcpTable(
            raw,
            &actualBytes,
            FALSE,
            AF_INET,
            TCP_TABLE_OWNER_PID_ALL,
            0);
        bool owned = false;
        if (queried == NO_ERROR) {
            const auto* table =
                static_cast<const MIB_TCPTABLE_OWNER_PID*>(raw);
            constexpr std::size_t HeaderBytes =
                offsetof(MIB_TCPTABLE_OWNER_PID, table);
            const auto count =
                static_cast<std::size_t>(table->dwNumEntries);
            const bool bounded =
                count <=
                    ((std::numeric_limits<std::size_t>::max)() -
                        HeaderBytes) /
                        sizeof(MIB_TCPROW_OWNER_PID) &&
                HeaderBytes +
                        count * sizeof(MIB_TCPROW_OWNER_PID) <=
                    actualBytes;
            unsigned matches = 0;
            if (bounded) {
                for (std::size_t index = 0;
                     index < count;
                     ++index) {
                    if (IsMatchingLoopbackPeerOwner(
                            connection,
                            ConvertRow(table->table[index]),
                            GetCurrentProcessId())) {
                        ++matches;
                    }
                }
            }
            owned = matches == 1;
        }

        SecureZeroMemory(raw, bytes);
        HeapFree(GetProcessHeap(), 0, raw);
        if (queried == NO_ERROR) {
            return owned;
        }
        if (queried != ERROR_INSUFFICIENT_BUFFER) {
            return false;
        }
    }
    return false;
}

} // namespace godswar::network
