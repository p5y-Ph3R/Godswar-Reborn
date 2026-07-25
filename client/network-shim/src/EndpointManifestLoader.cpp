#include "EndpointManifestLoader.h"

#include <Windows.h>

#include <climits>
#include <cstddef>
#include <cstdint>
#include <cwchar>

namespace godswar::network {
namespace {

class ScopedHandle final {
public:
    explicit ScopedHandle(HANDLE value = INVALID_HANDLE_VALUE) noexcept
        : value_(value) {
    }

    ~ScopedHandle() {
        if (value_ != INVALID_HANDLE_VALUE &&
            value_ != nullptr) {
            static_cast<void>(CloseHandle(value_));
        }
    }

    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;

    HANDLE Get() const noexcept {
        return value_;
    }

    bool IsValid() const noexcept {
        return value_ != INVALID_HANDLE_VALUE &&
            value_ != nullptr;
    }

private:
    HANDLE value_;
};

EndpointManifestLoadResult Failure(
    EndpointManifestLoadError loadError,
    DWORD systemError = ERROR_SUCCESS,
    EndpointManifestError validationError =
        EndpointManifestError::InvalidArgument) noexcept {
    EndpointManifestLoadResult result{};
    result.loadError = loadError;
    result.validationError = validationError;
    result.systemError = systemError;
    return result;
}

bool DefaultModulePathLookup(
    void*,
    HMODULE module,
    wchar_t* path,
    std::size_t pathCapacity,
    std::size_t* pathLength) noexcept {
    if (module == nullptr ||
        path == nullptr ||
        pathLength == nullptr ||
        pathCapacity < 2 ||
        pathCapacity > MAXDWORD) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

    const DWORD copied = GetModuleFileNameW(
        module,
        path,
        static_cast<DWORD>(pathCapacity));
    if (copied == 0) {
        return false;
    }
    if (copied >= pathCapacity ||
        path[copied] != L'\0') {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return false;
    }
    *pathLength = copied;
    return true;
}

bool FindDirectoryLength(
    const wchar_t* modulePath,
    std::size_t modulePathLength,
    std::size_t* directoryLength) noexcept {
    if (modulePath == nullptr ||
        directoryLength == nullptr ||
        modulePathLength == 0) {
        return false;
    }
    for (std::size_t cursor = modulePathLength; cursor > 0; --cursor) {
        const wchar_t character = modulePath[cursor - 1];
        if (character == L'\\' || character == L'/') {
            if (cursor == 1) {
                return false;
            }
            *directoryLength = cursor - 1;
            return true;
        }
    }
    return false;
}

bool GetFinalPath(
    HANDLE handle,
    wchar_t* path,
    std::size_t pathCapacity,
    std::size_t* pathLength) noexcept {
    if (handle == nullptr ||
        handle == INVALID_HANDLE_VALUE ||
        path == nullptr ||
        pathLength == nullptr ||
        pathCapacity < 2 ||
        pathCapacity > MAXDWORD) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }

    const DWORD copied = GetFinalPathNameByHandleW(
        handle,
        path,
        static_cast<DWORD>(pathCapacity),
        FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (copied == 0) {
        return false;
    }
    if (copied >= pathCapacity ||
        path[copied] != L'\0') {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return false;
    }
    *pathLength = copied;
    return true;
}

bool FinalPathHasExactParent(
    const wchar_t* filePath,
    std::size_t filePathLength,
    const wchar_t* directoryPath,
    std::size_t directoryPathLength) noexcept {
    std::size_t fileParentLength = 0;
    if (!FindDirectoryLength(
            filePath,
            filePathLength,
            &fileParentLength)) {
        return false;
    }

    while (directoryPathLength > 0 &&
        (directoryPath[directoryPathLength - 1] == L'\\' ||
            directoryPath[directoryPathLength - 1] == L'/')) {
        --directoryPathLength;
    }

    if (fileParentLength != directoryPathLength ||
        fileParentLength > INT_MAX) {
        return false;
    }
    return CompareStringOrdinal(
        filePath,
        static_cast<int>(fileParentLength),
        directoryPath,
        static_cast<int>(directoryPathLength),
        TRUE) == CSTR_EQUAL;
}

} // namespace

static EndpointManifestLoadResult LoadEndpointManifestCore(
    const EndpointManifestLoadContext& context,
    EndpointManifest* manifest) noexcept {
    if (manifest == nullptr || context.module == nullptr) {
        return Failure(EndpointManifestLoadError::InvalidArgument);
    }

    wchar_t modulePath[EndpointManifestPathCapacity]{};
    std::size_t modulePathLength = 0;
    const auto pathLookup =
        context.modulePathLookup != nullptr
            ? context.modulePathLookup
            : DefaultModulePathLookup;
    if (!pathLookup(
            context.modulePathContext,
            context.module,
            modulePath,
            EndpointManifestPathCapacity,
            &modulePathLength) ||
        modulePathLength == 0 ||
        modulePathLength >= EndpointManifestPathCapacity ||
        modulePath[modulePathLength] != L'\0' ||
        std::wcslen(modulePath) != modulePathLength) {
        const DWORD error = GetLastError();
        return Failure(
            EndpointManifestLoadError::ModulePathUnavailable,
            error != ERROR_SUCCESS
                ? error
                : ERROR_INVALID_DATA);
    }

    std::size_t directoryLength = 0;
    if (!FindDirectoryLength(
            modulePath,
            modulePathLength,
            &directoryLength)) {
        return Failure(
            EndpointManifestLoadError::ModulePathUnavailable,
            ERROR_INVALID_NAME);
    }

    wchar_t directoryPath[EndpointManifestPathCapacity]{};
    if (directoryLength + 1 > EndpointManifestPathCapacity) {
        return Failure(
            EndpointManifestLoadError::PathTooLong,
            ERROR_INSUFFICIENT_BUFFER);
    }
    std::wmemcpy(directoryPath, modulePath, directoryLength);
    directoryPath[directoryLength] = L'\0';

    constexpr std::size_t filenameLength =
        (sizeof(EndpointManifestFilename) / sizeof(wchar_t)) - 1;
    if (directoryLength + 1 + filenameLength + 1 >
        EndpointManifestPathCapacity) {
        return Failure(
            EndpointManifestLoadError::PathTooLong,
            ERROR_INSUFFICIENT_BUFFER);
    }
    wchar_t manifestPath[EndpointManifestPathCapacity]{};
    std::wmemcpy(manifestPath, directoryPath, directoryLength);
    manifestPath[directoryLength] = L'\\';
    std::wmemcpy(
        manifestPath + directoryLength + 1,
        EndpointManifestFilename,
        filenameLength);
    const std::size_t manifestPathLength =
        directoryLength + 1 + filenameLength;
    manifestPath[manifestPathLength] = L'\0';

    const ScopedHandle directory(CreateFileW(
        directoryPath,
        0,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS,
        nullptr));
    if (!directory.IsValid()) {
        return Failure(
            EndpointManifestLoadError::DirectoryOpenFailed,
            GetLastError());
    }

    const ScopedHandle file(CreateFileW(
        manifestPath,
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL |
            FILE_FLAG_OPEN_REPARSE_POINT |
            FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr));
    if (!file.IsValid()) {
        return Failure(
            EndpointManifestLoadError::FileOpenFailed,
            GetLastError());
    }

    FILE_ATTRIBUTE_TAG_INFO attributes{};
    if (!GetFileInformationByHandleEx(
            file.Get(),
            FileAttributeTagInfo,
            &attributes,
            sizeof(attributes))) {
        return Failure(
            EndpointManifestLoadError::FinalPathUnavailable,
            GetLastError());
    }
    if ((attributes.FileAttributes &
            FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
        return Failure(
            EndpointManifestLoadError::ReparsePointRejected,
            ERROR_REPARSE_TAG_INVALID);
    }

    wchar_t finalDirectoryPath[EndpointManifestPathCapacity]{};
    wchar_t finalManifestPath[EndpointManifestPathCapacity]{};
    std::size_t finalDirectoryLength = 0;
    std::size_t finalManifestLength = 0;
    if (!GetFinalPath(
            directory.Get(),
            finalDirectoryPath,
            EndpointManifestPathCapacity,
            &finalDirectoryLength) ||
        !GetFinalPath(
            file.Get(),
            finalManifestPath,
            EndpointManifestPathCapacity,
            &finalManifestLength)) {
        return Failure(
            EndpointManifestLoadError::FinalPathUnavailable,
            GetLastError());
    }
    if (!FinalPathHasExactParent(
            finalManifestPath,
            finalManifestLength,
            finalDirectoryPath,
            finalDirectoryLength)) {
        return Failure(
            EndpointManifestLoadError::FinalPathEscaped,
            ERROR_ACCESS_DENIED);
    }

    LARGE_INTEGER fileSize{};
    if (!GetFileSizeEx(file.Get(), &fileSize)) {
        return Failure(
            EndpointManifestLoadError::InvalidFileSize,
            GetLastError());
    }
    if (fileSize.QuadPart < 146 ||
        fileSize.QuadPart >
            static_cast<LONGLONG>(EndpointManifestMaximumBytes)) {
        return Failure(
            EndpointManifestLoadError::InvalidFileSize,
            ERROR_FILE_TOO_LARGE);
    }

    std::uint8_t bytes[EndpointManifestMaximumBytes]{};
    DWORD bytesRead = 0;
    if (!ReadFile(
            file.Get(),
            bytes,
            static_cast<DWORD>(sizeof(bytes)),
            &bytesRead,
            nullptr) ||
        bytesRead != static_cast<DWORD>(fileSize.QuadPart)) {
        const DWORD error = GetLastError();
        return Failure(
            EndpointManifestLoadError::FileReadFailed,
            error != ERROR_SUCCESS
                ? error
                : ERROR_HANDLE_EOF);
    }

    EndpointManifest candidate{};
    const auto validationError = ParseAndVerifyEndpointManifest(
        bytes,
        bytesRead,
        context.validation,
        &candidate);
    if (validationError != EndpointManifestError::Success) {
        return Failure(
            EndpointManifestLoadError::ValidationFailed,
            ERROR_INVALID_DATA,
            validationError);
    }

    *manifest = candidate;
    EndpointManifestLoadResult result{};
    result.loadError = EndpointManifestLoadError::Success;
    result.validationError = EndpointManifestError::Success;
    result.systemError = ERROR_SUCCESS;
    return result;
}

EndpointManifestLoadResult EndpointManifestLoader::LoadOnce(
    const EndpointManifestLoadContext& context) noexcept {
    const LONG prior = InterlockedCompareExchange(
        &state_,
        1,
        0);
    if (prior == 1) {
        return Failure(
            EndpointManifestLoadError::LoadInProgress,
            ERROR_BUSY);
    }
    if (prior == 2 || prior == 3) {
        return result_;
    }
    if (prior != 0) {
        return Failure(
            EndpointManifestLoadError::InvalidArgument,
            ERROR_INVALID_STATE);
    }

    EndpointManifest candidate{};
    const auto loadResult =
        LoadEndpointManifestCore(context, &candidate);
    if (loadResult.loadError ==
        EndpointManifestLoadError::Success) {
        manifest_ = candidate;
    }
    result_ = loadResult;
    InterlockedExchange(
        &state_,
        loadResult.loadError ==
                EndpointManifestLoadError::Success
            ? 2
            : 3);
    return loadResult;
}

bool EndpointManifestLoader::TryCopyManifest(
    EndpointManifest* manifest) const noexcept {
    if (manifest == nullptr) {
        return false;
    }
    auto* mutableState = const_cast<volatile LONG*>(&state_);
    if (InterlockedCompareExchange(
            mutableState,
            2,
            2) != 2) {
        return false;
    }
    *manifest = manifest_;
    return true;
}

} // namespace godswar::network
