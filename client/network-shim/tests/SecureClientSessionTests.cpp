#include "SecureClientSessionTests.h"

#include "SecureGameControlTestSupport.h"

#include "../src/SecureClientSession.h"

#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::ClientBridgePlan;
using godswar::network::ClientEndpointRole;
using godswar::network::ClientRouteDecision;
using godswar::network::ILegacyNetClient;
using godswar::network::SecureClientSession;
using godswar::network::SecureClientSessionConfiguration;
using godswar::network::SecureClientSessionFailure;
using godswar::network::SecureClientSessionState;
using godswar::network::SecureGameGrantPolicy;
using godswar::network::SecureGameGrantRegistry;
using godswar::network::TryCopyClientRoute;
using godswar::network::tests::BuildSecureGrantTestManifest;
using godswar::network::tests::SecureGrantTestClock;
using godswar::network::tests::TestClock;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

class ProbeLegacyClient final : public ILegacyNetClient {
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

SecureClientSessionConfiguration Configuration(
    SecureGameGrantRegistry* registry,
    bool validIdentity = true) noexcept {
    SecureClientSessionConfiguration configuration{};
    configuration.manifest = BuildSecureGrantTestManifest();
    configuration.grantRegistry = registry;
    if (validIdentity) {
        for (std::size_t index = 0;
             index <
                 sizeof(configuration.clientInstanceId);
             ++index) {
            configuration.clientInstanceId[index] =
                static_cast<std::uint8_t>(index + 1);
        }
        for (std::size_t index = 0;
             index < sizeof(configuration.originSha256);
             ++index) {
            configuration.originSha256[index] =
                static_cast<std::uint8_t>(0x40 + index);
        }
    }
    return configuration;
}

ClientBridgePlan LoginPlan() noexcept {
    ClientBridgePlan plan{};
    plan.proxyId = 7;
    plan.generation = 11;
    plan.decision = ClientRouteDecision::Login;
    plan.role = ClientEndpointRole::Login;
    static_cast<void>(TryCopyClientRoute(
        "127.1.1.110",
        5999,
        &plan.logicalRoute));
    return plan;
}

ClientBridgePlan GamePlan() noexcept {
    auto plan = LoginPlan();
    plan.decision = ClientRouteDecision::Game;
    plan.role = ClientEndpointRole::Game;
    static_cast<void>(TryCopyClientRoute(
        "game-route.reborn.test",
        7000,
        &plan.logicalRoute));
    return plan;
}

void CheckInvalidIdentityNeverTouchesStock() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(SecureGameGrantPolicy{
        BuildSecureGrantTestManifest(),
        &clock,
        TestClock});
    SecureClientSession session(Configuration(&registry, false));
    ProbeLegacyClient legacy;
    Check(
        !session.Connect(&legacy, LoginPlan()),
        "zero client identity was accepted");
    const auto snapshot = session.Snapshot();
    Check(
        snapshot.state == SecureClientSessionState::Failed &&
            snapshot.failure ==
                SecureClientSessionFailure::InvalidArgument,
        "invalid identity returned unstable session state");
    Check(
        legacy.setHostCalls == 0 &&
            legacy.connectCalls == 0 &&
            legacy.disconnectCalls == 0,
        "invalid secure identity touched the stock transport");
}

void CheckInvalidLoginTargetNeverTouchesStock() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(SecureGameGrantPolicy{
        BuildSecureGrantTestManifest(),
        &clock,
        TestClock});
    auto configuration = Configuration(&registry);
    configuration.manifest.tlsLoginHost = {};
    SecureClientSession session(configuration);
    ProbeLegacyClient legacy;
    Check(
        !session.Connect(&legacy, LoginPlan()),
        "empty TLS login target was accepted");
    Check(
        session.Snapshot().failure ==
                SecureClientSessionFailure::TargetName &&
            legacy.setHostCalls == 0 &&
            legacy.connectCalls == 0 &&
            legacy.disconnectCalls == 0,
        "invalid TLS login target reached the stock transport");
}

void CheckMissingGameGrantNeverTouchesStock() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(SecureGameGrantPolicy{
        BuildSecureGrantTestManifest(),
        &clock,
        TestClock});
    SecureClientSession session(Configuration(&registry));
    ProbeLegacyClient legacy;
    Check(
        !session.Connect(&legacy, GamePlan()),
        "game session connected without an authenticated grant");
    Check(
        session.Snapshot().failure ==
                SecureClientSessionFailure::GameClaim &&
            legacy.setHostCalls == 0 &&
            legacy.connectCalls == 0 &&
            legacy.disconnectCalls == 0,
        "missing game grant reached the stock transport");
}

} // namespace

int RunSecureClientSessionTests() {
    Failures = 0;
    CheckInvalidIdentityNeverTouchesStock();
    CheckInvalidLoginTargetNeverTouchesStock();
    CheckMissingGameGrantNeverTouchesStock();
    return Failures;
}
