#include "NetClientProxy.h"

#include <new>

namespace godswar::network {

NetClientProxy::NetClientProxy(
    ILegacyNetClient* legacyClient,
    bool enableAvatarGate,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarMonotonicClock monotonicClock,
    std::uint64_t waitTimeoutMilliseconds) noexcept
    : legacyClient_(legacyClient),
      avatarPreviewGate_(
          enableAvatarGate,
          readinessProbe,
          messageDisposer,
          monotonicClock,
          waitTimeoutMilliseconds) {
}

ILegacyNetClient* NetClientProxy::Create(
    ILegacyNetClient* legacyClient) noexcept {
    return CreateForTesting(
        legacyClient,
        IsSupportedOriginAvatarHost(),
        AreOriginAvatarResourcesReady,
        DestroyLegacyMessage,
        ReadAvatarMonotonicMilliseconds,
        AvatarPreviewWaitTimeoutMilliseconds);
}

ILegacyNetClient* NetClientProxy::CreateForTesting(
    ILegacyNetClient* legacyClient,
    bool enableAvatarGate,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarMonotonicClock monotonicClock,
    std::uint64_t waitTimeoutMilliseconds) noexcept {
    if (legacyClient == nullptr) {
        return nullptr;
    }

    auto* proxy = new (std::nothrow) NetClientProxy(
        legacyClient,
        enableAvatarGate,
        readinessProbe,
        messageDisposer,
        monotonicClock,
        waitTimeoutMilliseconds);
    if (proxy == nullptr) {
        legacyClient->Release();
    }

    return proxy;
}

std::uint32_t NetClientProxy::Release() {
    auto* legacyClient = legacyClient_;
    legacyClient_ = nullptr;

    avatarPreviewGate_.Reset();
    const auto result = legacyClient->Release();
    delete this;
    return result;
}

void NetClientProxy::SetHost(const char* host, std::uint16_t port) {
    legacyClient_->SetHost(host, port);
}

bool NetClientProxy::Connect() {
    avatarPreviewGate_.Reset();
    return legacyClient_->Connect();
}

void NetClientProxy::DisConnect() {
    avatarPreviewGate_.Reset();
    legacyClient_->DisConnect();
}

void NetClientProxy::Process() {
    legacyClient_->Process();
}

std::uint32_t NetClientProxy::GetStatus() const {
    return legacyClient_->GetStatus();
}

void* NetClientProxy::PickMsg() {
    if (avatarPreviewGate_.IsHolding()) {
        return avatarPreviewGate_.TryRelease();
    }

    return avatarPreviewGate_.Filter(legacyClient_->PickMsg());
}

bool NetClientProxy::SendMsg(const void* data, int size) {
    return legacyClient_->SendMsg(data, size);
}

long NetClientProxy::GetMsgNum() {
    return avatarPreviewGate_.AdjustMessageCount(
        legacyClient_->GetMsgNum());
}

} // namespace godswar::network
