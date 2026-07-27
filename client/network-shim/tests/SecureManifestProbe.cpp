#include "SecureManifestProbe.h"

#include "../src/EndpointManifest.h"
#include "../src/FileSha256.h"
#include "../src/SecureClientManifestBuildContract.h"
#include "../src/SecureClientRuntimeInternal.h"

#include <Windows.h>

#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::EndpointManifest;
using godswar::network::EndpointManifestEnvironment;
using godswar::network::EndpointManifestError;
using godswar::network::EndpointManifestMaximumBytes;
using godswar::network::EndpointManifestPublicKey;

struct ProbeContext final {
    godswar::network::SecureClientManifestBuildContract contract{};
};

bool LookupKey(
    void* rawContext,
    std::uint16_t keyId,
    EndpointManifestPublicKey* key) noexcept {
    const auto* context =
        static_cast<const ProbeContext*>(rawContext);
    if (context == nullptr || key == nullptr) {
        return false;
    }
    if (keyId == context->contract.currentKeyId) {
        *key = context->contract.currentKey;
        return true;
    }
    if (keyId == context->contract.nextKeyId) {
        *key = context->contract.nextKey;
        return true;
    }
    return false;
}

bool LookupFloors(
    void* rawContext,
    EndpointManifestEnvironment environment,
    std::uint64_t* compiled,
    std::uint64_t* installed) noexcept {
    const auto* context =
        static_cast<const ProbeContext*>(rawContext);
    if (context == nullptr ||
        compiled == nullptr ||
        installed == nullptr ||
        static_cast<std::uint8_t>(environment) !=
            context->contract.environment) {
        return false;
    }
    *compiled = context->contract.compiledMinimumSequence;
    *installed = *compiled;
    return true;
}

bool ReadClock(void*, std::uint64_t* seconds) noexcept {
    std::uint64_t milliseconds = 0;
    if (seconds == nullptr ||
        !godswar::network::ReadSystemUnixMilliseconds(
            &milliseconds)) {
        return false;
    }
    *seconds = milliseconds / 1000;
    return true;
}

bool ReadManifestFile(
    const wchar_t* path,
    std::uint8_t* bytes,
    DWORD* byteCount) noexcept {
    if (path == nullptr || bytes == nullptr || byteCount == nullptr) {
        return false;
    }
    *byteCount = 0;
    const HANDLE file = CreateFileW(
        path,
        GENERIC_READ,
        FILE_SHARE_READ,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL |
            FILE_FLAG_OPEN_REPARSE_POINT |
            FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }

    FILE_ATTRIBUTE_TAG_INFO attributes{};
    LARGE_INTEGER length{};
    bool succeeded = GetFileInformationByHandleEx(
            file,
            FileAttributeTagInfo,
            &attributes,
            sizeof(attributes)) != FALSE &&
        (attributes.FileAttributes &
            (FILE_ATTRIBUTE_DIRECTORY |
                FILE_ATTRIBUTE_REPARSE_POINT)) == 0 &&
        GetFileSizeEx(file, &length) != FALSE &&
        length.QuadPart > 14 &&
        length.QuadPart <=
            static_cast<LONGLONG>(EndpointManifestMaximumBytes);
    DWORD read = 0;
    if (succeeded) {
        succeeded = ReadFile(
                file,
                bytes,
                static_cast<DWORD>(length.QuadPart),
                &read,
                nullptr) != FALSE &&
            read == static_cast<DWORD>(length.QuadPart);
    }
    CloseHandle(file);
    if (succeeded) {
        *byteCount = read;
    }
    return succeeded;
}

} // namespace

int RunSecureCandidateContractProbe(
    const wchar_t* candidatePath) noexcept {
    godswar::network::SecureClientManifestBuildContract contract{};
    if (!godswar::network::ReadSecureClientManifestBuildContract(
            candidatePath,
            &contract)) {
        std::fprintf(
            stderr,
            "Candidate manifest-key build contract is invalid.\n");
        return 1;
    }
    std::puts("Candidate manifest-key build contract is valid.");
    return 0;
}

int RunSecureCandidateOriginContractProbe(
    const wchar_t* candidatePath,
    const wchar_t* originPath) noexcept {
    godswar::network::SecureClientManifestBuildContract contract{};
    if (!godswar::network::ReadSecureClientManifestBuildContract(
            candidatePath,
            &contract)) {
        std::fprintf(
            stderr,
            "Candidate Origin probe could not read the client build contract.\n");
        return 1;
    }
    if (!godswar::network::FileMatchesSha256(
            originPath,
            contract.originSha256,
            sizeof(contract.originSha256))) {
        std::fprintf(
            stderr,
            "Candidate Origin does not match the client build contract (%lu).\n",
            static_cast<unsigned long>(GetLastError()));
        return 1;
    }
    std::puts("Candidate Origin matches the client build contract.");
    return 0;
}

int RunSecureManifestProbe(
    const wchar_t* candidatePath,
    const wchar_t* manifestPath) noexcept {
    ProbeContext context{};
    if (!godswar::network::ReadSecureClientManifestBuildContract(
            candidatePath,
            &context.contract)) {
        std::fprintf(
            stderr,
            "Secure manifest probe could not read the candidate key contract.\n");
        return 1;
    }

    std::uint8_t bytes[EndpointManifestMaximumBytes]{};
    DWORD byteCount = 0;
    if (!ReadManifestFile(manifestPath, bytes, &byteCount)) {
        std::fprintf(stderr, "Secure manifest probe could not read the file.\n");
        return 1;
    }

    const auto environment = static_cast<EndpointManifestEnvironment>(
        context.contract.environment);
    godswar::network::EndpointManifestValidationContext validation{};
    validation.context = &context;
    validation.publicKeyLookup = LookupKey;
    validation.sequenceFloorLookup = LookupFloors;
    validation.clock = ReadClock;
    validation.expectedEnvironment = environment;

    EndpointManifest manifest{};
    const auto result =
        godswar::network::ParseAndVerifyEndpointManifest(
            bytes,
            byteCount,
            validation,
            &manifest);
    SecureZeroMemory(bytes, sizeof(bytes));
    if (result != EndpointManifestError::Success) {
        std::fprintf(
            stderr,
            "Secure manifest probe rejected the candidate key contract (%u).\n",
            static_cast<unsigned>(result));
        return 1;
    }

    std::puts("Secure manifest matches the candidate client verification key.");
    return 0;
}
