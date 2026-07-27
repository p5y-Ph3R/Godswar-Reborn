#include "SecureClientRuntimeTests.h"

#include "SecureGameControlTestSupport.h"

#include "../src/NetClientProxy.h"
#include "../src/SecureClientManifestBuildContract.h"
#include "../src/SecureClientRuntime.h"

#include <Windows.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <utility>

namespace {

using godswar::network::ClientRoute;
using godswar::network::ClientRouteDecision;
using godswar::network::ILegacyNetClient;
using godswar::network::NativeClientCoordinator;
using godswar::network::NetClientProxy;
using godswar::network::EndpointManifest;
using godswar::network::EndpointManifestEnvironment;
using godswar::network::EndpointManifestLoadError;
using godswar::network::EndpointManifestLoadResult;
using godswar::network::EndpointManifestPublicKey;
using godswar::network::SecureClientActivationMode;
using godswar::network::SecureClientActivationReadResult;
using godswar::network::SecureClientActivationRecord;
using godswar::network::SecureClientRuntime;
using godswar::network::SecureClientRuntimeDependencies;
using godswar::network::SecureClientRuntimeFailure;
using godswar::network::SecureClientRuntimeState;
using godswar::network::SecureClientSessionFailure;
using godswar::network::SecureClientSessionSnapshot;
using godswar::network::SecureClientSessionState;
using godswar::network::SecureGameGrantClaim;
using godswar::network::SecureGameGrantResult;
using godswar::network::TryCopyClientRoute;
using godswar::network::TryGetCompiledSecureClientManifestSequenceFloor;
using godswar::network::TryLookupEmbeddedSecureClientManifestPublicKey;
using godswar::network::tests::BuildSecureGrantTestBytes;
using godswar::network::tests::BuildSecureGrantTestManifest;
using godswar::network::tests::DecodeSecureGrantForTest;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

class RetentionProbeLegacyClient final : public ILegacyNetClient {
public:
    std::uint32_t Release() override {
        ++releaseCalls;
        return 1;
    }
    void SetHost(const char*, std::uint16_t) override {
        ++setHostCalls;
    }
    bool Connect() override {
        ++connectCalls;
        return true;
    }
    void DisConnect() override {
        ++disconnectCalls;
    }
    void Process() override {
    }
    std::uint32_t GetStatus() const override {
        return 0;
    }
    void* PickMsg() override {
        return nullptr;
    }
    bool SendMsg(const void*, int) override {
        return false;
    }
    long GetMsgNum() override {
        return 0;
    }

    int releaseCalls = 0;
    int setHostCalls = 0;
    int connectCalls = 0;
    int disconnectCalls = 0;
};

struct RuntimeFixture final {
    SecureClientActivationReadResult activationResult =
        SecureClientActivationReadResult::Success;
    SecureClientActivationRecord activation{};
    EndpointManifestLoadResult manifestResult{
        EndpointManifestLoadError::Success,
        godswar::network::EndpointManifestError::Success,
        ERROR_SUCCESS};
    EndpointManifest manifest{};
    bool randomAvailable = true;
    bool randomZero = false;
    bool clockAvailable = true;
    std::uint64_t now = 1'000;
};

SecureClientActivationReadResult ReadActivation(
    void* raw,
    SecureClientActivationRecord* activation,
    DWORD* systemError) noexcept {
    auto* fixture = static_cast<RuntimeFixture*>(raw);
    if (fixture == nullptr ||
        activation == nullptr ||
        systemError == nullptr) {
        return SecureClientActivationReadResult::Failed;
    }
    *activation = fixture->activation;
    *systemError =
        fixture->activationResult ==
            SecureClientActivationReadResult::Success
        ? ERROR_SUCCESS
        : ERROR_ACCESS_DENIED;
    return fixture->activationResult;
}

EndpointManifestLoadResult ReadManifest(
    void* raw,
    HMODULE,
    const godswar::network::EndpointManifestValidationContext&,
    EndpointManifest* manifest) noexcept {
    auto* fixture = static_cast<RuntimeFixture*>(raw);
    if (fixture == nullptr || manifest == nullptr) {
        return EndpointManifestLoadResult{};
    }
    *manifest = fixture->manifest;
    return fixture->manifestResult;
}

bool FillRandom(
    void* raw,
    void* destination,
    std::size_t destinationBytes) noexcept {
    auto* fixture = static_cast<RuntimeFixture*>(raw);
    if (fixture == nullptr ||
        destination == nullptr ||
        !fixture->randomAvailable) {
        return false;
    }
    auto* bytes = static_cast<std::uint8_t*>(destination);
    for (std::size_t index = 0;
         index < destinationBytes;
         ++index) {
        bytes[index] = fixture->randomZero
            ? 0
            : static_cast<std::uint8_t>(index + 1);
    }
    return true;
}

bool ReadClock(
    void* raw,
    std::uint64_t* milliseconds) noexcept {
    auto* fixture = static_cast<RuntimeFixture*>(raw);
    if (fixture == nullptr ||
        milliseconds == nullptr ||
        !fixture->clockAvailable) {
        return false;
    }
    *milliseconds = fixture->now;
    return true;
}

SecureClientRuntimeDependencies Dependencies(
    RuntimeFixture* fixture) noexcept {
    SecureClientRuntimeDependencies dependencies{};
    dependencies.activationContext = fixture;
    dependencies.activationReader = ReadActivation;
    dependencies.manifestContext = fixture;
    dependencies.manifestReader = ReadManifest;
    dependencies.randomContext = fixture;
    dependencies.randomGenerator = FillRandom;
    dependencies.clockContext = fixture;
    dependencies.clock = ReadClock;
    return dependencies;
}

RuntimeFixture ReadyFixture() noexcept {
    RuntimeFixture fixture{};
    fixture.activation.mode =
        SecureClientActivationMode::SecureRequired;
    fixture.activation.environment =
        EndpointManifestEnvironment::Development;
    fixture.activation.installedMinimumSequence = 7;
    fixture.manifest = BuildSecureGrantTestManifest();
    fixture.manifest.environment =
        EndpointManifestEnvironment::Development;
    fixture.manifest.sequence = 7;
    return fixture;
}

ClientRoute Route(
    const char* host,
    std::uint16_t port) noexcept {
    ClientRoute route{};
    static_cast<void>(TryCopyClientRoute(host, port, &route));
    return route;
}

void CheckDisabledAndFailedClosedModes() {
    RuntimeFixture disabled{};
    disabled.activation.mode =
        SecureClientActivationMode::Disabled;
    SecureClientRuntime disabledRuntime(Dependencies(&disabled));
    Check(
        disabledRuntime.Initialize(nullptr) &&
            disabledRuntime.Snapshot().state ==
                SecureClientRuntimeState::Disabled &&
            disabledRuntime.ClassifyRoute(
                Route("anything.example", 5999)) ==
                ClientRouteDecision::PassThrough,
        "explicit Disabled activation did not preserve raw baseline");

    RuntimeFixture unreadable{};
    unreadable.activationResult =
        SecureClientActivationReadResult::Failed;
    SecureClientRuntime failedRuntime(Dependencies(&unreadable));
    Check(
        !failedRuntime.Initialize(nullptr) &&
            failedRuntime.Snapshot().state ==
                SecureClientRuntimeState::FailedClosed &&
            failedRuntime.Snapshot().failure ==
                SecureClientRuntimeFailure::ActivationRead &&
            failedRuntime.ClassifyRoute(
                Route("login-route.reborn.test", 5999)) ==
                ClientRouteDecision::Reject,
        "activation-read failure did not fail closed");
}

void CheckReadyRoutesAndIdentity() {
    constexpr std::uint8_t ExpectedOriginSha256[32] = {
        0xE1, 0x77, 0xD9, 0x4D, 0xC7, 0x0C, 0xCF, 0x65,
        0x7D, 0x19, 0x0C, 0x85, 0xB1, 0xEB, 0xAC, 0xE5,
        0xC8, 0xE7, 0x90, 0xD5, 0x2D, 0xBC, 0x01, 0x48,
        0x54, 0xE0, 0x3A, 0x57, 0x23, 0x4C, 0xC7, 0x6C,
    };
    auto fixture = ReadyFixture();
    SecureClientRuntime runtime(Dependencies(&fixture));
    Check(
        runtime.Initialize(reinterpret_cast<HMODULE>(1)) &&
            runtime.Snapshot().state ==
                SecureClientRuntimeState::SecureRequiredReady,
        "valid injected secure activation did not become ready");
    Check(
        runtime.ClassifyRoute(
            Route("login-route.reborn.test", 5999)) ==
                ClientRouteDecision::Login &&
            runtime.ClassifyRoute(
                Route("login-route.reborn.test", 6000)) ==
                ClientRouteDecision::Reject &&
            runtime.ClassifyRoute(
                Route("game-route.reborn.test", 7000)) ==
                ClientRouteDecision::Reject,
        "secure route classification was not exact and grant-gated");

    std::uint8_t instance[16]{};
    std::uint8_t origin[32]{};
    EndpointManifest manifest{};
    const auto& buildContract =
        godswar::network::GetSecureClientManifestBuildContract();
    Check(
        runtime.TryCopyManifest(&manifest) &&
            runtime.TryCopyClientInstanceId(
                instance,
                sizeof(instance)) &&
            runtime.TryCopyOriginSha256(
                origin,
                sizeof(origin)) &&
            instance[0] == 1 &&
            instance[15] == 16 &&
            godswar::network::
                IsValidSecureClientManifestBuildContract(
                    buildContract) &&
            std::memcmp(
                buildContract.originSha256,
                ExpectedOriginSha256,
                sizeof(ExpectedOriginSha256)) == 0 &&
            std::memcmp(
                origin,
                buildContract.originSha256,
                sizeof(origin)) == 0 &&
            manifest.sequence == 7 &&
            runtime.GrantRegistry() != nullptr,
        "ready runtime did not publish immutable session inputs");

    auto grant = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
    Check(
        runtime.GrantRegistry()->Commit(std::move(grant)) ==
                SecureGameGrantResult::Success &&
            runtime.ClassifyRoute(
                Route("game-route.reborn.test", 7000)) ==
                ClientRouteDecision::Game,
        "authenticated pending redirect did not enable Game route");

    SecureGameGrantClaim claim{};
    Check(
        runtime.GrantRegistry()->Claim(
            9,
            2,
            Route("game-route.reborn.test", 7000),
            &claim) == SecureGameGrantResult::Success &&
            runtime.ClassifyRoute(
                Route("game-route.reborn.test", 7000)) ==
                ClientRouteDecision::Reject,
        "claimed single-use redirect remained routable");
}

void CheckInitializationFailures() {
    auto noModule = ReadyFixture();
    SecureClientRuntime noModuleRuntime(Dependencies(&noModule));
    Check(
        !noModuleRuntime.Initialize(nullptr) &&
            noModuleRuntime.Snapshot().failure ==
                SecureClientRuntimeFailure::ModuleUnavailable,
        "SecureRequired accepted a missing shim module");

    auto badManifest = ReadyFixture();
    badManifest.manifestResult.loadError =
        EndpointManifestLoadError::ValidationFailed;
    SecureClientRuntime badManifestRuntime(
        Dependencies(&badManifest));
    Check(
        !badManifestRuntime.Initialize(
            reinterpret_cast<HMODULE>(1)) &&
            badManifestRuntime.Snapshot().failure ==
                SecureClientRuntimeFailure::ManifestLoad,
        "manifest validation failure did not fail closed");

    auto zeroRandom = ReadyFixture();
    zeroRandom.randomZero = true;
    SecureClientRuntime zeroRandomRuntime(
        Dependencies(&zeroRandom));
    Check(
        !zeroRandomRuntime.Initialize(
            reinterpret_cast<HMODULE>(1)) &&
            zeroRandomRuntime.Snapshot().failure ==
                SecureClientRuntimeFailure::RandomGeneration,
        "all-zero process identity was accepted");
}

void CheckEmbeddedTrustBoundary() {
    EndpointManifestPublicKey key{};
    std::uint64_t floor = 0;
    Check(
        TryLookupEmbeddedSecureClientManifestPublicKey(
            EndpointManifestEnvironment::Development,
            godswar::network::
                SecureClientDevelopmentCurrentManifestKeyId,
            &key) &&
            TryGetCompiledSecureClientManifestSequenceFloor(
                EndpointManifestEnvironment::Development,
                &floor) &&
            floor == 1,
        "development public-only trust seam is unavailable");
    Check(
        !TryLookupEmbeddedSecureClientManifestPublicKey(
            EndpointManifestEnvironment::Production,
            godswar::network::
                SecureClientDevelopmentCurrentManifestKeyId,
            &key) &&
            !TryGetCompiledSecureClientManifestSequenceFloor(
                EndpointManifestEnvironment::Production,
                &floor),
        "placeholder development trust leaked into production");
}

void CheckBoundedSessionSnapshotRetention() {
    SecureClientRuntime runtime;
    Check(
        !runtime.LastSessionSnapshot().available &&
            runtime.LastSessionSnapshot().generation == 0,
        "empty runtime exposed a retained session snapshot");

    SecureClientSessionSnapshot first{};
    first.state = SecureClientSessionState::Failed;
    first.failure = SecureClientSessionFailure::TlsHandshake;
    first.tls.failure =
        godswar::network::SchannelClientFailure::CertificatePolicy;
    runtime.RetainSessionSnapshot(first);

    const auto retainedFirst = runtime.LastSessionSnapshot();
    Check(
        retainedFirst.available &&
            retainedFirst.generation == 1 &&
            retainedFirst.session.failure ==
                SecureClientSessionFailure::TlsHandshake &&
            retainedFirst.session.tls.failure ==
                godswar::network::SchannelClientFailure::
                    CertificatePolicy,
        "runtime did not retain the first failure snapshot");

    SecureClientSessionSnapshot second{};
    second.state = SecureClientSessionState::Failed;
    second.failure =
        SecureClientSessionFailure::BridgeTerminated;
    second.bridge.state =
        godswar::network::NativeBridgeState::Failed;
    second.bridge.failure =
        godswar::network::NativeBridgeFailure::PumpTerminated;
    runtime.RetainSessionSnapshot(second);

    const auto retainedSecond = runtime.LastSessionSnapshot();
    Check(
        retainedSecond.available &&
            retainedSecond.generation == 2 &&
            retainedSecond.session.failure ==
                SecureClientSessionFailure::BridgeTerminated &&
            retainedSecond.session.bridge.failure ==
                godswar::network::NativeBridgeFailure::
                    PumpTerminated,
        "runtime retention was not bounded to the newest snapshot");
}

void CheckFailedProxyConnectSurvivesSessionDeletion() {
    auto fixture = ReadyFixture();
    fixture.manifest.tlsLoginHost.bytes[0] = ' ';
    SecureClientRuntime runtime(Dependencies(&fixture));
    Check(
        runtime.Initialize(reinterpret_cast<HMODULE>(1)),
        "retained-connect runtime fixture did not initialize");

    NativeClientCoordinator coordinator(runtime.RoutePolicy());
    RetentionProbeLegacyClient legacy;
    auto* client = NetClientProxy::CreateWithRuntimeForTesting(
        &legacy,
        &coordinator,
        &runtime);
    Check(client != nullptr, "retained-connect proxy was not created");
    if (client == nullptr) {
        return;
    }

    client->SetHost("login-route.reborn.test", 5999);
    Check(
        !client->Connect(),
        "invalid secure target unexpectedly connected");
    const auto retained = runtime.LastSessionSnapshot();
    Check(
        retained.available &&
            retained.generation == 1 &&
            retained.session.state ==
                SecureClientSessionState::Failed &&
            retained.session.failure ==
                SecureClientSessionFailure::TargetName,
        "failed ConnectSecure snapshot did not survive deletion");
    Check(
        legacy.setHostCalls == 0 &&
            legacy.connectCalls == 0 &&
            legacy.disconnectCalls == 0,
        "failed secure connect touched the stock endpoint");

    static_cast<void>(client->Release());
    Check(
        legacy.releaseCalls == 1 &&
            runtime.LastSessionSnapshot().generation == 1,
        "proxy release changed retained failure diagnostics");
}

} // namespace

int RunSecureClientRuntimeTests() {
    Failures = 0;
    CheckDisabledAndFailedClosedModes();
    CheckReadyRoutesAndIdentity();
    CheckInitializationFailures();
    CheckEmbeddedTrustBoundary();
    CheckBoundedSessionSnapshotRetention();
    CheckFailedProxyConnectSurvivesSessionDeletion();
    return Failures;
}
