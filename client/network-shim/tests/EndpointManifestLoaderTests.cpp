#include "EndpointManifestLoaderTests.h"

#include "../src/EndpointManifestLoader.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cwchar>

namespace {

using godswar::network::EndpointManifest;
using godswar::network::EndpointManifestError;
using godswar::network::EndpointManifestLoadContext;
using godswar::network::EndpointManifestLoadError;
using godswar::network::EndpointManifestLoader;
using godswar::network::tests::BuildTestManifest;
using godswar::network::tests::EndpointManifestTestBytes;
using godswar::network::tests::EndpointManifestTestSigner;
using godswar::network::tests::EndpointManifestValidationFixture;
using godswar::network::tests::MakeTestValidation;

int Failures = 0;

void Check(bool condition, const char* message) noexcept {
    if (condition) {
        return;
    }
    std::fprintf(stderr, "FAIL: %s\n", message);
    ++Failures;
}

struct ModulePathFixture final {
    wchar_t modulePath[1024]{};
};

bool CopyModulePath(
    void* rawContext,
    HMODULE,
    wchar_t* path,
    std::size_t capacity,
    std::size_t* length) noexcept {
    auto* context =
        static_cast<ModulePathFixture*>(rawContext);
    if (context == nullptr ||
        path == nullptr ||
        length == nullptr) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return false;
    }
    const std::size_t sourceLength =
        std::wcslen(context->modulePath);
    if (sourceLength + 1 > capacity) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return false;
    }
    std::wmemcpy(path, context->modulePath, sourceLength + 1);
    *length = sourceLength;
    return true;
}

bool WriteFixtureFile(
    const wchar_t* path,
    const std::uint8_t* bytes,
    std::size_t byteCount) noexcept {
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
    const bool succeeded =
        byteCount <= MAXDWORD &&
        WriteFile(
            file,
            bytes,
            static_cast<DWORD>(byteCount),
            &written,
            nullptr) &&
        written == byteCount;
    CloseHandle(file);
    return succeeded;
}

} // namespace

int RunEndpointManifestLoaderTests(
    const EndpointManifestTestSigner& signer,
    EndpointManifestValidationFixture* fixture) {
    Failures = 0;
    wchar_t temporaryRoot[MAX_PATH]{};
    wchar_t temporaryName[MAX_PATH]{};
    if (GetTempPathW(MAX_PATH, temporaryRoot) == 0 ||
        GetTempFileNameW(
            temporaryRoot,
            L"gwe",
            0,
            temporaryName) == 0 ||
        !DeleteFileW(temporaryName) ||
        !CreateDirectoryW(temporaryName, nullptr)) {
        Check(false, "manifest loader temp directory setup failed");
        return Failures;
    }

    ModulePathFixture pathFixture{};
    ::swprintf_s(
        pathFixture.modulePath,
        L"%s\\Fixture.dll",
        temporaryName);
    wchar_t manifestPath[MAX_PATH]{};
    ::swprintf_s(
        manifestPath,
        L"%s\\RebornNetwork.gwem",
        temporaryName);

    EndpointManifestTestBytes bytes{};
    BuildTestManifest(&bytes, signer);
    Check(
        WriteFixtureFile(
            manifestPath,
            bytes.bytes,
            bytes.byteCount),
        "signed loader fixture write failed");

    EndpointManifestLoadContext context{};
    context.module = GetModuleHandleW(nullptr);
    context.modulePathContext = &pathFixture;
    context.modulePathLookup = CopyModulePath;
    context.validation = MakeTestValidation(fixture);

    EndpointManifestLoader loader;
    const auto loaded = loader.LoadOnce(context);
    EndpointManifest manifest{};
    Check(
        loaded.loadError == EndpointManifestLoadError::Success &&
            loaded.validationError ==
                EndpointManifestError::Success &&
            loader.TryCopyManifest(&manifest) &&
            manifest.sequence == 12,
        "module-relative signed manifest did not load");
    bytes.bytes[bytes.byteCount - 1] ^= 0x10;
    Check(
        WriteFixtureFile(
            manifestPath,
            bytes.bytes,
            bytes.byteCount),
        "no-reload fixture write failed");
    EndpointManifest cached{};
    Check(
        loader.LoadOnce(context).loadError ==
                EndpointManifestLoadError::Success &&
            loader.TryCopyManifest(&cached) &&
            cached.sequence == 12,
        "one-shot loader hot-reloaded a changed file");
    bytes.bytes[bytes.byteCount - 1] ^= 0x10;
    Check(
        WriteFixtureFile(
            manifestPath,
            bytes.bytes,
            bytes.byteCount),
        "writer-test fixture restore failed");

    const HANDLE writer = CreateFileW(
        manifestPath,
        GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    Check(writer != INVALID_HANDLE_VALUE, "writer fixture open failed");
    if (writer != INVALID_HANDLE_VALUE) {
        EndpointManifestLoader lockedLoader;
        const auto locked = lockedLoader.LoadOnce(context);
        Check(
            locked.loadError ==
                    EndpointManifestLoadError::FileOpenFailed &&
                locked.systemError == ERROR_SHARING_VIOLATION,
            "loader permitted an existing writer");
        CloseHandle(writer);
    }

    bytes.bytes[bytes.byteCount - 1] ^= 0x40;
    Check(
        WriteFixtureFile(
            manifestPath,
            bytes.bytes,
            bytes.byteCount),
        "tampered loader fixture write failed");
    manifest.sequence = 777;
    EndpointManifestLoader rejectedLoader;
    const auto rejected = rejectedLoader.LoadOnce(context);
    Check(
        rejected.loadError ==
                EndpointManifestLoadError::ValidationFailed &&
            rejected.validationError ==
                EndpointManifestError::SignatureVerificationFailed &&
            !rejectedLoader.TryCopyManifest(&manifest) &&
            manifest.sequence == 777,
        "loader published a manifest before signature verification");

    std::uint8_t oversized[4097]{};
    Check(
        WriteFixtureFile(
            manifestPath,
            oversized,
            sizeof(oversized)),
        "oversized loader fixture write failed");
    manifest.sequence = 778;
    EndpointManifestLoader sizeLoader;
    const auto sizeRejected = sizeLoader.LoadOnce(context);
    Check(
        sizeRejected.loadError ==
                EndpointManifestLoadError::InvalidFileSize &&
            !sizeLoader.TryCopyManifest(&manifest) &&
            manifest.sequence == 778,
        "loader read or published a file above 4096 bytes");

    wchar_t symlinkTarget[MAX_PATH]{};
    if (GetTempFileNameW(
            temporaryRoot,
            L"gwt",
            0,
            symlinkTarget) != 0) {
        Check(
            WriteFixtureFile(
                symlinkTarget,
                bytes.bytes,
                bytes.byteCount),
            "reparse target fixture write failed");
        static_cast<void>(DeleteFileW(manifestPath));
        constexpr DWORD allowUnprivilegedCreate = 0x2;
        bool linked = CreateSymbolicLinkW(
            manifestPath,
            symlinkTarget,
            allowUnprivilegedCreate) != FALSE;
        if (!linked && GetLastError() == ERROR_INVALID_PARAMETER) {
            linked = CreateSymbolicLinkW(
                manifestPath,
                symlinkTarget,
                0) != FALSE;
        }
        if (linked) {
            EndpointManifestLoader reparseLoader;
            const auto reparseRejected =
                reparseLoader.LoadOnce(context);
            Check(
                reparseRejected.loadError ==
                    EndpointManifestLoadError::ReparsePointRejected,
                "manifest file reparse point was followed");
            static_cast<void>(DeleteFileW(manifestPath));
        }
        static_cast<void>(DeleteFileW(symlinkTarget));
    }

    static_cast<void>(DeleteFileW(manifestPath));
    static_cast<void>(RemoveDirectoryW(temporaryName));
    return Failures;
}
