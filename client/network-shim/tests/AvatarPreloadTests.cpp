#include "AvatarPreloadTests.h"

#include "../src/AvatarPreviewGate.h"
#include "../src/LegacyClientApi.h"
#include "../src/NetClientProxy.h"

#include <climits>
#include <cstddef>
#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::AvatarPreviewGate;
using godswar::network::ILegacyNetClient;
using godswar::network::NetClientProxy;

int Failures = 0;
bool AvatarResourcesReady = false;
bool PreloadRequestAllowed = false;
int PreloadRequestCalls = 0;
int DisposedMessageCount = 0;

void Check(bool condition, const char* message) {
    if (condition) {
        return;
    }

    std::fprintf(stderr, "FAIL: %s\n", message);
    ++Failures;
}

bool ProbeAvatarResources() noexcept {
    return AvatarResourcesReady;
}

bool RequestAvatarPreload() noexcept {
    ++PreloadRequestCalls;
    return PreloadRequestAllowed;
}

void DisposeTestMessage(void*) noexcept {
    ++DisposedMessageCount;
}

struct TestBootstrapMessage {
    void* vtable = nullptr;
    std::uint16_t length = 44;
    std::uint16_t opcode = 0x2876;
    std::int32_t manifestId = 0;
    std::uint8_t hash[32]{};
    std::uint8_t hashTerminator = 0;
    std::uint8_t versionDigitOne = '8';
    std::uint8_t versionDigitTwo = '8';
    std::uint8_t versionTerminator = 0;
};

struct TestPreviewMessage {
    void* vtable = nullptr;
    std::uint16_t length = 188;
    std::uint16_t opcode = 0x2712;
    std::uint8_t characterCount = 1;
};

static_assert(offsetof(TestBootstrapMessage, length) == 4);
static_assert(offsetof(TestBootstrapMessage, opcode) == 6);
static_assert(offsetof(TestBootstrapMessage, hashTerminator) == 44);
static_assert(sizeof(TestBootstrapMessage) >= 48);
static_assert(0x2876 == 10358);

class FakeLegacyClient final : public ILegacyNetClient {
public:
    std::uint32_t Release() override {
        ++releaseCalls;
        return 73;
    }

    void SetHost(const char*, std::uint16_t) override {
    }

    bool Connect() override {
        ++connectCalls;
        return true;
    }

    void DisConnect() override {
        ++disconnectCalls;
    }

    void Process() override {
        ++processCalls;
    }

    std::uint32_t GetStatus() const override {
        return 0;
    }

    void* PickMsg() override {
        ++pickCalls;
        if (queuedIndex < queuedCount) {
            return queuedMessages[queuedIndex++];
        }
        return nextMessage;
    }

    bool SendMsg(const void*, int) override {
        return true;
    }

    long GetMsgNum() override {
        return messageCount;
    }

    void* queuedMessages[4]{};
    int queuedCount = 0;
    int queuedIndex = 0;
    void* nextMessage = nullptr;
    long messageCount = 19;
    int releaseCalls = 0;
    int connectCalls = 0;
    int disconnectCalls = 0;
    int processCalls = 0;
    int pickCalls = 0;
};

void ResetEvidence(bool requestAllowed) {
    AvatarResourcesReady = false;
    PreloadRequestAllowed = requestAllowed;
    PreloadRequestCalls = 0;
    DisposedMessageCount = 0;
}

void RunBootstrapRecognitionChecks() {
    TestBootstrapMessage exact;
    TestBootstrapMessage wrongLength;
    wrongLength.length = 43;
    TestBootstrapMessage wrongOpcode;
    wrongOpcode.opcode = 0x2877;
    TestBootstrapMessage wrongVersion;
    wrongVersion.versionDigitTwo = '7';
    TestBootstrapMessage wrongTerminator;
    wrongTerminator.hashTerminator = 1;

    Check(
        godswar::network::IsAfterLoginBootstrapMessage(&exact),
        "exact AfterLogin bootstrap record was not recognized");
    Check(
        !godswar::network::IsAfterLoginBootstrapMessage(nullptr),
        "null message was recognized as an AfterLogin bootstrap");
    Check(
        !godswar::network::IsAfterLoginBootstrapMessage(&wrongLength),
        "wrong-length message was recognized as an AfterLogin bootstrap");
    Check(
        !godswar::network::IsAfterLoginBootstrapMessage(&wrongOpcode),
        "wrong-opcode message was recognized as an AfterLogin bootstrap");
    Check(
        !godswar::network::IsAfterLoginBootstrapMessage(&wrongVersion),
        "wrong-version message was recognized as an AfterLogin bootstrap");
    Check(
        !godswar::network::IsAfterLoginBootstrapMessage(&wrongTerminator),
        "unterminated message was recognized as an AfterLogin bootstrap");
}

void RunBootstrapSchedulingChecks() {
    TestBootstrapMessage first;
    TestBootstrapMessage second;
    TestBootstrapMessage third;
    TestBootstrapMessage fourth;
    TestBootstrapMessage malformed;
    malformed.length = 45;

    ResetEvidence(false);
    AvatarPreviewGate gate(
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        RequestAvatarPreload);

    Check(
        gate.Filter(&first) == &first && PreloadRequestCalls == 1,
        "bootstrap was changed or did not make its first preload request");
    Check(
        gate.Filter(&malformed) == &malformed &&
            PreloadRequestCalls == 1,
        "malformed bootstrap scheduled a preload");
    Check(
        gate.Filter(&second) == &second && PreloadRequestCalls == 2,
        "blocked preload was not retried on the next bootstrap");

    PreloadRequestAllowed = true;
    Check(
        gate.Filter(&third) == &third && PreloadRequestCalls == 3,
        "successful preload request changed its bootstrap pointer");
    Check(
        gate.Filter(&fourth) == &fourth && PreloadRequestCalls == 3,
        "preload was requested again after success");

    gate.Reset();
    Check(
        gate.Filter(&first) == &first && PreloadRequestCalls == 4,
        "gate reset did not permit one request for the next lifecycle");
}

void RunPreviewFallbackChecks() {
    TestPreviewMessage preview;

    ResetEvidence(true);
    AvatarPreviewGate gate(
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        RequestAvatarPreload);
    Check(
        gate.Filter(&preview) == nullptr &&
            gate.IsHolding() &&
            PreloadRequestCalls == 1,
        "unready preview did not request preload before being retained");

    bool retained = true;
    for (int poll = 0; poll < 4'096; ++poll) {
        if (gate.TryRelease() != nullptr ||
            !gate.IsHolding() ||
            PreloadRequestCalls != 1) {
            retained = false;
            break;
        }
    }
    Check(
        retained,
        "successful preview fallback was not bounded across 4096 polls");

    AvatarResourcesReady = true;
    Check(
        gate.TryRelease() == &preview && !gate.IsHolding(),
        "ready preview fallback did not return the exact pointer");

    ResetEvidence(false);
    AvatarPreviewGate retryingGate(
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        RequestAvatarPreload);
    Check(
        retryingGate.Filter(&preview) == nullptr &&
            PreloadRequestCalls == 1,
        "blocked preview fallback did not make its first request");
    Check(
        retryingGate.TryRelease() == nullptr &&
            PreloadRequestCalls == 2,
        "blocked preview fallback was not retried while retained");
    PreloadRequestAllowed = true;
    Check(
        retryingGate.TryRelease() == nullptr &&
            PreloadRequestCalls == 3,
        "preview fallback did not record a later successful request");
    Check(
        retryingGate.TryRelease() == nullptr &&
            PreloadRequestCalls == 3,
        "preview fallback retried after success");
    AvatarResourcesReady = true;
    Check(
        retryingGate.TryRelease() == &preview,
        "retried preview fallback did not release after readiness");
}

void RunProxyOrderingChecks() {
    TestBootstrapMessage bootstrap;
    TestPreviewMessage preview;
    TestPreviewMessage later;
    later.opcode = 0x2713;
    FakeLegacyClient legacy;
    legacy.queuedMessages[0] = &bootstrap;
    legacy.queuedMessages[1] = &preview;
    legacy.queuedMessages[2] = &later;
    legacy.queuedCount = 3;

    ResetEvidence(true);
    auto* client = NetClientProxy::CreateForTesting(
        &legacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        RequestAvatarPreload);
    Check(client != nullptr, "preload proxy fixture returned null");
    if (client == nullptr) {
        return;
    }

    Check(
        client->PickMsg() == &bootstrap &&
            legacy.pickCalls == 1 &&
            PreloadRequestCalls == 1,
        "proxy did not pass the triggering bootstrap through unchanged");
    Check(
        client->PickMsg() == nullptr &&
            legacy.pickCalls == 2 &&
            client->GetMsgNum() == 20,
        "proxy did not retain the following unready preview");

    bool stayedOrdered = true;
    constexpr int SchedulingCycles = 4'096;
    for (int cycle = 0; cycle < SchedulingCycles; ++cycle) {
        client->Process();
        if (client->PickMsg() != nullptr ||
            legacy.pickCalls != 2 ||
            PreloadRequestCalls != 1) {
            stayedOrdered = false;
            break;
        }
    }
    Check(
        stayedOrdered &&
            legacy.processCalls == SchedulingCycles &&
            DisposedMessageCount == 0,
        "proxy ordering or processing changed across 4096 cycles");

    AvatarResourcesReady = true;
    Check(
        client->PickMsg() == &preview,
        "proxy did not return the retained preview first");
    Check(
        client->PickMsg() == &later && legacy.pickCalls == 3,
        "a later message overtook the retained preview");
    client->DisConnect();
    Check(legacy.disconnectCalls == 1, "proxy disconnect was not delegated");
    Check(client->Release() == 73, "proxy release result changed");
    Check(
        legacy.releaseCalls == 1 && DisposedMessageCount == 0,
        "released proxy changed message ownership");
}

void RunProxyLifecycleResetChecks() {
    TestBootstrapMessage bootstrap;
    FakeLegacyClient legacy;
    legacy.nextMessage = &bootstrap;

    ResetEvidence(true);
    auto* client = NetClientProxy::CreateForTesting(
        &legacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        RequestAvatarPreload);
    Check(client != nullptr, "preload lifecycle fixture returned null");
    if (client == nullptr) {
        return;
    }

    Check(
        client->PickMsg() == &bootstrap && PreloadRequestCalls == 1,
        "initial lifecycle did not request preload");
    Check(
        client->PickMsg() == &bootstrap && PreloadRequestCalls == 1,
        "initial lifecycle requested preload more than once");
    Check(client->Connect(), "proxy reconnect was not delegated");
    Check(
        client->PickMsg() == &bootstrap && PreloadRequestCalls == 2,
        "connect did not reset preload scheduling");
    client->DisConnect();
    Check(
        client->PickMsg() == &bootstrap && PreloadRequestCalls == 3,
        "disconnect did not reset preload scheduling");
    client->Release();
    Check(
        legacy.connectCalls == 1 &&
            legacy.disconnectCalls == 1 &&
            legacy.releaseCalls == 1,
        "preload lifecycle delegation changed");
}

} // namespace

int RunAvatarPreloadTests() {
    RunBootstrapRecognitionChecks();
    RunBootstrapSchedulingChecks();
    RunPreviewFallbackChecks();
    RunProxyOrderingChecks();
    RunProxyLifecycleResetChecks();
    return Failures;
}
