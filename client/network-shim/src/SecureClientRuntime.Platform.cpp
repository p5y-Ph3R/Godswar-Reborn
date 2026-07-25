#include "SecureClientRuntime.h"
#include "SecureClientRuntimeInternal.h"
#include "SecureClientManifestBuildContract.h"

#include <Windows.h>
#include <bcrypt.h>

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "bcrypt.lib")

namespace godswar::network {
namespace {

constexpr wchar_t ActivationRegistryPath[] =
    L"SOFTWARE\\Reborn\\NetworkManifest";
constexpr wchar_t ActivationModeValue[] = L"ActivationMode";
constexpr wchar_t ActivationEnvironmentValue[] = L"Environment";
constexpr wchar_t ActivationSequenceValue[] =
    L"HighestAcceptedSequence";

bool ReadDword(
    HKEY key,
    const wchar_t* valueName,
    DWORD* value,
    DWORD* error) noexcept {
    DWORD type = 0;
    DWORD bytes = sizeof(*value);
    const LSTATUS result = RegQueryValueExW(
        key,
        valueName,
        nullptr,
        &type,
        reinterpret_cast<BYTE*>(value),
        &bytes);
    if (result != ERROR_SUCCESS ||
        type != REG_DWORD ||
        bytes != sizeof(*value)) {
        *error = result != ERROR_SUCCESS
            ? static_cast<DWORD>(result)
            : ERROR_INVALID_DATA;
        return false;
    }
    return true;
}

bool ReadQword(
    HKEY key,
    const wchar_t* valueName,
    std::uint64_t* value,
    DWORD* error) noexcept {
    DWORD type = 0;
    DWORD bytes = sizeof(*value);
    const LSTATUS result = RegQueryValueExW(
        key,
        valueName,
        nullptr,
        &type,
        reinterpret_cast<BYTE*>(value),
        &bytes);
    if (result != ERROR_SUCCESS ||
        type != REG_QWORD ||
        bytes != sizeof(*value)) {
        *error = result != ERROR_SUCCESS
            ? static_cast<DWORD>(result)
            : ERROR_INVALID_DATA;
        return false;
    }
    return true;
}

bool IsKnownEnvironment(
    EndpointManifestEnvironment environment) noexcept {
    return environment == EndpointManifestEnvironment::Development ||
        environment == EndpointManifestEnvironment::Staging ||
        environment == EndpointManifestEnvironment::Production;
}

} // namespace

SecureClientActivationReadResult
ReadInstalledSecureClientActivation(
    void*,
    SecureClientActivationRecord* activation,
    DWORD* systemError) noexcept {
    if (activation == nullptr || systemError == nullptr) {
        if (systemError != nullptr) {
            *systemError = ERROR_INVALID_PARAMETER;
        }
        return SecureClientActivationReadResult::Failed;
    }
    *activation = SecureClientActivationRecord{};
    *systemError = ERROR_SUCCESS;

    HKEY key = nullptr;
    const LSTATUS opened = RegOpenKeyExW(
        HKEY_LOCAL_MACHINE,
        ActivationRegistryPath,
        0,
        KEY_QUERY_VALUE | KEY_WOW64_64KEY,
        &key);
    if (opened == ERROR_FILE_NOT_FOUND ||
        opened == ERROR_PATH_NOT_FOUND) {
        return SecureClientActivationReadResult::Success;
    }
    if (opened != ERROR_SUCCESS) {
        *systemError = static_cast<DWORD>(opened);
        return SecureClientActivationReadResult::Failed;
    }

    DWORD mode = 0;
    bool valid = ReadDword(
        key,
        ActivationModeValue,
        &mode,
        systemError);
    if (valid && mode == 0) {
        activation->mode = SecureClientActivationMode::Disabled;
    } else if (valid && mode == 1) {
        DWORD environment = 0;
        std::uint64_t sequence = 0;
        valid = ReadDword(
                    key,
                    ActivationEnvironmentValue,
                    &environment,
                    systemError) &&
            ReadQword(
                    key,
                    ActivationSequenceValue,
                    &sequence,
                    systemError);
        const auto parsedEnvironment =
            static_cast<EndpointManifestEnvironment>(environment);
        if (valid &&
            sequence != 0 &&
            IsKnownEnvironment(parsedEnvironment)) {
            activation->mode =
                SecureClientActivationMode::SecureRequired;
            activation->environment = parsedEnvironment;
            activation->installedMinimumSequence = sequence;
        } else if (valid) {
            valid = false;
            *systemError = ERROR_INVALID_DATA;
        }
    } else if (valid) {
        valid = false;
        *systemError = ERROR_INVALID_DATA;
    }

    RegCloseKey(key);
    return valid
        ? SecureClientActivationReadResult::Success
        : SecureClientActivationReadResult::Failed;
}

bool TryLookupEmbeddedSecureClientManifestPublicKey(
    EndpointManifestEnvironment environment,
    std::uint16_t publicKeyId,
    EndpointManifestPublicKey* publicKey) noexcept {
    if (publicKey == nullptr) {
        return false;
    }
    *publicKey = EndpointManifestPublicKey{};
    if (environment != EndpointManifestEnvironment::Development) {
        return false;
    }

    const auto& contract =
        GetSecureClientManifestBuildContract();
    if (!IsValidSecureClientManifestBuildContract(contract) ||
        contract.environment !=
            static_cast<std::uint8_t>(environment)) {
        return false;
    }
    const EndpointManifestPublicKey* selected = nullptr;
    if (publicKeyId ==
            contract.currentKeyId &&
        publicKeyId ==
            SecureClientDevelopmentCurrentManifestKeyId) {
        selected = &contract.currentKey;
    } else if (publicKeyId ==
                   contract.nextKeyId &&
               publicKeyId ==
                   SecureClientDevelopmentNextManifestKeyId) {
        selected = &contract.nextKey;
    } else {
        return false;
    }

    *publicKey = *selected;
    return true;
}

bool TryGetCompiledSecureClientManifestSequenceFloor(
    EndpointManifestEnvironment environment,
    std::uint64_t* compiledMinimum) noexcept {
    if (compiledMinimum == nullptr) {
        return false;
    }
    *compiledMinimum = 0;
    if (environment != EndpointManifestEnvironment::Development) {
        return false;
    }
    const auto& contract =
        GetSecureClientManifestBuildContract();
    if (!IsValidSecureClientManifestBuildContract(contract) ||
        contract.environment !=
            static_cast<std::uint8_t>(environment)) {
        return false;
    }
    *compiledMinimum = contract.compiledMinimumSequence;
    return true;
}

bool GenerateSystemSecureRandom(
    void* destination,
    std::size_t destinationBytes) noexcept {
    return destination != nullptr &&
        destinationBytes != 0 &&
        destinationBytes <=
            (std::numeric_limits<ULONG>::max)() &&
        BCryptGenRandom(
            nullptr,
            static_cast<PUCHAR>(destination),
            static_cast<ULONG>(destinationBytes),
            BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0;
}

bool ReadSystemUnixMilliseconds(
    std::uint64_t* unixMilliseconds) noexcept {
    if (unixMilliseconds == nullptr) {
        return false;
    }
    constexpr std::uint64_t WindowsToUnixEpochTicks =
        116'444'736'000'000'000ULL;
    constexpr std::uint64_t TicksPerMillisecond = 10'000ULL;
    FILETIME fileTime{};
    GetSystemTimeAsFileTime(&fileTime);
    ULARGE_INTEGER ticks{};
    ticks.LowPart = fileTime.dwLowDateTime;
    ticks.HighPart = fileTime.dwHighDateTime;
    if (ticks.QuadPart < WindowsToUnixEpochTicks) {
        *unixMilliseconds = 0;
        return false;
    }
    *unixMilliseconds =
        (ticks.QuadPart - WindowsToUnixEpochTicks) /
        TicksPerMillisecond;
    return true;
}

} // namespace godswar::network
