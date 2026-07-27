#include "AvatarPreloadTests.h"

#include "../src/AvatarPreviewGate.h"
#include "../src/LegacyClientApi.h"
#include "../src/NetClientProxy.h"

#include <cstddef>
#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::AvatarPreviewGate;
using godswar::network::AvatarPreloadResult;
using godswar::network::ILegacyNetClient;
using godswar::network::NetClientProxy;

int Failures = 0;
bool AvatarResourcesReady = false;
AvatarPreloadResult InitializationResult =
    AvatarPreloadResult::NotInvoked;
bool InitializationCompletesResources = false;
int InitializationCalls = 0;
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

AvatarPreloadResult InitializeAvatarResources() noexcept {
    ++InitializationCalls;
    if (InitializationCompletesResources) {
        AvatarResourcesReady = true;
    }
    return InitializationResult;
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
        ++sendCalls;
        return true;
    }

    long GetMsgNum() override {
        ++messageCountCalls;
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
    int sendCalls = 0;
    int messageCountCalls = 0;
};

void ResetEvidence(
    AvatarPreloadResult result,
    bool completesResources) {
    AvatarResourcesReady = false;
    InitializationResult = result;
    InitializationCompletesResources = completesResources;
    InitializationCalls = 0;
    DisposedMessageCount = 0;
}

void RunUnsupportedHostChecks() {
    TestPreviewMessage preview;
    AvatarPreviewGate defaultGate;

    Check(
        !godswar::network::IsSupportedOriginAvatarHost(),
        "native test executable was accepted as the pinned Origin host");
    Check(
        godswar::network::RequestOriginAvatarPreload() ==
            AvatarPreloadResult::NotInvoked,
        "unsupported native test host invoked the Origin initializer");
    Check(
        defaultGate.Filter(&preview) == &preview &&
            !defaultGate.IsHolding(),
        "unsupported host did not retain pass-through preview behavior");
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

void RunBootstrapBarrierChecks() {
    constexpr std::size_t BootstrapRecordCount = 63;
    TestBootstrapMessage bootstraps[BootstrapRecordCount]{};
    TestPreviewMessage preview;
    TestPreviewMessage repeatedPreview;

    ResetEvidence(AvatarPreloadResult::Ready, true);
    AvatarPreviewGate gate(
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        InitializeAvatarResources);

    bool bootstrapsPassed = true;
    for (std::size_t index = 0;
         index < BootstrapRecordCount;
         ++index) {
        bootstraps[index].manifestId =
            static_cast<std::int32_t>(index);
        if (!godswar::network::IsAfterLoginBootstrapMessage(
                &bootstraps[index]) ||
            gate.Filter(&bootstraps[index]) !=
                &bootstraps[index] ||
            gate.IsHolding()) {
            bootstrapsPassed = false;
            break;
        }
    }
    Check(
        bootstrapsPassed &&
            InitializationCalls == 0 &&
            !AvatarResourcesReady,
        "63-record bootstrap initialized before the preview barrier");
    Check(
        gate.Filter(&preview) == &preview &&
            InitializationCalls == 1 &&
            AvatarResourcesReady &&
            !gate.IsHolding(),
        "preview barrier did not initialize and return its exact pointer");
    Check(
        gate.Filter(&repeatedPreview) == &repeatedPreview &&
            InitializationCalls == 1,
        "ready preview initialized one lifecycle more than once");
}

void RunPreviewRetryChecks() {
    TestPreviewMessage immediatePreview;

    ResetEvidence(AvatarPreloadResult::Ready, true);
    AvatarPreviewGate immediateGate(
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        InitializeAvatarResources);
    Check(
        immediateGate.Filter(&immediatePreview) == &immediatePreview &&
            !immediateGate.IsHolding() &&
            InitializationCalls == 1,
        "synchronous readiness did not pass the original preview");

    TestPreviewMessage retainedPreview;
    ResetEvidence(AvatarPreloadResult::NotInvoked, false);
    AvatarPreviewGate retryingGate(
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        InitializeAvatarResources);
    Check(
        retryingGate.Filter(&retainedPreview) == nullptr &&
            retryingGate.IsHolding() &&
            InitializationCalls == 1,
        "unready preview was not retained after initialization");

    constexpr int NotInvokedRetries = 64;
    bool retained = true;
    for (int retry = 0; retry < NotInvokedRetries; ++retry) {
        if (retryingGate.TryRelease() != nullptr ||
            !retryingGate.IsHolding()) {
            retained = false;
            break;
        }
    }
    Check(
        retained &&
            InitializationCalls == 1 + NotInvokedRetries,
        "not-invoked initialization was not retried");

    InitializationResult = AvatarPreloadResult::InvokedNotReady;
    Check(
        retryingGate.TryRelease() == nullptr &&
            retryingGate.IsHolding() &&
            InitializationCalls == 2 + NotInvokedRetries,
        "eligible retry did not record its one native invocation");

    for (int poll = 0; poll < NotInvokedRetries; ++poll) {
        static_cast<void>(retryingGate.TryRelease());
    }
    Check(
        retryingGate.IsHolding() &&
            InitializationCalls == 2 + NotInvokedRetries,
        "partial native initialization was invoked more than once");

    AvatarResourcesReady = true;
    Check(
        retryingGate.TryRelease() == &retainedPreview &&
            !retryingGate.IsHolding() &&
            InitializationCalls == 2 + NotInvokedRetries,
        "later readiness did not release the exact retained pointer");
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

    ResetEvidence(AvatarPreloadResult::NotInvoked, false);
    auto* client = NetClientProxy::CreateForTesting(
        &legacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        InitializeAvatarResources);
    Check(client != nullptr, "initializer proxy fixture returned null");
    if (client == nullptr) {
        return;
    }

    Check(
        client->PickMsg() == &bootstrap &&
            legacy.pickCalls == 1 &&
            InitializationCalls == 0,
        "proxy initialized before passing the bootstrap through");
    InitializationResult = AvatarPreloadResult::InvokedNotReady;
    Check(
        client->PickMsg() == nullptr &&
            legacy.pickCalls == 2 &&
            InitializationCalls == 1 &&
            client->GetMsgNum() == 20,
        "proxy did not retain the following unready preview");

    bool stayedOrdered = true;
    constexpr int SchedulingCycles = 4'096;
    for (int cycle = 0; cycle < SchedulingCycles; ++cycle) {
        client->Process();
        if (client->PickMsg() != nullptr ||
            legacy.pickCalls != 2) {
            stayedOrdered = false;
            break;
        }
    }
    Check(
        stayedOrdered &&
            legacy.processCalls == SchedulingCycles &&
            InitializationCalls == 1 &&
            DisposedMessageCount == 0,
        "bounded invocation changed legacy Process, order, or ownership");

    AvatarResourcesReady = true;
    Check(
        client->PickMsg() == &preview &&
            InitializationCalls == 1,
        "later readiness did not return the retained preview first");
    Check(
        client->PickMsg() == &later && legacy.pickCalls == 3,
        "a later message overtook the retained preview");
    client->DisConnect();
    Check(legacy.disconnectCalls == 1, "proxy disconnect was not delegated");
    Check(client->Release() == 73, "proxy release result changed");
    Check(
        legacy.releaseCalls == 1 && DisposedMessageCount == 0,
        "released proxy changed returned-message ownership");
}

void RunPickPathAndLifecycleChecks() {
    TestPreviewMessage preview;
    FakeLegacyClient legacy;
    legacy.nextMessage = &preview;

    ResetEvidence(AvatarPreloadResult::Ready, true);
    auto* client = NetClientProxy::CreateForTesting(
        &legacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        InitializeAvatarResources);
    Check(client != nullptr, "PickMsg-path fixture returned null");
    if (client == nullptr) {
        return;
    }

    client->Process();
    static_cast<void>(client->GetMsgNum());
    static_cast<void>(client->SendMsg(nullptr, 0));
    Check(
        InitializationCalls == 0,
        "non-PickMsg proxy methods invoked the native initializer");

    Check(
        client->PickMsg() == &preview &&
            InitializationCalls == 1,
        "PickMsg did not invoke initialization at the preview barrier");
    Check(
        client->PickMsg() == &preview &&
            InitializationCalls == 1,
        "initializer ran twice before lifecycle reset");
    Check(client->Connect(), "proxy reconnect was not delegated");
    Check(
        InitializationCalls == 1,
        "connect invoked the native initializer");

    AvatarResourcesReady = false;
    client->Process();
    Check(
        client->PickMsg() == &preview &&
            InitializationCalls == 2,
        "post-connect preview did not allow one initialization");
    client->DisConnect();
    Check(
        InitializationCalls == 2,
        "disconnect invoked the native initializer");
    AvatarResourcesReady = false;
    Check(
        client->PickMsg() == &preview &&
            InitializationCalls == 3,
        "post-disconnect preview did not allow one initialization");

    client->Release();
    Check(
        legacy.connectCalls == 1 &&
            legacy.disconnectCalls == 1 &&
            legacy.releaseCalls == 1 &&
            legacy.processCalls == 2 &&
            legacy.sendCalls == 1 &&
            legacy.messageCountCalls == 1 &&
            DisposedMessageCount == 0,
        "preview initialization changed legacy lifecycle delegation");
}

} // namespace

int RunAvatarPreloadTests() {
    RunUnsupportedHostChecks();
    RunBootstrapRecognitionChecks();
    RunBootstrapBarrierChecks();
    RunPreviewRetryChecks();
    RunProxyOrderingChecks();
    RunPickPathAndLifecycleChecks();
    return Failures;
}
