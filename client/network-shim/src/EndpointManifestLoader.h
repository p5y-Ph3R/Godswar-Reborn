#pragma once

#include "EndpointManifest.h"

#include <Windows.h>

#include <cstddef>

namespace godswar::network {

inline constexpr wchar_t EndpointManifestFilename[] =
    L"RebornNetwork.gwem";
inline constexpr std::size_t EndpointManifestPathCapacity = 4096;

enum class EndpointManifestLoadError : std::uint8_t {
    Success = 0,
    InvalidArgument,
    LoadInProgress,
    ModulePathUnavailable,
    PathTooLong,
    DirectoryOpenFailed,
    FileOpenFailed,
    ReparsePointRejected,
    FinalPathUnavailable,
    FinalPathEscaped,
    InvalidFileSize,
    FileReadFailed,
    ValidationFailed,
};

using EndpointManifestModulePathLookup = bool (*)(
    void* context,
    HMODULE module,
    wchar_t* path,
    std::size_t pathCapacity,
    std::size_t* pathLength) noexcept;

struct EndpointManifestLoadContext final {
    HMODULE module = nullptr;
    void* modulePathContext = nullptr;
    EndpointManifestModulePathLookup modulePathLookup = nullptr;
    EndpointManifestValidationContext validation{};
};

struct EndpointManifestLoadResult final {
    EndpointManifestLoadError loadError =
        EndpointManifestLoadError::InvalidArgument;
    EndpointManifestError validationError =
        EndpointManifestError::InvalidArgument;
    DWORD systemError = ERROR_SUCCESS;
};

class EndpointManifestLoader final {
public:
    EndpointManifestLoader() = default;

    EndpointManifestLoader(const EndpointManifestLoader&) = delete;
    EndpointManifestLoader& operator=(
        const EndpointManifestLoader&) = delete;

    EndpointManifestLoadResult LoadOnce(
        const EndpointManifestLoadContext& context) noexcept;

    bool TryCopyManifest(
        EndpointManifest* manifest) const noexcept;

private:
    volatile LONG state_ = 0;
    EndpointManifest manifest_{};
    EndpointManifestLoadResult result_{};
};

} // namespace godswar::network
