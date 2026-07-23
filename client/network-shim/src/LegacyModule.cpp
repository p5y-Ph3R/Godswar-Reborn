#include "LegacyModule.h"

#include <wincrypt.h>

#include <cstddef>
#include <cstdint>

#pragma comment(lib, "advapi32.lib")

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

bool HashFileSha256(
    const wchar_t* path,
    std::uint8_t* hash,
    DWORD hashSize) noexcept {
    const auto file = CreateFileW(
        path,
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }

    HCRYPTPROV provider = 0;
    HCRYPTHASH hashHandle = 0;
    bool succeeded = false;

    if (CryptAcquireContextW(
            &provider,
            nullptr,
            nullptr,
            PROV_RSA_AES,
            CRYPT_VERIFYCONTEXT) &&
        CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hashHandle)) {
        BYTE buffer[32 * 1024]{};
        DWORD bytesRead = 0;
        succeeded = true;

        while (true) {
            if (!ReadFile(
                    file,
                    buffer,
                    static_cast<DWORD>(sizeof(buffer)),
                    &bytesRead,
                    nullptr)) {
                succeeded = false;
                break;
            }

            if (bytesRead == 0) {
                break;
            }

            if (!CryptHashData(hashHandle, buffer, bytesRead, 0)) {
                succeeded = false;
                break;
            }
        }

        DWORD actualSize = hashSize;
        if (succeeded &&
            (!CryptGetHashParam(
                hashHandle,
                HP_HASHVAL,
                hash,
                &actualSize,
                0) ||
             actualSize != hashSize)) {
            succeeded = false;
        }

        SecureZeroMemory(buffer, sizeof(buffer));
    }

    if (hashHandle != 0) {
        CryptDestroyHash(hashHandle);
    }
    if (provider != 0) {
        CryptReleaseContext(provider, 0);
    }
    CloseHandle(file);
    return succeeded;
}

bool IsSupportedLegacy(const wchar_t* path) noexcept {
    std::uint8_t actualHash[sizeof(SupportedLegacySha256)]{};
    if (!HashFileSha256(
            path,
            actualHash,
            static_cast<DWORD>(sizeof(actualHash)))) {
        return false;
    }

    std::uint8_t difference = 0;
    for (std::size_t index = 0;
         index < sizeof(SupportedLegacySha256);
         ++index) {
        difference |=
            static_cast<std::uint8_t>(
                actualHash[index] ^ SupportedLegacySha256[index]);
    }
    SecureZeroMemory(actualHash, sizeof(actualHash));

    if (difference != 0) {
        SetLastError(ERROR_INVALID_DATA);
        return false;
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
            sizeof(legacyPath) / sizeof(legacyPath[0])) ||
        !IsSupportedLegacy(legacyPath)) {
        return false;
    }

    const auto module = LoadLibraryExW(
        legacyPath,
        nullptr,
        LOAD_WITH_ALTERED_SEARCH_PATH);
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
