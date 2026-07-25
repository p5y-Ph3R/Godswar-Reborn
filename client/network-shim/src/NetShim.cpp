#include "LegacyModule.h"
#include "NetClientProxy.h"
#include "SecureClientRuntime.h"

#include <Windows.h>

namespace {

HMODULE ShimModule = nullptr;
INIT_ONCE LegacyInitOnce = INIT_ONCE_STATIC_INIT;
godswar::network::LegacyFactories Factories{};
DWORD InitializationError = ERROR_SUCCESS;

BOOL CALLBACK InitializeLegacy(
    PINIT_ONCE,
    PVOID,
    PVOID*) noexcept {
    if (!godswar::network::LoadVerifiedLegacyModule(
            ShimModule,
            &Factories)) {
        InitializationError = GetLastError();
        if (InitializationError == ERROR_SUCCESS) {
            InitializationError = ERROR_DLL_INIT_FAILED;
        }
    }

    // Record failure as a stable process-lifetime result. Loading arbitrary
    // replacement bytes after an initialization failure is deliberately
    // unsupported.
    return TRUE;
}

bool EnsureLegacyInitialized() noexcept {
    if (!InitOnceExecuteOnce(
            &LegacyInitOnce,
            InitializeLegacy,
            nullptr,
            nullptr)) {
        return false;
    }

    if (Factories.module == nullptr) {
        SetLastError(InitializationError);
        return false;
    }

    return true;
}

} // namespace

extern "C" void* __cdecl NetClientCreate() {
    if (!godswar::network::
            EnsureProcessSecureClientRuntimeInitialized(ShimModule)) {
        SetLastError(ERROR_ACCESS_DENIED);
        return nullptr;
    }
    if (!EnsureLegacyInitialized()) {
        return nullptr;
    }

    auto* legacyClient =
        static_cast<godswar::network::ILegacyNetClient*>(
            Factories.createClient());
    return godswar::network::NetClientProxy::Create(legacyClient);
}

extern "C" void* __cdecl NetServiceCreate() {
    if (!EnsureLegacyInitialized()) {
        return nullptr;
    }

    // Origin.exe does not import this factory. It is forwarded unchanged to
    // retain the complete stock export contract while its separate service
    // interface remains outside the audited client path.
    return Factories.createService();
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        ShimModule = instance;
        DisableThreadLibraryCalls(instance);
    }

    return TRUE;
}
