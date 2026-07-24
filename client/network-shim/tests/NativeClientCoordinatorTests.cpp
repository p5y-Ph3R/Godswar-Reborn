#include "NativeClientCoordinatorTests.h"

#include "../src/NativeClientCoordinator.h"

#include <Windows.h>
#include <process.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace {

using godswar::network::ClientBridgePlan;
using godswar::network::ClientEndpointRole;
using godswar::network::ClientRoute;
using godswar::network::ClientRouteDecision;
using godswar::network::ClientRoutePolicy;
using godswar::network::ClientRoutesEqual;
using godswar::network::NativeClientCoordinator;
using godswar::network::NativeClientRegistryCapacity;
using godswar::network::NativeClientSnapshot;
using godswar::network::NativeClientState;
using godswar::network::NativeCoordinatorResult;
using godswar::network::NativeProxyId;
using godswar::network::NativeRouteHostCapacity;
using godswar::network::NativeRouteHostMaximumBytes;
using godswar::network::TryCopyClientRoute;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (condition) {
        return;
    }

    std::fprintf(stderr, "FAIL: %s\n", message);
    ++Failures;
}

struct ExactPolicyContext final {
    ClientRoute login{};
    ClientRoute game{};
    NativeClientCoordinator* reentrantCoordinator = nullptr;
    bool observedUnlockedCoordinator = false;
};

ClientRouteDecision ClassifyExactRoute(
    void* rawContext,
    NativeProxyId,
    const ClientRoute& route) noexcept {
    auto* context = static_cast<ExactPolicyContext*>(rawContext);
    if (context->reentrantCoordinator != nullptr) {
        static_cast<void>(context->reentrantCoordinator->Snapshot());
        context->observedUnlockedCoordinator = true;
    }

    if (ClientRoutesEqual(route, context->login)) {
        return ClientRouteDecision::Login;
    }
    if (ClientRoutesEqual(route, context->game)) {
        return ClientRouteDecision::Game;
    }

    return ClientRouteDecision::Reject;
}

ClientRouteDecision ReturnUnknownDecision(
    void*,
    NativeProxyId,
    const ClientRoute&) noexcept {
    return static_cast<ClientRouteDecision>(255);
}

void RunRouteCopyChecks() {
    ClientRoute route{};
    Check(
        !TryCopyClientRoute(nullptr, 5999, &route),
        "null route host was accepted");
    Check(
        !TryCopyClientRoute("", 5999, &route),
        "empty route host was accepted");
    Check(
        !TryCopyClientRoute("login.example", 0, &route),
        "zero route port was accepted");
    Check(
        !TryCopyClientRoute("login.example", 5999, nullptr),
        "null route destination was accepted");

    char maximum[NativeRouteHostCapacity]{};
    std::memset(maximum, 'a', NativeRouteHostMaximumBytes);
    maximum[NativeRouteHostMaximumBytes] = '\0';
    Check(
        TryCopyClientRoute(maximum, 5999, &route),
        "maximum-length route host was rejected");
    Check(
        route.hostLength == NativeRouteHostMaximumBytes,
        "maximum route host length changed");

    char unterminated[NativeRouteHostCapacity]{};
    std::memset(unterminated, 'b', sizeof(unterminated));
    Check(
        !TryCopyClientRoute(unterminated, 5999, &route),
        "unterminated route host was accepted");

    char overlong[NativeRouteHostCapacity + 1]{};
    std::memset(overlong, 'c', NativeRouteHostCapacity);
    overlong[NativeRouteHostCapacity] = '\0';
    Check(
        !TryCopyClientRoute(overlong, 5999, &route),
        "overlong route host was accepted");
}

void RunDefaultPolicyChecks() {
    NativeClientCoordinator coordinator;
    NativeProxyId id = 0;
    Check(
        coordinator.Register(&id) == NativeCoordinatorResult::Success &&
            id != 0,
        "default coordinator registration failed");
    Check(
        coordinator.SetHost(id, "legacy.example", 5999) ==
            NativeCoordinatorResult::Success,
        "default coordinator SetHost failed");

    ClientBridgePlan plan{};
    Check(
        coordinator.BeginConnect(id, &plan) ==
            NativeCoordinatorResult::Success,
        "default coordinator BeginConnect failed");
    Check(
        plan.decision == ClientRouteDecision::PassThrough &&
            plan.role == ClientEndpointRole::None,
        "disabled policy did not explicitly select pass-through");
    Check(
        coordinator.MarkConnected(plan) ==
            NativeCoordinatorResult::Success,
        "default coordinator connected marker failed");
    Check(
        coordinator.Unregister(id) == NativeCoordinatorResult::Success &&
            coordinator.Unregister(id) ==
                NativeCoordinatorResult::Success,
        "coordinator unregister was not idempotent");
}

void RunExactPolicyChecks() {
    ExactPolicyContext context{};
    Check(
        TryCopyClientRoute(
            "login.reborn.test",
            5999,
            &context.login),
        "test login route could not be created");
    Check(
        TryCopyClientRoute(
            "game.reborn.test",
            7000,
            &context.game),
        "test game route could not be created");

    NativeClientCoordinator coordinator(ClientRoutePolicy{
        &context,
        ClassifyExactRoute,
    });
    context.reentrantCoordinator = &coordinator;

    NativeProxyId loginId = 0;
    NativeProxyId gameId = 0;
    NativeProxyId wrongId = 0;
    Check(
        coordinator.Register(&loginId) ==
                NativeCoordinatorResult::Success &&
            coordinator.Register(&gameId) ==
                NativeCoordinatorResult::Success &&
            coordinator.Register(&wrongId) ==
                NativeCoordinatorResult::Success,
        "exact-policy registrations failed");

    char mutableLogin[] = "login.reborn.test";
    Check(
        coordinator.SetHost(loginId, mutableLogin, 5999) ==
            NativeCoordinatorResult::Success,
        "exact login route was rejected");
    mutableLogin[0] = 'X';

    NativeClientSnapshot snapshot{};
    Check(
        coordinator.TryGetSnapshot(loginId, &snapshot) &&
            std::strcmp(snapshot.route.host, "login.reborn.test") == 0,
        "SetHost did not take immediate route ownership");
    Check(
        snapshot.decision == ClientRouteDecision::Login &&
            snapshot.state == NativeClientState::HostReady,
        "login route classification or state changed");
    Check(
        context.observedUnlockedCoordinator,
        "route policy could not re-enter coordinator snapshot");

    ClientBridgePlan loginPlan{};
    Check(
        coordinator.BeginConnect(loginId, &loginPlan) ==
                NativeCoordinatorResult::Success &&
            loginPlan.decision == ClientRouteDecision::Login &&
            loginPlan.role == ClientEndpointRole::Login,
        "login bridge plan changed");

    Check(
        coordinator.SetHost(gameId, "game.reborn.test", 7000) ==
            NativeCoordinatorResult::Success,
        "exact game route was rejected");
    ClientBridgePlan gamePlan{};
    Check(
        coordinator.BeginConnect(gameId, &gamePlan) ==
                NativeCoordinatorResult::Success &&
            gamePlan.decision == ClientRouteDecision::Game &&
            gamePlan.role == ClientEndpointRole::Game,
        "game bridge plan changed");

    Check(
        coordinator.SetHost(wrongId, "game.reborn.test", 7001) ==
            NativeCoordinatorResult::Success,
        "unknown route could not be recorded");
    ClientBridgePlan rejectedPlan{};
    Check(
        coordinator.BeginConnect(wrongId, &rejectedPlan) ==
                NativeCoordinatorResult::RouteRejected &&
            rejectedPlan.decision == ClientRouteDecision::Reject,
        "wrong host/port route did not fail closed");
}

void RunStateChecks() {
    NativeClientCoordinator coordinator;
    NativeProxyId id = 0;
    Check(
        coordinator.Register(&id) == NativeCoordinatorResult::Success,
        "state-test registration failed");

    NativeClientSnapshot initial{};
    Check(
        coordinator.TryGetSnapshot(id, &initial) &&
            initial.state == NativeClientState::Registered,
        "new proxy was not registered");
    Check(
        coordinator.SetHost(id, "legacy.example", 5999) ==
            NativeCoordinatorResult::Success,
        "state-test SetHost failed");

    ClientBridgePlan plan{};
    Check(
        coordinator.BeginConnect(id, &plan) ==
            NativeCoordinatorResult::Success,
        "state-test BeginConnect failed");

    ClientBridgePlan duplicate{};
    Check(
        coordinator.BeginConnect(id, &duplicate) ==
            NativeCoordinatorResult::InvalidState,
        "duplicate BeginConnect was accepted");
    Check(
        coordinator.SetHost(id, "other.example", 7000) ==
            NativeCoordinatorResult::InvalidState,
        "SetHost changed an active connection");

    auto alteredPlan = plan;
    alteredPlan.logicalRoute.host[0] = 'X';
    Check(
        coordinator.MarkConnected(alteredPlan) ==
            NativeCoordinatorResult::OperationSuperseded,
        "altered connection plan was accepted");

    auto stalePlan = plan;
    ++stalePlan.generation;
    Check(
        coordinator.MarkConnected(stalePlan) ==
            NativeCoordinatorResult::OperationSuperseded,
        "wrong connection generation was accepted");
    Check(
        coordinator.MarkConnected(plan) ==
            NativeCoordinatorResult::Success,
        "connected marker failed");
    Check(
        coordinator.MarkConnected(plan) ==
            NativeCoordinatorResult::InvalidState,
        "duplicate connected marker was accepted");

    Check(
        coordinator.Reset(id) == NativeCoordinatorResult::Success &&
            coordinator.Reset(id) == NativeCoordinatorResult::Success,
        "registered proxy reset was not idempotent");
    NativeClientSnapshot reset{};
    Check(
        coordinator.TryGetSnapshot(id, &reset) &&
            reset.state == NativeClientState::Registered &&
            reset.route.hostLength == 0,
        "reset did not clear per-proxy route state");
    Check(
        coordinator.MarkConnected(plan) ==
            NativeCoordinatorResult::OperationSuperseded,
        "stale pre-reset plan changed current state");

    Check(
        coordinator.Unregister(id) == NativeCoordinatorResult::Success &&
            coordinator.Unregister(id) ==
                NativeCoordinatorResult::Success,
        "repeated unregister failed");
    Check(
        !coordinator.TryGetSnapshot(id, &reset),
        "unregistered proxy remained visible");
}

void RunUnknownPolicyChecks() {
    NativeClientCoordinator coordinator(ClientRoutePolicy{
        nullptr,
        ReturnUnknownDecision,
    });
    NativeProxyId id = 0;
    Check(
        coordinator.Register(&id) == NativeCoordinatorResult::Success &&
            coordinator.SetHost(id, "unknown.example", 5999) ==
                NativeCoordinatorResult::Success,
        "unknown-policy setup failed");

    ClientBridgePlan plan{};
    Check(
        coordinator.BeginConnect(id, &plan) ==
                NativeCoordinatorResult::RouteRejected &&
            plan.decision == ClientRouteDecision::Reject,
        "unknown policy decision did not fail closed");
}

struct RegisterThreadContext final {
    NativeClientCoordinator* coordinator = nullptr;
    HANDLE start = nullptr;
    NativeCoordinatorResult result =
        NativeCoordinatorResult::InvalidArgument;
    NativeProxyId id = 0;
};

unsigned __stdcall RegisterWorker(void* rawContext) {
    auto* context = static_cast<RegisterThreadContext*>(rawContext);
    WaitForSingleObject(context->start, INFINITE);
    context->result = context->coordinator->Register(&context->id);
    return 0;
}

void RunConcurrentCapacityChecks() {
    constexpr std::size_t WorkerCount =
        NativeClientRegistryCapacity + 16;
    NativeClientCoordinator coordinator;
    RegisterThreadContext contexts[WorkerCount]{};
    HANDLE workers[WorkerCount]{};
    const auto start = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Check(start != nullptr, "registration start event creation failed");
    if (start == nullptr) {
        return;
    }

    std::size_t created = 0;
    for (; created < WorkerCount; ++created) {
        contexts[created].coordinator = &coordinator;
        contexts[created].start = start;
        const auto rawHandle = _beginthreadex(
            nullptr,
            0,
            RegisterWorker,
            &contexts[created],
            0,
            nullptr);
        workers[created] = reinterpret_cast<HANDLE>(rawHandle);
        if (workers[created] == nullptr) {
            Check(false, "registration worker creation failed");
            break;
        }
    }

    SetEvent(start);
    for (std::size_t index = 0; index < created; ++index) {
        const auto waitResult =
            WaitForSingleObject(workers[index], 5000);
        Check(
            waitResult == WAIT_OBJECT_0,
            "registration worker did not finish");
        if (waitResult != WAIT_OBJECT_0) {
            static_cast<void>(
                WaitForSingleObject(workers[index], INFINITE));
        }
        CloseHandle(workers[index]);
    }
    CloseHandle(start);

    if (created != WorkerCount) {
        return;
    }

    std::size_t succeeded = 0;
    std::size_t capacityRejected = 0;
    for (std::size_t index = 0; index < WorkerCount; ++index) {
        if (contexts[index].result == NativeCoordinatorResult::Success) {
            ++succeeded;
            Check(
                contexts[index].id != 0,
                "concurrent registration returned zero ID");
            for (std::size_t earlier = 0; earlier < index; ++earlier) {
                if (contexts[earlier].result ==
                    NativeCoordinatorResult::Success) {
                    Check(
                        contexts[index].id != contexts[earlier].id,
                        "concurrent registration reused a live ID");
                }
            }
        } else if (
            contexts[index].result ==
            NativeCoordinatorResult::CapacityReached) {
            ++capacityRejected;
        } else {
            Check(false, "concurrent registration returned wrong result");
        }
    }

    Check(
        succeeded == NativeClientRegistryCapacity,
        "concurrent registration did not fill exact capacity");
    Check(
        capacityRejected ==
            WorkerCount - NativeClientRegistryCapacity,
        "capacity overflow count changed");

    const auto registry = coordinator.Snapshot();
    Check(
        registry.registered == NativeClientRegistryCapacity,
        "coordinator snapshot registration count changed");

    NativeProxyId maximumId = 0;
    for (const auto& context : contexts) {
        if (context.result == NativeCoordinatorResult::Success) {
            if (context.id > maximumId) {
                maximumId = context.id;
            }
            Check(
                coordinator.Unregister(context.id) ==
                    NativeCoordinatorResult::Success,
                "concurrent registration cleanup failed");
        }
    }

    NativeProxyId replacement = 0;
    Check(
        coordinator.Register(&replacement) ==
                NativeCoordinatorResult::Success &&
            replacement > maximumId,
        "freed registry slot reused a stale proxy ID");
}

} // namespace

int RunNativeClientCoordinatorTests() {
    Failures = 0;
    RunRouteCopyChecks();
    RunDefaultPolicyChecks();
    RunExactPolicyChecks();
    RunStateChecks();
    RunUnknownPolicyChecks();
    RunConcurrentCapacityChecks();
    return Failures;
}
