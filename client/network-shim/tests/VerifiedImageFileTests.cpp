#include "VerifiedImageFileTests.h"

#include "../src/VerifiedImageFile.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::VerifiedImageFile;

constexpr char FixtureBytes[] = "verified-image";
constexpr std::uint8_t FixtureSha256[] = {
    0x5C, 0xCC, 0x7B, 0x74, 0xFB, 0x41, 0x79, 0xB8,
    0xBB, 0x08, 0xFE, 0x2E, 0x45, 0x8B, 0x10, 0x21,
    0x7E, 0x70, 0x7A, 0x7A, 0x0F, 0x25, 0x9E, 0x31,
    0xD5, 0x57, 0xDB, 0x07, 0x42, 0x5C, 0x82, 0x3C,
};

int Failures = 0;

void Check(bool condition, const char* message) noexcept {
    if (condition) {
        return;
    }
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++Failures;
}

bool NewTemporaryPath(
    const wchar_t* prefix,
    wchar_t* path,
    std::size_t pathCapacity) noexcept {
    if (prefix == nullptr ||
        path == nullptr ||
        pathCapacity < MAX_PATH) {
        return false;
    }

    wchar_t root[MAX_PATH]{};
    const DWORD rootLength = GetTempPathW(MAX_PATH, root);
    return rootLength > 0 &&
        rootLength < MAX_PATH &&
        GetTempFileNameW(root, prefix, 0, path) != 0;
}

bool WriteFixture(const wchar_t* path) noexcept {
    const HANDLE file = CreateFileW(
        path,
        GENERIC_WRITE,
        0,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }

    DWORD written = 0;
    const bool succeeded = WriteFile(
        file,
        FixtureBytes,
        static_cast<DWORD>(sizeof(FixtureBytes) - 1),
        &written,
        nullptr) != FALSE &&
        written == sizeof(FixtureBytes) - 1 &&
        FlushFileBuffers(file) != FALSE;
    CloseHandle(file);
    return succeeded;
}

void CheckSharingAndReplacement() noexcept {
    wchar_t verifiedPath[MAX_PATH]{};
    wchar_t replacementPath[MAX_PATH]{};
    Check(
        NewTemporaryPath(L"gvi", verifiedPath, MAX_PATH) &&
            WriteFixture(verifiedPath),
        "verified-image fixture creation failed");
    Check(
        NewTemporaryPath(L"gvr", replacementPath, MAX_PATH) &&
            WriteFixture(replacementPath),
        "verified-image replacement fixture creation failed");
    if (verifiedPath[0] == L'\0' ||
        replacementPath[0] == L'\0') {
        return;
    }

    {
        VerifiedImageFile verified;
        Check(
            verified.OpenAndVerify(
                verifiedPath,
                FixtureSha256,
                sizeof(FixtureSha256)),
            "known verified image was rejected");
        Check(
            verified.IsOpen(),
            "verified image did not retain its file handle");

        const HANDLE reader = CreateFileW(
            verifiedPath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        Check(
            reader != INVALID_HANDLE_VALUE,
            "verified image blocked a compatible reader");
        if (reader != INVALID_HANDLE_VALUE) {
            CloseHandle(reader);
        }

        SetLastError(ERROR_SUCCESS);
        const HANDLE writer = CreateFileW(
            verifiedPath,
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        Check(
            writer == INVALID_HANDLE_VALUE &&
                GetLastError() == ERROR_SHARING_VIOLATION,
            "verified image permitted a concurrent writer");
        if (writer != INVALID_HANDLE_VALUE) {
            CloseHandle(writer);
        }

        SetLastError(ERROR_SUCCESS);
        const HANDLE deleter = CreateFileW(
            verifiedPath,
            DELETE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        Check(
            deleter == INVALID_HANDLE_VALUE &&
                GetLastError() == ERROR_SHARING_VIOLATION,
            "verified image permitted a concurrent delete handle");
        if (deleter != INVALID_HANDLE_VALUE) {
            CloseHandle(deleter);
        }

        Check(
            DeleteFileW(verifiedPath) == FALSE,
            "verified image permitted deletion while held");
        Check(
            MoveFileExW(
                replacementPath,
                verifiedPath,
                MOVEFILE_REPLACE_EXISTING |
                    MOVEFILE_WRITE_THROUGH) == FALSE,
            "verified image permitted path replacement while held");
    }

    Check(
        MoveFileExW(
            replacementPath,
            verifiedPath,
            MOVEFILE_REPLACE_EXISTING |
                MOVEFILE_WRITE_THROUGH) != FALSE,
        "verified image did not release replacement protection");
    static_cast<void>(DeleteFileW(verifiedPath));
    static_cast<void>(DeleteFileW(replacementPath));
}

void CheckFailureReleasesHandle() noexcept {
    wchar_t path[MAX_PATH]{};
    Check(
        NewTemporaryPath(L"gvf", path, MAX_PATH) &&
            WriteFixture(path),
        "verified-image mismatch fixture creation failed");
    if (path[0] == L'\0') {
        return;
    }

    std::uint8_t wrongHash[sizeof(FixtureSha256)]{};
    for (std::size_t index = 0;
         index < sizeof(wrongHash);
         ++index) {
        wrongHash[index] = FixtureSha256[index];
    }
    wrongHash[0] ^= 0xFF;

    VerifiedImageFile rejected;
    SetLastError(ERROR_SUCCESS);
    Check(
        !rejected.OpenAndVerify(
            path,
            wrongHash,
            sizeof(wrongHash)) &&
            !rejected.IsOpen() &&
            GetLastError() == ERROR_INVALID_DATA,
        "hash mismatch did not fail closed");

    const HANDLE writer = CreateFileW(
        path,
        GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    Check(
        writer != INVALID_HANDLE_VALUE,
        "failed verification leaked its restrictive handle");
    if (writer != INVALID_HANDLE_VALUE) {
        CloseHandle(writer);
    }
    static_cast<void>(DeleteFileW(path));
}

void CheckReparsePointRejected() noexcept {
    wchar_t target[MAX_PATH]{};
    wchar_t link[MAX_PATH]{};
    if (!NewTemporaryPath(L"gvt", target, MAX_PATH) ||
        !WriteFixture(target) ||
        !NewTemporaryPath(L"gvl", link, MAX_PATH)) {
        Check(false, "reparse fixture creation failed");
        return;
    }
    static_cast<void>(DeleteFileW(link));

    constexpr DWORD AllowUnprivilegedCreate = 0x2;
    bool linked = CreateSymbolicLinkW(
        link,
        target,
        AllowUnprivilegedCreate) != FALSE;
    if (!linked && GetLastError() == ERROR_INVALID_PARAMETER) {
        linked = CreateSymbolicLinkW(link, target, 0) != FALSE;
    }
    if (linked) {
        VerifiedImageFile rejected;
        Check(
            !rejected.OpenAndVerify(
                link,
                FixtureSha256,
                sizeof(FixtureSha256)),
            "verified image followed a final reparse point");
        static_cast<void>(DeleteFileW(link));
    }

    static_cast<void>(DeleteFileW(target));
}

} // namespace

int RunVerifiedImageFileTests() {
    CheckSharingAndReplacement();
    CheckFailureReleasesHandle();
    CheckReparsePointRejected();
    return Failures;
}
