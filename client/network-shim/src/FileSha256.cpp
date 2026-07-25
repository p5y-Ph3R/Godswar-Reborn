#include "FileSha256.h"

#include <Windows.h>
#include <wincrypt.h>

#pragma comment(lib, "advapi32.lib")

namespace godswar::network {

bool FileHandleMatchesSha256(
    HANDLE file,
    const std::uint8_t* expectedHash,
    std::size_t expectedHashSize) noexcept {
    constexpr DWORD Sha256Size = 32;
    if (file == nullptr ||
        file == INVALID_HANDLE_VALUE ||
        expectedHash == nullptr ||
        expectedHashSize != Sha256Size) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

    LARGE_INTEGER start{};
    if (!SetFilePointerEx(file, start, nullptr, FILE_BEGIN)) {
        return false;
    }

    HCRYPTPROV provider = 0;
    HCRYPTHASH hashHandle = 0;
    std::uint8_t actualHash[Sha256Size]{};
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

        DWORD actualSize = Sha256Size;
        if (succeeded &&
            (!CryptGetHashParam(
                hashHandle,
                HP_HASHVAL,
                actualHash,
                &actualSize,
                0) ||
             actualSize != Sha256Size)) {
            succeeded = false;
        }

        SecureZeroMemory(buffer, sizeof(buffer));
    }

    std::uint8_t difference = 0;
    if (succeeded) {
        for (DWORD index = 0; index < Sha256Size; ++index) {
            difference |= static_cast<std::uint8_t>(
                actualHash[index] ^ expectedHash[index]);
        }
        if (difference != 0) {
            SetLastError(ERROR_INVALID_DATA);
            succeeded = false;
        }
    }

    SecureZeroMemory(actualHash, sizeof(actualHash));
    if (hashHandle != 0) {
        CryptDestroyHash(hashHandle);
    }
    if (provider != 0) {
        CryptReleaseContext(provider, 0);
    }
    return succeeded;
}

bool FileMatchesSha256(
    const wchar_t* path,
    const std::uint8_t* expectedHash,
    std::size_t expectedHashSize) noexcept {
    if (path == nullptr) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

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

    const bool succeeded = FileHandleMatchesSha256(
        file,
        expectedHash,
        expectedHashSize);
    const DWORD error = succeeded ? ERROR_SUCCESS : GetLastError();
    CloseHandle(file);
    if (!succeeded) {
        SetLastError(error);
    }
    return succeeded;
}

} // namespace godswar::network
