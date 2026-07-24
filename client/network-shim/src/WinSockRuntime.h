#pragma once

namespace godswar::network {

// Initializes WinSock once per process. This must never be called from DllMain.
bool EnsureWinSock() noexcept;

} // namespace godswar::network
