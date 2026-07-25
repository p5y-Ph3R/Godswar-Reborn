#include "ExternalTcpConnector.h"

#include "SchannelClientStream.h"
#include "WinSockRuntime.h"

#include <WS2tcpip.h>

#include <algorithm>
#include <cwchar>
#include <new>

namespace godswar::network {
namespace {

constexpr DWORD MaximumConnectBudgetMilliseconds = 60'000;
constexpr DWORD ResolverCancellationGraceMilliseconds = 1'000;
constexpr unsigned MaximumResolvedAddresses = 16;
volatile LONG ResolutionInProgress = 0;

struct ResolutionOperation final {
    OVERLAPPED overlapped{};
    HANDLE cancellation = nullptr;
    PADDRINFOEXW addresses = nullptr;
};

DWORD RemainingMilliseconds(ULONGLONG deadline) noexcept {
    const ULONGLONG now = GetTickCount64();
    if (now >= deadline) {
        return 0;
    }
    const ULONGLONG remaining = deadline - now;
    return static_cast<DWORD>(
        (std::min)(
            remaining,
            static_cast<ULONGLONG>(INFINITE - 1)));
}

void ReleaseResolution(
    ResolutionOperation* operation,
    bool releaseGuard) noexcept {
    if (operation != nullptr) {
        if (operation->addresses != nullptr) {
            FreeAddrInfoExW(operation->addresses);
        }
        if (operation->overlapped.hEvent != nullptr) {
            CloseHandle(operation->overlapped.hEvent);
        }
        delete operation;
    }
    if (releaseGuard) {
        InterlockedExchange(&ResolutionInProgress, 0);
    }
}

bool ResolveAddresses(
    const wchar_t* host,
    const wchar_t* service,
    const ADDRINFOEXW& hints,
    ULONGLONG deadline,
    PADDRINFOEXW* addresses,
    ExternalTcpConnectSnapshot* snapshot) noexcept {
    if (addresses == nullptr ||
        snapshot == nullptr ||
        InterlockedCompareExchange(
            &ResolutionInProgress,
            1,
            0) != 0) {
        if (snapshot != nullptr) {
            snapshot->failure =
                ExternalTcpConnectFailure::ResolutionFailed;
            snapshot->nativeError = WSAEINPROGRESS;
        }
        return false;
    }

    auto* operation =
        new (std::nothrow) ResolutionOperation();
    if (operation == nullptr) {
        InterlockedExchange(&ResolutionInProgress, 0);
        snapshot->failure =
            ExternalTcpConnectFailure::ResolutionFailed;
        snapshot->nativeError = WSA_NOT_ENOUGH_MEMORY;
        return false;
    }
    operation->overlapped.hEvent =
        CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (operation->overlapped.hEvent == nullptr) {
        snapshot->failure =
            ExternalTcpConnectFailure::ResolutionFailed;
        snapshot->nativeError =
            static_cast<int>(GetLastError());
        ReleaseResolution(operation, true);
        return false;
    }

    int result = GetAddrInfoExW(
        host,
        service,
        NS_ALL,
        nullptr,
        &hints,
        &operation->addresses,
        nullptr,
        &operation->overlapped,
        nullptr,
        &operation->cancellation);
    if (result == WSA_IO_PENDING) {
        const DWORD wait = WaitForSingleObject(
            operation->overlapped.hEvent,
            RemainingMilliseconds(deadline));
        if (wait == WAIT_OBJECT_0) {
            result = GetAddrInfoExOverlappedResult(
                &operation->overlapped);
        } else if (wait == WAIT_TIMEOUT) {
            static_cast<void>(
                GetAddrInfoExCancel(&operation->cancellation));
            const DWORD cancellationWait = WaitForSingleObject(
                operation->overlapped.hEvent,
                ResolverCancellationGraceMilliseconds);
            if (cancellationWait != WAIT_OBJECT_0) {
                // The still-owned operation can complete into this one
                // intentionally abandoned allocation. Keeping the global
                // guard set bounds the failure to one allocation and rejects
                // every later resolution attempt in this process.
                snapshot->failure =
                    ExternalTcpConnectFailure::ResolutionDeadline;
                snapshot->nativeError = WSAETIMEDOUT;
                return false;
            }
            static_cast<void>(
                GetAddrInfoExOverlappedResult(
                    &operation->overlapped));
            snapshot->failure =
                ExternalTcpConnectFailure::ResolutionDeadline;
            snapshot->nativeError = WSAETIMEDOUT;
            ReleaseResolution(operation, true);
            return false;
        } else {
            const DWORD waitError =
                wait == WAIT_FAILED
                    ? GetLastError()
                    : ERROR_OPERATION_ABORTED;
            static_cast<void>(
                GetAddrInfoExCancel(&operation->cancellation));
            const DWORD cancellationWait = WaitForSingleObject(
                operation->overlapped.hEvent,
                ResolverCancellationGraceMilliseconds);
            if (cancellationWait != WAIT_OBJECT_0) {
                // Completion is still allowed to reference this OVERLAPPED.
                // Preserve the single bounded allocation and keep the guard
                // closed instead of risking a use-after-free.
                snapshot->failure =
                    ExternalTcpConnectFailure::ResolutionFailed;
                snapshot->nativeError =
                    static_cast<int>(waitError);
                return false;
            }
            static_cast<void>(
                GetAddrInfoExOverlappedResult(
                    &operation->overlapped));
            snapshot->failure =
                ExternalTcpConnectFailure::ResolutionFailed;
            snapshot->nativeError =
                static_cast<int>(waitError);
            ReleaseResolution(operation, true);
            return false;
        }
    }

    if (result != 0 || operation->addresses == nullptr) {
        snapshot->failure =
            RemainingMilliseconds(deadline) == 0 ||
                result == WSAETIMEDOUT
            ? ExternalTcpConnectFailure::ResolutionDeadline
            : ExternalTcpConnectFailure::ResolutionFailed;
        snapshot->nativeError = result;
        ReleaseResolution(operation, true);
        return false;
    }

    *addresses = operation->addresses;
    operation->addresses = nullptr;
    ReleaseResolution(operation, true);
    return true;
}

bool IsSupportedAddress(const ADDRINFOEXW* address) noexcept {
    return address != nullptr &&
        address->ai_addr != nullptr &&
        (address->ai_family == AF_INET ||
            address->ai_family == AF_INET6) &&
        address->ai_socktype == SOCK_STREAM &&
        address->ai_protocol == IPPROTO_TCP &&
        address->ai_addrlen >=
            (address->ai_family == AF_INET
                ? sizeof(sockaddr_in)
                : sizeof(sockaddr_in6));
}

bool WaitForConnected(
    SOCKET socketValue,
    ULONGLONG deadline) noexcept {
    for (;;) {
        const DWORD remaining = RemainingMilliseconds(deadline);
        if (remaining == 0) {
            return false;
        }

        fd_set writable;
        fd_set failed;
        FD_ZERO(&writable);
        FD_ZERO(&failed);
        FD_SET(socketValue, &writable);
        FD_SET(socketValue, &failed);
        timeval timeout{};
        timeout.tv_sec = remaining / 1000;
        timeout.tv_usec = static_cast<long>(
            (remaining % 1000) * 1000);
        const int selected = select(
            0,
            nullptr,
            &writable,
            &failed,
            &timeout);
        if (selected == 0) {
            return false;
        }
        if (selected == SOCKET_ERROR) {
            return false;
        }

        int socketError = 0;
        int socketErrorBytes = sizeof(socketError);
        return getsockopt(
                   socketValue,
                   SOL_SOCKET,
                   SO_ERROR,
                   reinterpret_cast<char*>(&socketError),
                   &socketErrorBytes) != SOCKET_ERROR &&
            socketError == 0 &&
            FD_ISSET(socketValue, &writable);
    }
}

bool TryConnectAddress(
    const ADDRINFOEXW* address,
    ULONGLONG deadline,
    SocketHandle* connectedSocket,
    bool* deadlineExpired) noexcept {
    if (!IsSupportedAddress(address) ||
        connectedSocket == nullptr ||
        deadlineExpired == nullptr) {
        return false;
    }

    SocketHandle candidate(socket(
        address->ai_family,
        address->ai_socktype,
        address->ai_protocol));
    if (!candidate.IsValid()) {
        return false;
    }

    u_long nonblocking = 1;
    const BOOL noDelay = TRUE;
    if (ioctlsocket(
            candidate.Get(),
            FIONBIO,
            &nonblocking) == SOCKET_ERROR ||
        setsockopt(
            candidate.Get(),
            IPPROTO_TCP,
            TCP_NODELAY,
            reinterpret_cast<const char*>(&noDelay),
            sizeof(noDelay)) == SOCKET_ERROR) {
        return false;
    }

    if (RemainingMilliseconds(deadline) == 0) {
        *deadlineExpired = true;
        return false;
    }

    const int result = connect(
        candidate.Get(),
        address->ai_addr,
        static_cast<int>(address->ai_addrlen));
    if (result == 0) {
        *connectedSocket =
            static_cast<SocketHandle&&>(candidate);
        return true;
    }

    const int error = WSAGetLastError();
    if (error != WSAEWOULDBLOCK &&
        error != WSAEINPROGRESS &&
        error != WSAEALREADY) {
        return false;
    }
    if (!WaitForConnected(candidate.Get(), deadline)) {
        *deadlineExpired =
            RemainingMilliseconds(deadline) == 0;
        return false;
    }

    *connectedSocket = static_cast<SocketHandle&&>(candidate);
    return true;
}

} // namespace

bool ConnectExternalTcp(
    const wchar_t* tlsDnsHost,
    std::uint16_t port,
    DWORD timeoutMilliseconds,
    SocketHandle* connectedSocket,
    ExternalTcpConnectSnapshot* snapshot) noexcept {
    ExternalTcpConnectSnapshot local{};
    if (connectedSocket != nullptr) {
        connectedSocket->Reset();
    }
    if (snapshot != nullptr) {
        *snapshot = local;
    }
    if (!IsValidSchannelTargetName(tlsDnsHost) ||
        port == 0 ||
        timeoutMilliseconds == 0 ||
        timeoutMilliseconds > MaximumConnectBudgetMilliseconds ||
        connectedSocket == nullptr ||
        snapshot == nullptr) {
        local.failure = ExternalTcpConnectFailure::InvalidArgument;
        if (snapshot != nullptr) {
            *snapshot = local;
        }
        return false;
    }
    if (!EnsureWinSock()) {
        local.failure =
            ExternalTcpConnectFailure::WinSockInitialization;
        *snapshot = local;
        return false;
    }

    const ULONGLONG deadline =
        GetTickCount64() + timeoutMilliseconds;
    wchar_t service[6]{};
    if (_snwprintf_s(
            service,
            sizeof(service) / sizeof(service[0]),
            _TRUNCATE,
            L"%hu",
            port) < 1) {
        local.failure = ExternalTcpConnectFailure::InvalidArgument;
        *snapshot = local;
        return false;
    }

    ADDRINFOEXW hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    hints.ai_flags = AI_ADDRCONFIG | AI_NUMERICSERV;

    PADDRINFOEXW addresses = nullptr;
    if (!ResolveAddresses(
        tlsDnsHost,
        service,
        hints,
        deadline,
        &addresses,
        &local)) {
        *snapshot = local;
        return false;
    }

    for (auto* address = addresses;
         address != nullptr &&
         local.resolvedAddresses < MaximumResolvedAddresses;
         address = address->ai_next) {
        if (IsSupportedAddress(address)) {
            ++local.resolvedAddresses;
        }
    }
    if (local.resolvedAddresses == 0) {
        local.failure =
            ExternalTcpConnectFailure::NoSupportedAddress;
        FreeAddrInfoExW(addresses);
        *snapshot = local;
        return false;
    }

    bool deadlineExpired = false;
    unsigned remainingAddresses = local.resolvedAddresses;
    for (auto* address = addresses;
         address != nullptr &&
         local.attemptedAddresses < local.resolvedAddresses;
         address = address->ai_next) {
        if (!IsSupportedAddress(address)) {
            continue;
        }
        ++local.attemptedAddresses;
        const DWORD remaining = RemainingMilliseconds(deadline);
        if (remaining == 0) {
            deadlineExpired = true;
            break;
        }
        const DWORD attemptBudget =
            (std::max)(static_cast<DWORD>(1),
                remaining / remainingAddresses);
        const ULONGLONG attemptDeadline =
            GetTickCount64() + attemptBudget;
        if (TryConnectAddress(
                address,
                attemptDeadline,
                connectedSocket,
                &deadlineExpired)) {
            FreeAddrInfoExW(addresses);
            local.failure = ExternalTcpConnectFailure::None;
            *snapshot = local;
            return true;
        }
        --remainingAddresses;
    }

    FreeAddrInfoExW(addresses);
    local.failure = deadlineExpired ||
            RemainingMilliseconds(deadline) == 0
        ? ExternalTcpConnectFailure::ConnectDeadline
        : ExternalTcpConnectFailure::ConnectFailed;
    *snapshot = local;
    return false;
}

} // namespace godswar::network
