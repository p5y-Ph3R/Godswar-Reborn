#include "VerifiedImageFile.h"

#include "FileSha256.h"

namespace godswar::network {

VerifiedImageFile::~VerifiedImageFile() noexcept {
    Close();
}

bool VerifiedImageFile::OpenAndVerify(
    const wchar_t* path,
    const std::uint8_t* expectedHash,
    std::size_t expectedHashSize) noexcept {
    if (IsOpen() ||
        path == nullptr ||
        expectedHash == nullptr) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

    const HANDLE candidate = CreateFileW(
        path,
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL |
            FILE_FLAG_OPEN_REPARSE_POINT |
            FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (candidate == INVALID_HANDLE_VALUE) {
        return false;
    }

    FILE_ATTRIBUTE_TAG_INFO attributes{};
    bool verified = GetFileInformationByHandleEx(
        candidate,
        FileAttributeTagInfo,
        &attributes,
        sizeof(attributes)) != FALSE;
    if (verified &&
        (attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
        SetLastError(ERROR_DIRECTORY);
        verified = false;
    }
    if (verified &&
        (attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
        SetLastError(ERROR_REPARSE_TAG_INVALID);
        verified = false;
    }
    if (verified) {
        verified = FileHandleMatchesSha256(
            candidate,
            expectedHash,
            expectedHashSize);
    }

    if (!verified) {
        const DWORD error = GetLastError();
        CloseHandle(candidate);
        SetLastError(
            error != ERROR_SUCCESS
                ? error
                : ERROR_INVALID_DATA);
        return false;
    }

    handle_ = candidate;
    return true;
}

bool VerifiedImageFile::IsOpen() const noexcept {
    return handle_ != nullptr &&
        handle_ != INVALID_HANDLE_VALUE;
}

void VerifiedImageFile::Close() noexcept {
    const DWORD priorError = GetLastError();
    if (IsOpen()) {
        CloseHandle(handle_);
    }
    handle_ = INVALID_HANDLE_VALUE;
    SetLastError(priorError);
}

} // namespace godswar::network
