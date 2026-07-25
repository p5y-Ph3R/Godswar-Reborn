#include "SecureGameGrantRegistryTests.h"

#include "SecureGameControlTestSupport.h"

#include "../src/ClientRoute.h"
#include "../src/SecureGameGrantRegistry.h"

#include <Windows.h>
#include <process.h>

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <utility>

namespace {

using godswar::network::ClientRoute;
using godswar::network::NativeProxyId;
using godswar::network::SecureGameGrant;
using godswar::network::SecureGameGrantClaim;
using godswar::network::SecureGameGrantPolicy;
using godswar::network::SecureGameGrantRegistry;
using godswar::network::SecureGameGrantResult;
using godswar::network::SecureGameGrantState;
using godswar::network::TryCopyClientRoute;
using godswar::network::tests::BuildSecureGrantTestBytes;
using godswar::network::tests::BuildSecureGrantTestManifest;
using godswar::network::tests::DecodeSecureGrantForTest;
using godswar::network::tests::SecureGrantTestClock;
using godswar::network::tests::TestClock;

int Failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

SecureGameGrantPolicy Policy(
    SecureGrantTestClock* clock) noexcept {
    return SecureGameGrantPolicy{
        BuildSecureGrantTestManifest(),
        clock,
        TestClock};
}

ClientRoute Route(
    const char* host = "game-route.reborn.test",
    std::uint16_t port = 7000) noexcept {
    ClientRoute route{};
    static_cast<void>(TryCopyClientRoute(host, port, &route));
    return route;
}

bool ContainsSequence(
    const void* object,
    std::size_t objectBytes,
    const std::uint8_t* sequence,
    std::size_t sequenceBytes) noexcept {
    if (object == nullptr ||
        sequence == nullptr ||
        sequenceBytes == 0 ||
        sequenceBytes > objectBytes) {
        return false;
    }
    const auto* bytes = static_cast<const std::uint8_t*>(object);
    for (std::size_t offset = 0;
         offset <= objectBytes - sequenceBytes;
         ++offset) {
        if (std::memcmp(
                bytes + offset,
                sequence,
                sequenceBytes) == 0) {
            return true;
        }
    }
    return false;
}

void CheckPolicyAndExpiry() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));

    auto allowed = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
    Check(
        registry.Commit(std::move(allowed)) ==
                SecureGameGrantResult::Success &&
            !allowed.IsValid() &&
            registry.Snapshot().state ==
                SecureGameGrantState::Pending,
        "allowed grant was not committed with exclusive ownership");

    auto wrongSuffix = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes(
            "game-route.reborn.test",
            "evil-reborn.test"));
    Check(
        registry.Commit(std::move(wrongSuffix)) ==
                SecureGameGrantResult::PolicyRejected &&
            wrongSuffix.IsValid() &&
            registry.Snapshot().state ==
                SecureGameGrantState::Pending,
        "DNS suffix-boundary rejection changed registry state");

    auto wrongAudience = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes(
            "game-route.reborn.test",
            "game.reborn.test",
            "other"));
    Check(
        registry.Commit(std::move(wrongAudience)) ==
                SecureGameGrantResult::PolicyRejected &&
            wrongAudience.IsValid(),
        "unlisted grant audience was accepted");

    auto wrongServer = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes(
            "game-route.reborn.test",
            "game.reborn.test",
            "reborn-game",
            7000,
            7443,
            43));
    Check(
        registry.Commit(std::move(wrongServer)) ==
                SecureGameGrantResult::PolicyRejected,
        "unlisted target server was accepted");

    auto expired = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes(
            "game-route.reborn.test",
            "game.reborn.test",
            "reborn-game",
            7000,
            7443,
            42,
            clock.now));
    Check(
        registry.Commit(std::move(expired)) ==
                SecureGameGrantResult::Expired &&
            expired.IsValid(),
        "expiry boundary was not rejected before ownership transfer");

    clock.available = false;
    auto noClock = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
    Check(
        registry.Commit(std::move(noClock)) ==
                SecureGameGrantResult::ClockUnavailable &&
            noClock.IsValid(),
        "unavailable client clock did not fail closed");
}

void CheckClaimLifecycle() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));
    auto grant = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
    Check(
        registry.Commit(std::move(grant)) ==
            SecureGameGrantResult::Success,
        "claim lifecycle grant commit failed");

    SecureGameGrantClaim claim{};
    Check(
        registry.Claim(
            7,
            11,
            Route("wrong.reborn.test"),
            &claim) ==
                SecureGameGrantResult::RouteMismatch &&
            claim.grantGeneration == 0 &&
            registry.Snapshot().state ==
                SecureGameGrantState::Pending,
        "wrong logical redirect mutated pending grant");
    Check(
        registry.Claim(7, 11, Route(), &claim) ==
                SecureGameGrantResult::Success &&
            claim.proxyId == 7 &&
            claim.proxyGeneration == 11 &&
            claim.grantGeneration != 0 &&
            registry.Snapshot().state ==
                SecureGameGrantState::Claimed,
        "exact logical redirect did not claim grant");

    SecureGameGrantClaim duplicate{};
    Check(
        registry.Claim(8, 12, Route(), &duplicate) ==
            SecureGameGrantResult::AlreadyClaimed,
        "second proxy claimed one pending grant");

    auto stale = claim;
    ++stale.proxyGeneration;
    Check(
        registry.ReturnUnpresented(stale) ==
                SecureGameGrantResult::StaleClaim &&
            registry.Snapshot().state ==
                SecureGameGrantState::Claimed,
        "stale proxy generation returned a live claim");
    Check(
        registry.ReturnUnpresented(claim) ==
                SecureGameGrantResult::Success &&
            registry.Snapshot().state ==
                SecureGameGrantState::Pending,
        "unpresented claim did not return to Pending");

    SecureGameGrantClaim secondClaim{};
    Check(
        registry.Claim(8, 12, Route(), &secondClaim) ==
            SecureGameGrantResult::Success,
        "returned grant could not be reclaimed");
    SecureGameGrant presented;
    Check(
        registry.BeginPresentation(
            secondClaim,
            &presented) ==
                SecureGameGrantResult::Success &&
            presented.IsValid() &&
            registry.Snapshot().state ==
                SecureGameGrantState::Presented,
        "BeginPresentation did not transfer ticket ownership");
    Check(
        registry.ReturnUnpresented(secondClaim) ==
                SecureGameGrantResult::StaleClaim &&
            registry.Claim(9, 13, Route(), &duplicate) ==
                SecureGameGrantResult::Unavailable,
        "presented ticket could be returned or replayed");
    presented.Clear();
    registry.Erase();
    Check(
        registry.Snapshot().state ==
            SecureGameGrantState::Empty,
        "explicit registry erase did not clear state");
}

void CheckReplacementAndLazyExpiryWipe() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));
    const auto oldBytes = BuildSecureGrantTestBytes();
    auto first = DecodeSecureGrantForTest(oldBytes);
    std::uint64_t firstGeneration = 0;
    Check(
        registry.Commit(
            std::move(first),
            &firstGeneration) ==
                SecureGameGrantResult::Success &&
            firstGeneration != 0,
        "replacement first grant commit failed");
    Check(
        ContainsSequence(
            &registry,
            sizeof(registry),
            oldBytes.bytes + 36,
            32),
        "registry did not own pending ticket bytes");

    auto newBytes = BuildSecureGrantTestBytes();
    for (std::size_t index = 0; index < 32; ++index) {
        newBytes.bytes[36 + index] =
            static_cast<std::uint8_t>(0x80 + index);
    }
    auto second = DecodeSecureGrantForTest(newBytes);
    std::uint64_t secondGeneration = 0;
    Check(
        registry.Commit(
            std::move(second),
            &secondGeneration) ==
                SecureGameGrantResult::Success &&
            secondGeneration > firstGeneration,
        "replacement second grant commit failed");
    Check(
        !registry.EraseIfGeneration(firstGeneration) &&
            !ContainsSequence(
            &registry,
            sizeof(registry),
            oldBytes.bytes + 36,
            32) &&
            ContainsSequence(
                &registry,
                sizeof(registry),
                newBytes.bytes + 36,
                32),
        "grant replacement did not wipe old ticket ownership");

    clock.now = 60'000;
    SecureGameGrantClaim expiredClaim{};
    Check(
        registry.Claim(
            7,
            1,
            Route(),
            &expiredClaim) ==
                SecureGameGrantResult::Expired &&
            registry.Snapshot().state ==
                SecureGameGrantState::Empty &&
            !ContainsSequence(
                &registry,
                sizeof(registry),
                newBytes.bytes + 36,
                32),
        "lazy expiry did not wipe pending ticket");
}

struct ClaimWorkerContext final {
    SecureGameGrantRegistry* registry = nullptr;
    HANDLE start = nullptr;
    NativeProxyId proxyId = 0;
    SecureGameGrantResult result =
        SecureGameGrantResult::InvalidArgument;
    SecureGameGrantClaim claim{};
};

unsigned __stdcall ClaimWorker(void* rawContext) {
    auto* context = static_cast<ClaimWorkerContext*>(rawContext);
    WaitForSingleObject(context->start, INFINITE);
    context->result = context->registry->Claim(
        context->proxyId,
        1,
        Route(),
        &context->claim);
    return 0;
}

void CheckConcurrentSingleClaim() {
    SecureGrantTestClock clock{};
    SecureGameGrantRegistry registry(Policy(&clock));
    auto grant = DecodeSecureGrantForTest(
        BuildSecureGrantTestBytes());
    Check(
        registry.Commit(std::move(grant)) ==
            SecureGameGrantResult::Success,
        "concurrent claim grant commit failed");

    HANDLE start = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    Check(start != nullptr, "claim concurrency event failed");
    if (start == nullptr) {
        return;
    }

    ClaimWorkerContext contexts[2]{};
    HANDLE workers[2]{};
    for (std::size_t index = 0; index < 2; ++index) {
        contexts[index].registry = &registry;
        contexts[index].start = start;
        contexts[index].proxyId =
            static_cast<NativeProxyId>(index + 1);
        workers[index] = reinterpret_cast<HANDLE>(_beginthreadex(
            nullptr,
            0,
            ClaimWorker,
            &contexts[index],
            0,
            nullptr));
        Check(workers[index] != nullptr, "claim worker creation failed");
    }
    SetEvent(start);
    for (auto worker : workers) {
        if (worker != nullptr) {
            Check(
                WaitForSingleObject(worker, 5'000) ==
                    WAIT_OBJECT_0,
                "claim worker did not complete");
            CloseHandle(worker);
        }
    }
    CloseHandle(start);

    const int successes =
        (contexts[0].result == SecureGameGrantResult::Success ? 1 : 0) +
        (contexts[1].result == SecureGameGrantResult::Success ? 1 : 0);
    const int alreadyClaimed =
        (contexts[0].result ==
                SecureGameGrantResult::AlreadyClaimed
            ? 1
            : 0) +
        (contexts[1].result ==
                SecureGameGrantResult::AlreadyClaimed
            ? 1
            : 0);
    Check(
        successes == 1 &&
            alreadyClaimed == 1 &&
            registry.Snapshot().state ==
                SecureGameGrantState::Claimed,
        "concurrent grant claim did not have exactly one winner");
}

} // namespace

int RunSecureGameGrantRegistryTests() {
    Failures = 0;
    CheckPolicyAndExpiry();
    CheckClaimLifecycle();
    CheckReplacementAndLazyExpiryWipe();
    CheckConcurrentSingleClaim();
    return Failures;
}
