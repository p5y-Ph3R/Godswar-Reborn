#include "LegacyModule.h"
#include "VerifiedImageFile.h"

#include <cstddef>
#include <cstdint>

namespace {

constexpr wchar_t LegacyFileName[] = L"NetLegacy.dll";
constexpr std::uint8_t SupportedLegacySha256[] = {
    0x1C, 0xC3, 0xF9, 0xAA, 0xBB, 0xC3, 0x39, 0x30,
    0x0D, 0xF0, 0x67, 0x95, 0xAB, 0x22, 0xEA, 0xD1,
    0xAC, 0xC7, 0xF4, 0xCB, 0xB4, 0x7F, 0x2F, 0x2D,
    0xBF, 0x36, 0xF1, 0xCF, 0x19, 0xBC, 0xA0, 0x0C,
};

bool BuildLegacyPath(
    HMODULE shimModule,
    wchar_t* path,
    std::size_t capacity) noexcept {
    const auto length = GetModuleFileNameW(
        shimModule,
        path,
        static_cast<DWORD>(capacity));
    if (length == 0 || length >= capacity) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return false;
    }

    std::size_t separator = length;
    while (separator > 0 &&
           path[separator - 1] != L'\\' &&
           path[separator - 1] != L'/') {
        --separator;
    }

    constexpr auto legacyLength =
        (sizeof(LegacyFileName) / sizeof(LegacyFileName[0])) - 1;
    if (separator == 0 || separator + legacyLength >= capacity) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return false;
    }

    for (std::size_t index = 0; index <= legacyLength; ++index) {
        path[separator + index] = LegacyFileName[index];
    }

    return true;
}

} // namespace

namespace godswar::network {

bool LoadVerifiedLegacyModule(
    HMODULE shimModule,
    LegacyFactories* factories) noexcept {
    if (shimModule == nullptr || factories == nullptr) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

    wchar_t legacyPath[4096]{};
    if (!BuildLegacyPath(
            shimModule,
            legacyPath,
            sizeof(legacyPath) / sizeof(legacyPath[0]))) {
        return false;
    }

    // This work runs on the first exported factory call, never from DllMain.
    // Holding the verified normal-file handle without write/delete sharing
    // prevents the path from being modified or replaced between hashing and
    // image mapping.
    VerifiedImageFile verifiedLegacy;
    if (!verifiedLegacy.OpenAndVerify(
            legacyPath,
            SupportedLegacySha256,
            sizeof(SupportedLegacySha256))) {
        return false;
    }

    const auto module = LoadLibraryExW(
        legacyPath,
        nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR |
            LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (module == nullptr) {
        return false;
    }

    const auto clientByOrdinal = GetProcAddress(
        module,
        MAKEINTRESOURCEA(1));
    const auto serviceByOrdinal = GetProcAddress(
        module,
        MAKEINTRESOURCEA(2));
    const auto clientByName = GetProcAddress(module, "NetClientCreate");
    const auto serviceByName = GetProcAddress(module, "NetServiceCreate");

    if (clientByOrdinal == nullptr ||
        serviceByOrdinal == nullptr ||
        clientByOrdinal != clientByName ||
        serviceByOrdinal != serviceByName) {
        FreeLibrary(module);
        SetLastError(ERROR_PROC_NOT_FOUND);
        return false;
    }

    factories->module = module;
    factories->createClient =
        reinterpret_cast<LegacyFactory>(clientByOrdinal);
    factories->createService =
        reinterpret_cast<LegacyFactory>(serviceByOrdinal);
    return true;
}

} // namespace godswar::network
