#include "SecureClientRuntime.h"
#include "SecureClientRuntimeInternal.h"
#include "SecureClientManifestBuildContract.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>

namespace godswar::network {
namespace {

constexpr std::uint8_t DevelopmentLegacyPassthroughFlag = 0x01;
constexpr unsigned RandomGenerationAttempts = 4;

bool ContainsNonzero(
    const std::uint8_t* bytes,
    std::size_t byteCount) noexcept {
    std::uint8_t combined = 0;
    for (std::size_t index = 0; index < byteCount; ++index) {
        combined |= bytes[index];
    }
    return combined != 0;
}

bool ExactRoute(
    const ClientRoute& route,
    const EndpointManifestText& host,
    std::uint16_t port) noexcept {
    return route.port == port &&
        route.hostLength == host.length &&
        route.hostLength <= NativeRouteHostMaximumBytes &&
        std::memcmp(route.host, host.bytes, route.hostLength) == 0;
}

} // namespace

SecureClientRuntime::SecureClientRuntime() noexcept {
    InitializeSRWLock(&lastSessionLock_);
}

SecureClientRuntime::SecureClientRuntime(
    SecureClientRuntimeDependencies dependencies) noexcept
    : dependencies_(dependencies) {
    InitializeSRWLock(&lastSessionLock_);
}

SecureClientRuntime::~SecureClientRuntime() noexcept {
    delete grantRegistry_;
    grantRegistry_ = nullptr;
    SecureZeroMemory(
        clientInstanceId_,
        sizeof(clientInstanceId_));
}

bool SecureClientRuntime::Initialize(HMODULE module) noexcept {
    InitializationCall call{this, module};
    if (!InitOnceExecuteOnce(
            &initializeOnce_,
            InitializeOnce,
            &call,
            nullptr)) {
        Fail(
            SecureClientRuntimeFailure::InvalidActivation,
            GetLastError());
    }
    const auto state = ReadState();
    return state == SecureClientRuntimeState::Disabled ||
        state == SecureClientRuntimeState::SecureRequiredReady;
}

ClientRoutePolicy SecureClientRuntime::RoutePolicy() noexcept {
    return ClientRoutePolicy{this, ClassifyCallback};
}

ClientRouteDecision SecureClientRuntime::ClassifyRoute(
    const ClientRoute& route) const noexcept {
    const auto state = ReadState();
    if (state == SecureClientRuntimeState::Disabled) {
        return ClientRouteDecision::PassThrough;
    }
    if (state != SecureClientRuntimeState::SecureRequiredReady) {
        return ClientRouteDecision::Reject;
    }

    if (ExactRoute(
            route,
            manifest_.logicalLoginHost,
            manifest_.logicalLoginPort)) {
        return ClientRouteDecision::Login;
    }
    if (grantRegistry_ != nullptr &&
        grantRegistry_->MatchesPendingRoute(route)) {
        return ClientRouteDecision::Game;
    }
    if (manifest_.environment ==
            EndpointManifestEnvironment::Development &&
        (manifest_.flags &
            DevelopmentLegacyPassthroughFlag) != 0) {
        return ClientRouteDecision::PassThrough;
    }
    return ClientRouteDecision::Reject;
}

bool SecureClientRuntime::TryCopyManifest(
    EndpointManifest* manifest) const noexcept {
    if (manifest == nullptr ||
        ReadState() !=
            SecureClientRuntimeState::SecureRequiredReady) {
        return false;
    }
    *manifest = manifest_;
    return true;
}

bool SecureClientRuntime::TryCopyClientInstanceId(
    void* destination,
    std::size_t destinationBytes) const noexcept {
    if (destination == nullptr ||
        destinationBytes != sizeof(clientInstanceId_) ||
        ReadState() !=
            SecureClientRuntimeState::SecureRequiredReady) {
        return false;
    }
    std::memcpy(
        destination,
        clientInstanceId_,
        sizeof(clientInstanceId_));
    return true;
}

bool SecureClientRuntime::TryCopyOriginSha256(
    void* destination,
    std::size_t destinationBytes) const noexcept {
    const auto& contract =
        GetSecureClientManifestBuildContract();
    static_assert(
        sizeof(contract.originSha256) ==
            SecureClientOriginSha256Bytes,
        "runtime Origin identity size changed");
    if (destination == nullptr ||
        destinationBytes != sizeof(contract.originSha256) ||
        !IsValidSecureClientManifestBuildContract(contract) ||
        ReadState() !=
            SecureClientRuntimeState::SecureRequiredReady) {
        return false;
    }
    std::memcpy(
        destination,
        contract.originSha256,
        sizeof(contract.originSha256));
    return true;
}

SecureGameGrantRegistry*
SecureClientRuntime::GrantRegistry() noexcept {
    return ReadState() ==
            SecureClientRuntimeState::SecureRequiredReady
        ? grantRegistry_
        : nullptr;
}

SecureClientRuntimeSnapshot
SecureClientRuntime::Snapshot() const noexcept {
    SecureClientRuntimeSnapshot snapshot{};
    snapshot.state = ReadState();
    snapshot.failure = failure_;
    snapshot.activation = activation_;
    snapshot.activationSystemError = activationSystemError_;
    snapshot.manifestLoad = manifestLoad_;
    snapshot.manifestSequence = manifest_.sequence;
    return snapshot;
}

void SecureClientRuntime::RetainSessionSnapshot(
    const SecureClientSessionSnapshot& snapshot) noexcept {
    AcquireSRWLockExclusive(&lastSessionLock_);
    if (lastSession_.generation !=
        (std::numeric_limits<std::uint64_t>::max)()) {
        ++lastSession_.generation;
    }
    lastSession_.available = true;
    lastSession_.session = snapshot;
    ReleaseSRWLockExclusive(&lastSessionLock_);
}

SecureClientSessionRetentionSnapshot
SecureClientRuntime::LastSessionSnapshot() const noexcept {
    AcquireSRWLockShared(&lastSessionLock_);
    const auto snapshot = lastSession_;
    ReleaseSRWLockShared(&lastSessionLock_);
    return snapshot;
}

BOOL CALLBACK SecureClientRuntime::InitializeOnce(
    PINIT_ONCE,
    PVOID parameter,
    PVOID*) noexcept {
    auto* call = static_cast<InitializationCall*>(parameter);
    if (call == nullptr || call->runtime == nullptr) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return FALSE;
    }
    call->runtime->InitializeCore(call->module);
    return TRUE;
}

ClientRouteDecision SecureClientRuntime::ClassifyCallback(
    void* context,
    NativeProxyId,
    const ClientRoute& route) noexcept {
    auto* runtime = static_cast<SecureClientRuntime*>(context);
    return runtime != nullptr
        ? runtime->ClassifyRoute(route)
        : ClientRouteDecision::Reject;
}

bool SecureClientRuntime::LookupManifestKey(
    void* context,
    std::uint16_t publicKeyId,
    EndpointManifestPublicKey* publicKey) noexcept {
    auto* runtime = static_cast<SecureClientRuntime*>(context);
    if (runtime == nullptr) {
        return false;
    }
    if (runtime->dependencies_.publicKeyLookup != nullptr) {
        return runtime->dependencies_.publicKeyLookup(
            runtime->dependencies_.publicKeyContext,
            publicKeyId,
            publicKey);
    }
    return TryLookupEmbeddedSecureClientManifestPublicKey(
        runtime->activation_.environment,
        publicKeyId,
        publicKey);
}

bool SecureClientRuntime::LookupSequenceFloors(
    void* context,
    EndpointManifestEnvironment environment,
    std::uint64_t* compiledMinimum,
    std::uint64_t* installedMinimum) noexcept {
    auto* runtime = static_cast<SecureClientRuntime*>(context);
    if (runtime == nullptr ||
        compiledMinimum == nullptr ||
        installedMinimum == nullptr ||
        environment != runtime->activation_.environment ||
        !TryGetCompiledSecureClientManifestSequenceFloor(
            environment,
            compiledMinimum)) {
        return false;
    }
    *installedMinimum =
        runtime->activation_.installedMinimumSequence;
    return true;
}

bool SecureClientRuntime::ReadManifestClock(
    void* context,
    std::uint64_t* unixSeconds) noexcept {
    if (unixSeconds == nullptr) {
        return false;
    }
    auto* runtime = static_cast<SecureClientRuntime*>(context);
    std::uint64_t milliseconds = 0;
    if (runtime == nullptr ||
        !runtime->TryReadClock(&milliseconds)) {
        return false;
    }
    *unixSeconds = milliseconds / 1000;
    return true;
}

bool SecureClientRuntime::ReadGrantClock(
    void* context,
    std::uint64_t* unixMilliseconds) noexcept {
    auto* runtime = static_cast<SecureClientRuntime*>(context);
    return runtime != nullptr &&
        runtime->TryReadClock(unixMilliseconds);
}

void SecureClientRuntime::InitializeCore(HMODULE module) noexcept {
    const auto activationReader =
        dependencies_.activationReader != nullptr
            ? dependencies_.activationReader
            : ReadInstalledSecureClientActivation;
    SecureClientActivationRecord activation{};
    DWORD systemError = ERROR_SUCCESS;
    if (activationReader(
            dependencies_.activationContext,
            &activation,
            &systemError) !=
        SecureClientActivationReadResult::Success) {
        Fail(
            SecureClientRuntimeFailure::ActivationRead,
            systemError);
        return;
    }
    activation_ = activation;
    activationSystemError_ = systemError;

    if (activation.mode == SecureClientActivationMode::Disabled) {
        InterlockedExchange(
            &state_,
            static_cast<LONG>(
                SecureClientRuntimeState::Disabled));
        return;
    }
    if (activation.mode !=
            SecureClientActivationMode::SecureRequired ||
        activation.installedMinimumSequence == 0) {
        Fail(SecureClientRuntimeFailure::InvalidActivation);
        return;
    }
    if (module == nullptr) {
        Fail(SecureClientRuntimeFailure::ModuleUnavailable);
        return;
    }

    EndpointManifestValidationContext validation{};
    validation.context = this;
    validation.publicKeyLookup = LookupManifestKey;
    validation.sequenceFloorLookup = LookupSequenceFloors;
    validation.clock = ReadManifestClock;
    validation.expectedEnvironment = activation.environment;

    EndpointManifest candidate{};
    if (dependencies_.manifestReader != nullptr) {
        manifestLoad_ = dependencies_.manifestReader(
            dependencies_.manifestContext,
            module,
            validation,
            &candidate);
    } else {
        EndpointManifestLoadContext loadContext{};
        loadContext.module = module;
        loadContext.validation = validation;
        manifestLoad_ =
            defaultManifestLoader_.LoadOnce(loadContext);
        static_cast<void>(
            defaultManifestLoader_.TryCopyManifest(&candidate));
    }
    if (manifestLoad_.loadError !=
            EndpointManifestLoadError::Success ||
        manifestLoad_.validationError !=
            EndpointManifestError::Success) {
        Fail(SecureClientRuntimeFailure::ManifestLoad);
        return;
    }
    if (candidate.environment != activation.environment ||
        candidate.sequence <
            activation.installedMinimumSequence ||
        candidate.logicalLoginHost.length == 0 ||
        candidate.logicalLoginPort == 0 ||
        candidate.tlsLoginHost.length == 0 ||
        candidate.tlsLoginPort == 0) {
        Fail(SecureClientRuntimeFailure::ManifestPolicy);
        return;
    }
    manifest_ = candidate;

    const auto randomGenerator =
        dependencies_.randomGenerator != nullptr
            ? dependencies_.randomGenerator
            : [](void*, void* destination, std::size_t bytes) noexcept {
                return GenerateSystemSecureRandom(
                    destination,
                    bytes);
            };
    bool generated = false;
    for (unsigned attempt = 0;
         attempt < RandomGenerationAttempts && !generated;
         ++attempt) {
        SecureZeroMemory(
            clientInstanceId_,
            sizeof(clientInstanceId_));
        generated = randomGenerator(
                dependencies_.randomContext,
                clientInstanceId_,
                sizeof(clientInstanceId_)) &&
            ContainsNonzero(
                clientInstanceId_,
                sizeof(clientInstanceId_));
    }
    if (!generated) {
        Fail(SecureClientRuntimeFailure::RandomGeneration);
        return;
    }

    grantRegistry_ = new (std::nothrow) SecureGameGrantRegistry(
        SecureGameGrantPolicy{
            manifest_,
            this,
            ReadGrantClock});
    if (grantRegistry_ == nullptr) {
        Fail(
            SecureClientRuntimeFailure::GrantRegistryAllocation,
            ERROR_NOT_ENOUGH_MEMORY);
        return;
    }
    InterlockedExchange(
        &state_,
        static_cast<LONG>(
            SecureClientRuntimeState::SecureRequiredReady));
}

bool SecureClientRuntime::TryReadClock(
    std::uint64_t* unixMilliseconds) const noexcept {
    if (dependencies_.clock != nullptr) {
        return dependencies_.clock(
            dependencies_.clockContext,
            unixMilliseconds);
    }
    return ReadSystemUnixMilliseconds(unixMilliseconds);
}

void SecureClientRuntime::Fail(
    SecureClientRuntimeFailure failure,
    DWORD systemError) noexcept {
    delete grantRegistry_;
    grantRegistry_ = nullptr;
    manifest_ = EndpointManifest{};
    SecureZeroMemory(
        clientInstanceId_,
        sizeof(clientInstanceId_));
    failure_ = failure;
    if (systemError != ERROR_SUCCESS) {
        activationSystemError_ = systemError;
    }
    InterlockedExchange(
        &state_,
        static_cast<LONG>(
            SecureClientRuntimeState::FailedClosed));
}

SecureClientRuntimeState SecureClientRuntime::ReadState() const noexcept {
    return static_cast<SecureClientRuntimeState>(
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&state_),
            0,
            0));
}

SecureClientRuntime& ProcessSecureClientRuntime() noexcept {
    static SecureClientRuntime runtime;
    return runtime;
}

bool EnsureProcessSecureClientRuntimeInitialized(
    HMODULE module) noexcept {
    return ProcessSecureClientRuntime().Initialize(module);
}

} // namespace godswar::network
