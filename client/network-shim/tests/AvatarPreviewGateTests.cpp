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
    0x2712 == 10002,
    "character preview opcode changed");

class TestVirtualMessage {
public:
    virtual ~TestVirtualMessage() {
        ++DestroyedVirtualMessageCount;
    }
};

class FakeLegacyClient final : public ILegacyNetClient {
public:
    std::uint32_t Release() override {
        disposedAtRelease = DisposedMessageCount;
        ++releaseCalls;
        return 73;
    }

    void SetHost(const char*, std::uint16_t) override {
    }

    bool Connect() override {
        disposedAtConnect = DisposedMessageCount;
        ++connectCalls;
        return true;
    }

    void DisConnect() override {
        disposedAtDisconnect = DisposedMessageCount;
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
    int disposedAtConnect = -1;
    int disposedAtDisconnect = -1;
    int disposedAtRelease = -1;
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
            gate.AdjustMessageCount(-1) == -1,
            "avatar gate changed an invalid legacy message count");
        Check(
            gate.TryRelease() == nullptr,
            "avatar gate released a preview before readiness");
        bool retainedUntilReady = true;
        for (int poll = 0; poll < 4'096; ++poll) {
            if (gate.TryRelease() != nullptr || !gate.IsHolding()) {
                retainedUntilReady = false;
                break;
            }
        }
        Check(
            retainedUntilReady,
            "avatar gate released a preview without readiness");

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
        gate.Reset();
        Check(
            !gate.IsHolding() &&
                DisposedMessageCount == 1 &&
                LastDisposedMessage == &preview,
            "avatar gate reset was not idempotent");
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
            DisposedMessageCount == 0 &&
                legacy.disposedAtDisconnect == 0 &&
                legacy.disposedAtRelease == 0,
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
        AvatarResourcesReady = true;
        disconnectClient->DisConnect();
        Check(
            DisposedMessageCount == 1 &&
                LastDisposedMessage == &preview &&
                disconnectLegacy.processCalls == 1 &&
                disconnectLegacy.disconnectCalls == 1 &&
                disconnectLegacy.disposedAtDisconnect == 1,
            "disconnect did not dispose its still-owned preview first");
        disconnectClient->Release();
        Check(
            DisposedMessageCount == 1 &&
                disconnectLegacy.releaseCalls == 1 &&
                disconnectLegacy.disposedAtRelease == 1,
            "release disposed a disconnect-reset preview twice");
    }

    FakeLegacyClient releaseLegacy;
    releaseLegacy.nextMessage = &preview;
    AvatarResourcesReady = false;
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
                LastDisposedMessage == &preview &&
                releaseLegacy.releaseCalls == 1 &&
                releaseLegacy.disposedAtRelease == 1,
            "direct release did not clean the retained preview");
    }

    FakeLegacyClient reconnectLegacy;
    reconnectLegacy.queuedMessages[0] = &preview;
    reconnectLegacy.queuedMessages[1] = &later;
    reconnectLegacy.queuedMessageCount = 2;
    AvatarResourcesReady = false;
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
                reconnectLegacy.connectCalls == 1 &&
                reconnectLegacy.disposedAtConnect == 1 &&
                reconnectClient->PickMsg() == &later,
            "reconnect delivered a stale prior-session preview");
        reconnectClient->Release();
        Check(
            DisposedMessageCount == 1 &&
                reconnectLegacy.disposedAtRelease == 1,
            "release disposed a reconnect-reset preview twice");
    }
}

void RunPersistentSchedulingChecks() {
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
    Check(client != nullptr, "persistent-hold fixture returned null");
    if (client != nullptr) {
        Check(
            client->PickMsg() == nullptr && legacy.pickCalls == 1,
            "persistent-hold fixture did not retain its preview");
        bool stayedBlocked = true;
        constexpr int SchedulingCycles = 4'096;
        for (int cycle = 0; cycle < SchedulingCycles; ++cycle) {
            client->Process();
            if (client->PickMsg() != nullptr ||
                legacy.pickCalls != 1) {
                stayedBlocked = false;
                break;
            }
        }
        Check(
            stayedBlocked &&
                legacy.processCalls == SchedulingCycles &&
                DisposedMessageCount == 0,
            "scheduling released early or stopped legacy Process");

        AvatarResourcesReady = true;
        Check(
            client->PickMsg() == &preview &&
                DisposedMessageCount == 0 &&
                legacy.disconnectCalls == 0,
            "readiness did not return the exact retained preview");
        Check(
            client->PickMsg() == &later && legacy.pickCalls == 2,
            "a later message overtook the readiness-released preview");
        client->DisConnect();
        client->Release();
        Check(
            DisposedMessageCount == 0 &&
                legacy.disconnectCalls == 1 &&
                legacy.releaseCalls == 1,
            "released scheduling fixture changed lifecycle ownership");
    }
}

} // namespace

int RunAvatarPreviewGateTests() {
    RunGateChecks();
    RunOrderingAndLifecycleChecks();
    RunPersistentSchedulingChecks();
    return Failures;
}
