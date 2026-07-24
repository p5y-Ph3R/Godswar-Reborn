#include "WinSockRuntime.h"

#include <WinSock2.h>
#include <Windows.h>

namespace {

INIT_ONCE WinSockInitOnce = INIT_ONCE_STATIC_INIT;
int WinSockInitializationError = 0;

BOOL CALLBACK InitializeWinSock(
    PINIT_ONCE,
    PVOID,
    PVOID*) noexcept {
    WSADATA data{};
    const auto result = WSAStartup(MAKEWORD(2, 2), &data);
    if (result != 0) {
        WinSockInitializationError = result;
        return TRUE;
    }

    if (LOBYTE(data.wVersion) != 2 ||
        HIBYTE(data.wVersion) != 2) {
        WinSockInitializationError = WSAVERNOTSUPPORTED;
        static_cast<void>(WSACleanup());
    }

    // WinSock intentionally remains initialized for the process lifetime.
    // Cleanup under the loader lock would race active client objects.
    return TRUE;
}

} // namespace

namespace godswar::network {

bool EnsureWinSock() noexcept {
    if (!InitOnceExecuteOnce(
            &WinSockInitOnce,
            InitializeWinSock,
            nullptr,
            nullptr)) {
        auto error = static_cast<int>(GetLastError());
        if (error == ERROR_SUCCESS) {
            error = WSASYSCALLFAILURE;
        }
        WSASetLastError(error);
        return false;
    }

    if (WinSockInitializationError != 0) {
        WSASetLastError(WinSockInitializationError);
        SetLastError(static_cast<DWORD>(WinSockInitializationError));
        return false;
    }

    return true;
}

} // namespace godswar::network
