#pragma once

#include "ClientRoute.h"
#include "EndpointManifestLoader.h"
#include "SecureGameGrantRegistry.h"
#include "SecureClientSession.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>

namespace godswar::network {

inline constexpr std::size_t SecureClientInstanceIdBytes = 16;
inline constexpr std::size_t SecureClientOriginSha256Bytes = 32;
inline constexpr std::uint16_t
    SecureClientDevelopmentCurrentManifestKeyId = 0xD001;
inline constexpr std::uint16_t
    SecureClientDevelopmentNextManifestKeyId = 0xD002;

enum class SecureClientActivationMode : std::uint8_t {
    Disabled = 0,
    SecureRequired = 1,
};

enum class SecureClientActivationReadResult : std::uint8_t {
    Success = 0,
    Failed,
};

struct SecureClientActivationRecord final {
    SecureClientActivationMode mode =
        SecureClientActivationMode::Disabled;
    EndpointManifestEnvironment environment =
        EndpointManifestEnvironment::Development;
    std::uint64_t installedMinimumSequence = 0;
};

using SecureClientActivationReader =
    SecureClientActivationReadResult (*)(
        void* context,
        SecureClientActivationRecord* activation,
        DWORD* systemError) noexcept;

using SecureClientManifestReader =
    EndpointManifestLoadResult (*)(
        void* context,
        HMODULE module,
        const EndpointManifestValidationContext& validation,
        EndpointManifest* manifest) noexcept;

using SecureClientRandomGenerator =
    bool (*)(
        void* context,
        void* destination,
        std::size_t destinationBytes) noexcept;

using SecureClientUnixMillisecondsClock =
    bool (*)(
        void* context,
        std::uint64_t* unixMilliseconds) noexcept;

struct SecureClientRuntimeDependencies final {
    void* activationContext = nullptr;
    SecureClientActivationReader activationReader = nullptr;
    void* manifestContext = nullptr;
    SecureClientManifestReader manifestReader = nullptr;
    void* publicKeyContext = nullptr;
    EndpointManifestPublicKeyLookup publicKeyLookup = nullptr;
    void* randomContext = nullptr;
    SecureClientRandomGenerator randomGenerator = nullptr;
    void* clockContext = nullptr;
    SecureClientUnixMillisecondsClock clock = nullptr;
};

enum class SecureClientRuntimeState : std::uint8_t {
    Uninitialized = 0,
    Disabled,
    SecureRequiredReady,
    FailedClosed,
};

enum class SecureClientRuntimeFailure : std::uint8_t {
    None = 0,
    ActivationRead,
    InvalidActivation,
    ModuleUnavailable,
    ManifestLoad,
    ManifestPolicy,
    RandomGeneration,
    GrantRegistryAllocation,
};

struct SecureClientRuntimeSnapshot final {
    SecureClientRuntimeState state =
        SecureClientRuntimeState::Uninitialized;
    SecureClientRuntimeFailure failure =
        SecureClientRuntimeFailure::None;
    SecureClientActivationRecord activation{};
    DWORD activationSystemError = ERROR_SUCCESS;
    EndpointManifestLoadResult manifestLoad{};
    std::uint64_t manifestSequence = 0;
};

struct SecureClientSessionRetentionSnapshot final {
    bool available = false;
    std::uint64_t generation = 0;
    SecureClientSessionSnapshot session{};
};

// Reads the process-wide activation contract from the native 64-bit registry
// view. This function never writes registry state. A missing key means the
// explicit Disabled baseline; a present but malformed key fails.
SecureClientActivationReadResult
ReadInstalledSecureClientActivation(
    void* context,
    SecureClientActivationRecord* activation,
    DWORD* systemError) noexcept;

// Development has two embedded verification-only public keys. Staging and
// production deliberately have no placeholder key that could accidentally be
// accepted. Matching private keys are not present in this repository.
bool TryLookupEmbeddedSecureClientManifestPublicKey(
    EndpointManifestEnvironment environment,
    std::uint16_t publicKeyId,
    EndpointManifestPublicKey* publicKey) noexcept;

bool TryGetCompiledSecureClientManifestSequenceFloor(
    EndpointManifestEnvironment environment,
    std::uint64_t* compiledMinimum) noexcept;

class SecureClientRuntime final {
public:
    SecureClientRuntime() noexcept;
    explicit SecureClientRuntime(
        SecureClientRuntimeDependencies dependencies) noexcept;
    ~SecureClientRuntime() noexcept;

    SecureClientRuntime(const SecureClientRuntime&) = delete;
    SecureClientRuntime& operator=(const SecureClientRuntime&) = delete;

    // Initialization is process-lifetime and one-shot. Concurrent callers
    // share the first result through INIT_ONCE.
    bool Initialize(HMODULE module) noexcept;

    ClientRoutePolicy RoutePolicy() noexcept;
    ClientRouteDecision ClassifyRoute(
        const ClientRoute& route) const noexcept;

    bool TryCopyManifest(EndpointManifest* manifest) const noexcept;
    bool TryCopyClientInstanceId(
        void* destination,
        std::size_t destinationBytes) const noexcept;
    bool TryCopyOriginSha256(
        void* destination,
        std::size_t destinationBytes) const noexcept;
    SecureGameGrantRegistry* GrantRegistry() noexcept;

    SecureClientRuntimeSnapshot Snapshot() const noexcept;
    void RetainSessionSnapshot(
        const SecureClientSessionSnapshot& snapshot) noexcept;
    SecureClientSessionRetentionSnapshot
    LastSessionSnapshot() const noexcept;

private:
    struct InitializationCall final {
        SecureClientRuntime* runtime = nullptr;
        HMODULE module = nullptr;
    };

    static BOOL CALLBACK InitializeOnce(
        PINIT_ONCE once,
        PVOID parameter,
        PVOID* context) noexcept;
    static ClientRouteDecision ClassifyCallback(
        void* context,
        NativeProxyId proxyId,
        const ClientRoute& route) noexcept;
    static bool LookupManifestKey(
        void* context,
        std::uint16_t publicKeyId,
        EndpointManifestPublicKey* publicKey) noexcept;
    static bool LookupSequenceFloors(
        void* context,
        EndpointManifestEnvironment environment,
        std::uint64_t* compiledMinimum,
        std::uint64_t* installedMinimum) noexcept;
    static bool ReadManifestClock(
        void* context,
        std::uint64_t* unixSeconds) noexcept;
    static bool ReadGrantClock(
        void* context,
        std::uint64_t* unixMilliseconds) noexcept;

    void InitializeCore(HMODULE module) noexcept;
    bool TryReadClock(std::uint64_t* unixMilliseconds) const noexcept;
    void Fail(
        SecureClientRuntimeFailure failure,
        DWORD systemError = ERROR_SUCCESS) noexcept;
    SecureClientRuntimeState ReadState() const noexcept;

    INIT_ONCE initializeOnce_{};
    SecureClientRuntimeDependencies dependencies_{};
    EndpointManifestLoader defaultManifestLoader_{};
    SecureClientActivationRecord activation_{};
    EndpointManifest manifest_{};
    EndpointManifestLoadResult manifestLoad_{};
    SecureGameGrantRegistry* grantRegistry_ = nullptr;
    volatile LONG state_ =
        static_cast<LONG>(SecureClientRuntimeState::Uninitialized);
    SecureClientRuntimeFailure failure_ =
        SecureClientRuntimeFailure::None;
    DWORD activationSystemError_ = ERROR_SUCCESS;
    std::uint8_t
        clientInstanceId_[SecureClientInstanceIdBytes]{};
    mutable SRWLOCK lastSessionLock_{};
    SecureClientSessionRetentionSnapshot lastSession_{};
};

SecureClientRuntime& ProcessSecureClientRuntime() noexcept;

bool EnsureProcessSecureClientRuntimeInitialized(
    HMODULE module) noexcept;

} // namespace godswar::network
