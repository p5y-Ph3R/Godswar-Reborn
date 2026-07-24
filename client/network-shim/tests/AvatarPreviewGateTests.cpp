#include "AvatarPreviewGateTests.h"

#include "../src/AvatarPreviewGate.h"
#include "../src/LegacyClientApi.h"
#include "../src/NetClientProxy.h"

#include <Windows.h>

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
std::uint64_t AvatarClockMilliseconds = 0;
int DisposedMessageCount = 0;
void* LastDisposedMessage = nullptr;
int DestroyedVirtualMessageCount = 0;

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

std::uint64_t ReadTestAvatarClock() noexcept {
    return AvatarClockMilliseconds;
}

void DisposeTestMessage(void* message) noexcept {
    ++DisposedMessageCount;
    LastDisposedMessage = message;
}

struct TestLegacyMessage {
    void* vtable = nullptr;
    std::uint16_t length = 188;
    std::uint16_t opcode = 0x2712;
    std::uint8_t characterCount = 1;
};

static_assert(
    offsetof(TestLegacyMessage, length) == 4,
    "legacy message length offset changed");
static_assert(
    offsetof(TestLegacyMessage, opcode) == 6,
    "legacy message opcode offset changed");
static_assert(
    offsetof(TestLegacyMessage, characterCount) == 8,
    "legacy message payload offset changed");
static_assert(
    godswar::network::AvatarPreviewWaitTimeoutMilliseconds == 5'000,
    "bounded preview fallback contract changed");

class TestVirtualMessage {
public:
    virtual ~TestVirtualMessage() {
        ++DestroyedVirtualMessageCount;
    }
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
        if (queuedMessageIndex < queuedMessageCount) {
            return queuedMessages[queuedMessageIndex++];
        }
        return nextMessage;
    }

    bool SendMsg(const void*, int) override {
        return true;
    }

    long GetMsgNum() override {
        ++messageCountCalls;
        return messageCountResult;
    }

    int releaseCalls = 0;
    int connectCalls = 0;
    int disconnectCalls = 0;
    int processCalls = 0;
    int pickCalls = 0;
    int messageCountCalls = 0;
    void* queuedMessages[4]{};
    int queuedMessageCount = 0;
    int queuedMessageIndex = 0;
    void* nextMessage = nullptr;
    long messageCountResult = 19;
};

void ResetDisposalEvidence() {
    DisposedMessageCount = 0;
    LastDisposedMessage = nullptr;
}

void RunGateChecks() {
    TestLegacyMessage preview;
    TestLegacyMessage other;
    other.opcode = 0x2713;
    TestLegacyMessage wrongLength;
    wrongLength.length = 187;
    TestLegacyMessage wrongCount;
    wrongCount.characterCount = 0;

    AvatarResourcesReady = false;
    AvatarClockMilliseconds = 1'000;
    ResetDisposalEvidence();

    {
        AvatarPreviewGate disabled(
            false,
            ProbeAvatarResources,
            DisposeTestMessage);
        Check(
            disabled.Filter(&preview) == &preview,
            "disabled avatar gate changed a preview");
        Check(!disabled.IsHolding(), "disabled avatar gate retained a message");
    }

    {
        AvatarPreviewGate gate(
            true,
            ProbeAvatarResources,
            DisposeTestMessage);
        Check(
            gate.Filter(nullptr) == nullptr && !gate.IsHolding(),
            "avatar gate retained a null message");
        Check(
            gate.Filter(&other) == &other,
            "avatar gate changed a non-preview message");
        Check(
            gate.Filter(&wrongLength) == &wrongLength,
            "avatar gate retained a wrong-length message");
        Check(
            gate.Filter(&wrongCount) == &wrongCount,
            "avatar gate retained a zero-character message");
        Check(
            gate.Filter(&preview) == nullptr && gate.IsHolding(),
            "avatar gate did not retain an unready preview");
        Check(
            gate.AdjustMessageCount(19) == 20,
            "avatar gate did not include its retained message in the count");
        Check(
            gate.AdjustMessageCount(LONG_MAX) == LONG_MAX,
            "avatar gate overflowed the retained message count");
        Check(
            gate.TryRelease() == nullptr,
            "avatar gate released a preview before readiness");

        AvatarResourcesReady = true;
        Check(
            gate.TryRelease() == &preview,
            "avatar gate did not release the original preview pointer");
        Check(!gate.IsHolding(), "avatar gate retained a released preview");
    }

    AvatarResourcesReady = false;
    {
        AvatarPreviewGate gate(
            true,
            ProbeAvatarResources,
            DisposeTestMessage);
        Check(
            gate.Filter(&preview) == nullptr,
            "avatar gate cleanup fixture was not retained");
        gate.Reset();
        Check(
            !gate.IsHolding() &&
                DisposedMessageCount == 1 &&
                LastDisposedMessage == &preview,
            "avatar gate did not dispose a retained preview exactly once");
    }
    Check(
        DisposedMessageCount == 1,
        "avatar gate destructor disposed a reset message twice");

    AvatarResourcesReady = true;
    {
        AvatarPreviewGate gate(
            true,
            ProbeAvatarResources,
            DisposeTestMessage);
        Check(
            gate.Filter(&preview) == &preview && !gate.IsHolding(),
            "avatar gate retained an already-ready preview");
    }

    AvatarResourcesReady = false;
    AvatarClockMilliseconds = 5'000;
    ResetDisposalEvidence();
    {
        AvatarPreviewGate gate(
            true,
            ProbeAvatarResources,
            DisposeTestMessage,
            ReadTestAvatarClock,
            100);
        Check(
            gate.Filter(&preview) == nullptr,
            "timeout fixture did not retain its preview");
        AvatarClockMilliseconds = 5'099;
        Check(
            !gate.HasTimedOut() && gate.TryRelease() == nullptr,
            "avatar gate released or timed out too early");
        AvatarClockMilliseconds = 5'100;
        Check(gate.HasTimedOut(), "avatar gate did not reach its timeout");
        Check(
            gate.TryRelease() == &preview && !gate.IsHolding(),
            "timeout did not return the original preview as a bounded fallback");
        Check(
            DisposedMessageCount == 0,
            "timeout fallback disposed a message owned by Origin");
    }

    AvatarClockMilliseconds = 6'000;
    {
        AvatarPreviewGate gate(
            true,
            ProbeAvatarResources,
            DisposeTestMessage,
            ReadTestAvatarClock,
            100);
        Check(
            gate.Filter(&preview) == nullptr,
            "readiness-boundary fixture did not retain its preview");
        AvatarClockMilliseconds = 6'100;
        AvatarResourcesReady = true;
        Check(
            !gate.HasTimedOut() && gate.TryRelease() == &preview,
            "readiness did not win at the timeout boundary");
    }

    DestroyedVirtualMessageCount = 0;
    godswar::network::DestroyLegacyMessage(new TestVirtualMessage());
    Check(
        DestroyedVirtualMessageCount == 1,
        "legacy scalar-deleting destructor was not invoked exactly once");

    auto* inaccessible = VirtualAlloc(
        nullptr,
        4096,
        MEM_RESERVE | MEM_COMMIT,
        PAGE_NOACCESS);
    Check(inaccessible != nullptr, "could not allocate no-access test page");
    if (inaccessible != nullptr) {
        Check(
            !godswar::network::IsCharacterPreviewMessage(inaccessible),
            "avatar gate accepted an inaccessible message");
        VirtualFree(inaccessible, 0, MEM_RELEASE);
    }
}

void RunOrderingAndLifecycleChecks() {
    TestLegacyMessage preview;
    TestLegacyMessage later;
    later.opcode = 0x2713;
    FakeLegacyClient legacy;
    legacy.queuedMessages[0] = &preview;
    legacy.queuedMessages[1] = &later;
    legacy.queuedMessageCount = 2;

    AvatarResourcesReady = false;
    ResetDisposalEvidence();
    auto* client = NetClientProxy::CreateForTesting(
        &legacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage);
    Check(client != nullptr, "avatar proxy fixture returned null");
    if (client != nullptr) {
        client->Process();
        Check(legacy.processCalls == 1, "proxy blocked before retention");
        Check(
            client->PickMsg() == nullptr && legacy.pickCalls == 1,
            "proxy delivered an unready preview");
        Check(
            client->GetMsgNum() == 20,
            "proxy did not expose the retained message count");

        client->Process();
        Check(
            legacy.processCalls == 2,
            "proxy stopped network processing while retaining a preview");
        Check(
            client->PickMsg() == nullptr && legacy.pickCalls == 1,
            "proxy polled past the retained preview");

        AvatarResourcesReady = true;
        Check(
            client->PickMsg() == &preview,
            "proxy did not return the retained preview on readiness");
        Check(
            client->PickMsg() == &later && legacy.pickCalls == 2,
            "a later message overtook the retained preview");
        client->DisConnect();
        client->Release();
        Check(
            DisposedMessageCount == 0,
            "proxy disposed a preview already returned to Origin");
    }

    FakeLegacyClient disconnectLegacy;
    disconnectLegacy.nextMessage = &preview;
    AvatarResourcesReady = false;
    ResetDisposalEvidence();
    auto* disconnectClient = NetClientProxy::CreateForTesting(
        &disconnectLegacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage);
    Check(disconnectClient != nullptr, "disconnect fixture returned null");
    if (disconnectClient != nullptr) {
        static_cast<void>(disconnectClient->PickMsg());
        disconnectClient->Process();
        disconnectClient->DisConnect();
        disconnectClient->Release();
        Check(
            DisposedMessageCount == 1 &&
                LastDisposedMessage == &preview &&
                disconnectLegacy.processCalls == 1,
            "remote-disconnect cleanup did not dispose exactly once");
    }

    FakeLegacyClient releaseLegacy;
    releaseLegacy.nextMessage = &preview;
    ResetDisposalEvidence();
    auto* releaseClient = NetClientProxy::CreateForTesting(
        &releaseLegacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage);
    Check(releaseClient != nullptr, "release fixture returned null");
    if (releaseClient != nullptr) {
        static_cast<void>(releaseClient->PickMsg());
        releaseClient->Release();
        Check(
            DisposedMessageCount == 1 &&
                releaseLegacy.releaseCalls == 1,
            "direct release did not clean the retained preview");
    }

    FakeLegacyClient reconnectLegacy;
    reconnectLegacy.queuedMessages[0] = &preview;
    reconnectLegacy.queuedMessages[1] = &later;
    reconnectLegacy.queuedMessageCount = 2;
    ResetDisposalEvidence();
    auto* reconnectClient = NetClientProxy::CreateForTesting(
        &reconnectLegacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage);
    Check(reconnectClient != nullptr, "reconnect fixture returned null");
    if (reconnectClient != nullptr) {
        static_cast<void>(reconnectClient->PickMsg());
        Check(reconnectClient->Connect(), "reconnect delegation failed");
        AvatarResourcesReady = true;
        Check(
            DisposedMessageCount == 1 &&
                reconnectClient->PickMsg() == &later,
            "reconnect delivered a stale prior-session preview");
        reconnectClient->Release();
    }
}

void RunTimeoutChecks() {
    TestLegacyMessage preview;
    TestLegacyMessage later;
    later.opcode = 0x2713;
    FakeLegacyClient legacy;
    legacy.queuedMessages[0] = &preview;
    legacy.queuedMessages[1] = &later;
    legacy.queuedMessageCount = 2;
    AvatarResourcesReady = false;
    AvatarClockMilliseconds = 10'000;
    ResetDisposalEvidence();
    auto* client = NetClientProxy::CreateForTesting(
        &legacy,
        true,
        ProbeAvatarResources,
        DisposeTestMessage,
        ReadTestAvatarClock,
        100);
    Check(client != nullptr, "timeout-fallback fixture returned null");
    if (client != nullptr) {
        Check(
            client->PickMsg() == nullptr && legacy.pickCalls == 1,
            "timeout fixture did not retain its preview");
        client->Process();
        AvatarClockMilliseconds = 10'099;
        Check(
            client->PickMsg() == nullptr &&
                legacy.processCalls == 1 &&
                legacy.pickCalls == 1,
            "pre-timeout polling changed order or stopped network processing");

        AvatarClockMilliseconds = 10'100;
        Check(
            client->PickMsg() == &preview &&
                DisposedMessageCount == 0 &&
                legacy.disconnectCalls == 0,
            "timeout did not return the original preview without disconnect");
        Check(
            client->PickMsg() == &later && legacy.pickCalls == 2,
            "a later message overtook the timeout-released preview");
        client->DisConnect();
        client->Release();
        Check(
            DisposedMessageCount == 0 &&
                legacy.disconnectCalls == 1 &&
                legacy.releaseCalls == 1,
            "timeout fallback changed native disconnect/release ownership");
    }
}

} // namespace

int RunAvatarPreviewGateTests() {
    RunGateChecks();
    RunOrderingAndLifecycleChecks();
    RunTimeoutChecks();
    return Failures;
}
