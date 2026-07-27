#include "AvatarPreloadLifecycleTests.h"

#include "../src/AvatarPreviewGate.h"
#include "../src/LegacyClientApi.h"
#include "../src/NetClientProxy.h"

#include <cstdint>
#include <cstdio>

namespace {

using godswar::network::AvatarPreloadResult;
using godswar::network::AvatarPreviewGate;
using godswar::network::ILegacyNetClient;
using godswar::network::NetClientProxy;

int Failures = 0;
bool ResourcesReady = false;
bool InitializationCompletes = false;
AvatarPreloadResult InitializationResult =
    AvatarPreloadResult::NotInvoked;
int InitializationCalls = 0;
int DisposedMessages = 0;
ILegacyNetClient* ReentrantClient = nullptr;
ILegacyNetClient* ReentrantDisconnectClient = nullptr;
bool ReentrantPickReturnedNull = false;

void Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        ++Failures;
    }
}

bool ProbeResources() noexcept {
    return ResourcesReady;
}

AvatarPreloadResult InitializeResources() noexcept {
    ++InitializationCalls;
    if (InitializationCompletes) {
        ResourcesReady = true;
    }
    if (ReentrantDisconnectClient != nullptr) {
        auto* const client = ReentrantDisconnectClient;
        ReentrantDisconnectClient = nullptr;
        client->DisConnect();
    }
    if (ReentrantClient != nullptr) {
        auto* const client = ReentrantClient;
        ReentrantClient = nullptr;
        ReentrantPickReturnedNull = client->PickMsg() == nullptr;
    }
    return InitializationResult;
}

void DisposeMessage(void*) noexcept {
    ++DisposedMessages;
}

struct BootstrapMessage {
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

struct PreviewMessage {
    void* vtable = nullptr;
    std::uint16_t length = 188;
    std::uint16_t opcode = 0x2712;
    std::uint8_t characterCount = 1;
};

class FakeLegacyClient final : public ILegacyNetClient {
public:
    std::uint32_t Release() override {
        ++releaseCalls;
        return 73;
    }
    void SetHost(const char*, std::uint16_t) override {
    }
    bool Connect() override {
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
        ++pickCalls;
        return queuedIndex < queuedCount
            ? queuedMessages[queuedIndex++]
            : nullptr;
    }
    bool SendMsg(const void*, int) override {
        return true;
    }
    long GetMsgNum() override {
        return 19;
    }

    void* queuedMessages[4]{};
    int queuedCount = 0;
    int queuedIndex = 0;
    int releaseCalls = 0;
    int disconnectCalls = 0;
    int pickCalls = 0;
};

void ResetEvidence(
    AvatarPreloadResult result,
    bool completes) {
    ResourcesReady = false;
    InitializationCompletes = completes;
    InitializationResult = result;
    InitializationCalls = 0;
    DisposedMessages = 0;
    ReentrantClient = nullptr;
    ReentrantDisconnectClient = nullptr;
    ReentrantPickReturnedNull = false;
}

ILegacyNetClient* CreateClient(FakeLegacyClient* legacy) {
    return NetClientProxy::CreateForTesting(
        legacy,
        true,
        ProbeResources,
        DisposeMessage,
        InitializeResources);
}

void RunReentrantResetCheck() {
    PreviewMessage stalePreview;
    BootstrapMessage nextBootstrap;
    PreviewMessage nextPreview;
    FakeLegacyClient legacy;
    legacy.queuedMessages[0] = &stalePreview;
    legacy.queuedMessages[1] = &nextBootstrap;
    legacy.queuedMessages[2] = &nextPreview;
    legacy.queuedCount = 3;

    ResetEvidence(AvatarPreloadResult::InvokedNotReady, false);
    auto* client = CreateClient(&legacy);
    Check(client != nullptr, "re-entrant reset fixture returned null");
    if (client == nullptr) {
        return;
    }
    ReentrantDisconnectClient = client;
    ReentrantClient = client;

    Check(
        client->PickMsg() == nullptr &&
            ReentrantPickReturnedNull &&
            legacy.pickCalls == 1 &&
            legacy.disconnectCalls == 1 &&
            InitializationCalls == 1 &&
            DisposedMessages == 1,
        "re-entrant disconnect retained an old-lifecycle preview");
    Check(
        client->PickMsg() == &nextBootstrap &&
            InitializationCalls == 1,
        "post-reset bootstrap invoked native initialization");
    InitializationResult = AvatarPreloadResult::Ready;
    InitializationCompletes = true;
    Check(
        client->PickMsg() == &nextPreview &&
            InitializationCalls == 2 &&
            ResourcesReady,
        "post-reset preview retained an old initialization result");
    client->Release();
}

void RunReentrantOrderingCheck() {
    PreviewMessage outerPreview;
    PreviewMessage nestedPreview;
    PreviewMessage later;
    later.opcode = 0x2713;
    FakeLegacyClient legacy;
    legacy.queuedMessages[0] = &outerPreview;
    legacy.queuedMessages[1] = &nestedPreview;
    legacy.queuedMessages[2] = &later;
    legacy.queuedCount = 3;

    ResetEvidence(AvatarPreloadResult::InvokedNotReady, false);
    auto* client = CreateClient(&legacy);
    Check(client != nullptr, "re-entrant proxy fixture returned null");
    if (client == nullptr) {
        return;
    }
    ReentrantClient = client;

    Check(
        client->PickMsg() == nullptr &&
            ReentrantPickReturnedNull &&
            legacy.pickCalls == 1 &&
            legacy.queuedIndex == 1 &&
            InitializationCalls == 1 &&
            client->GetMsgNum() == 20,
        "initializer re-entry polled or overtook the outer preview");

    ResourcesReady = true;
    Check(
        client->PickMsg() == &outerPreview &&
            legacy.pickCalls == 1,
        "outer preview was lost during initializer re-entry");
    Check(
        client->PickMsg() == &nestedPreview &&
            legacy.pickCalls == 2,
        "nested preview overtook the retained outer preview");
    Check(
        client->PickMsg() == &later &&
            legacy.pickCalls == 3,
        "later message order changed after initializer re-entry");

    client->Release();
    Check(
        legacy.releaseCalls == 1 && DisposedMessages == 0,
        "re-entrant proxy changed returned-message ownership");
}

void RunSameTransportLifecycleCheck() {
    PreviewMessage firstPreview;
    BootstrapMessage nextLifecycle;
    PreviewMessage nextPreview;
    PreviewMessage previewOnlyLifecycle;
    BootstrapMessage repeatedBootstrap;

    ResetEvidence(AvatarPreloadResult::Ready, true);
    AvatarPreviewGate gate(
        true,
        ProbeResources,
        DisposeMessage,
        InitializeResources);
    Check(
        gate.Filter(&firstPreview) == &firstPreview &&
            InitializationCalls == 1 &&
            ResourcesReady,
        "first selection lifecycle did not become ready");

    ResourcesReady = false;
    Check(
        gate.Filter(&nextLifecycle) == &nextLifecycle &&
            InitializationCalls == 1 &&
            !ResourcesReady,
        "same-transport bootstrap initialized before its preview");
    Check(
        gate.Filter(&nextPreview) == &nextPreview &&
            InitializationCalls == 2 &&
            ResourcesReady,
        "same-transport preview did not initialize once");
    Check(
        gate.Filter(&repeatedBootstrap) == &repeatedBootstrap &&
            InitializationCalls == 2,
        "ready lifecycle bootstrap initialized more than once");

    ResourcesReady = false;
    Check(
        gate.Filter(&previewOnlyLifecycle) == &previewOnlyLifecycle &&
            InitializationCalls == 3 &&
            ResourcesReady,
        "preview-only selection re-entry did not initialize once");
}

void RunRetainedPreviewReleaseCheck() {
    PreviewMessage preview;
    PreviewMessage later;
    later.opcode = 0x2713;
    FakeLegacyClient legacy;
    legacy.queuedMessages[0] = &preview;
    legacy.queuedMessages[1] = &later;
    legacy.queuedCount = 2;

    ResetEvidence(AvatarPreloadResult::NotInvoked, false);
    auto* client = CreateClient(&legacy);
    Check(client != nullptr, "retained-preview fixture returned null");
    if (client == nullptr) {
        return;
    }

    Check(
        client->PickMsg() == nullptr &&
            legacy.pickCalls == 1 &&
            InitializationCalls == 1,
        "preview was not retained before an eligible retry");

    InitializationResult = AvatarPreloadResult::Ready;
    InitializationCompletes = true;
    ReentrantClient = client;
    Check(
        client->PickMsg() == &preview &&
            ReentrantPickReturnedNull &&
            legacy.pickCalls == 1 &&
            InitializationCalls == 2,
        "retained preview was released inside the initializer");
    Check(
        client->PickMsg() == &later &&
            legacy.pickCalls == 2,
        "later message order changed after the outer release");

    client->Release();
    Check(
        legacy.releaseCalls == 1 && DisposedMessages == 0,
        "retained-preview release changed message ownership");
}

} // namespace

int RunAvatarPreloadLifecycleTests() {
    RunReentrantResetCheck();
    RunReentrantOrderingCheck();
    RunSameTransportLifecycleCheck();
    RunRetainedPreviewReleaseCheck();
    return Failures;
}
