#include "NetClientProxy.h"

#include <new>

namespace godswar::network {

NetClientProxy::NetClientProxy(
    ILegacyNetClient* legacyClient,
    NativeClientCoordinator* coordinator,
    NativeProxyId proxyId,
    bool enableAvatarGate,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarPreloadRequester preloadRequester) noexcept
    : legacyClient_(legacyClient),
      coordinator_(coordinator),
      proxyId_(proxyId),
      avatarPreviewGate_(
          enableAvatarGate,
          readinessProbe,
          messageDisposer,
          preloadRequester) {
}

ILegacyNetClient* NetClientProxy::Create(
    ILegacyNetClient* legacyClient) noexcept {
    return CreateWithCoordinatorForTesting(
        legacyClient,
        &ProcessNativeClientCoordinator());
}

ILegacyNetClient* NetClientProxy::CreateWithCoordinatorForTesting(
    ILegacyNetClient* legacyClient,
    NativeClientCoordinator* coordinator) noexcept {
    if (legacyClient == nullptr) {
        return nullptr;
    }
    if (coordinator == nullptr) {
        legacyClient->Release();
        return nullptr;
    }

    NativeProxyId proxyId = 0;
    if (coordinator->Register(&proxyId) !=
        NativeCoordinatorResult::Success) {
        legacyClient->Release();
        return nullptr;
    }

    auto* proxy = new (std::nothrow) NetClientProxy(
        legacyClient,
        coordinator,
        proxyId,
        false,
        AreOriginAvatarResourcesReady,
        DestroyLegacyMessage,
        RequestOriginAvatarPreload);
    if (proxy == nullptr) {
        static_cast<void>(coordinator->Unregister(proxyId));
        legacyClient->Release();
    }

    return proxy;
}

ILegacyNetClient* NetClientProxy::CreateForTesting(
    ILegacyNetClient* legacyClient,
    bool enableAvatarGate,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarPreloadRequester preloadRequester) noexcept {
    if (legacyClient == nullptr) {
        return nullptr;
    }

    auto* proxy = new (std::nothrow) NetClientProxy(
        legacyClient,
        nullptr,
        0,
        enableAvatarGate,
        readinessProbe,
        messageDisposer,
        preloadRequester);
    if (proxy == nullptr) {
        legacyClient->Release();
    }

    return proxy;
}

std::uint32_t NetClientProxy::Release() {
    auto* legacyClient = legacyClient_;
    legacyClient_ = nullptr;

    avatarPreviewGate_.Reset();
    if (coordinator_ != nullptr) {
        static_cast<void>(coordinator_->Unregister(proxyId_));
    }
    coordinator_ = nullptr;
    proxyId_ = 0;
    const auto result = legacyClient->Release();
    delete this;
    return result;
}

void NetClientProxy::SetHost(const char* host, std::uint16_t port) {
    if (coordinator_ != nullptr) {
        const auto result =
            coordinator_->SetHost(proxyId_, host, port);
        if (result != NativeCoordinatorResult::Success) {
            if (result == NativeCoordinatorResult::InvalidArgument) {
                static_cast<void>(coordinator_->Reset(proxyId_));
            }
            return;
        }
    }

    legacyClient_->SetHost(host, port);
}

bool NetClientProxy::Connect() {
    avatarPreviewGate_.Reset();

    if (coordinator_ == nullptr) {
        return legacyClient_->Connect();
    }

    ClientBridgePlan plan{};
    if (coordinator_->BeginConnect(proxyId_, &plan) !=
            NativeCoordinatorResult::Success) {
        return false;
    }

    // Slice 5 deliberately ships with the process route policy disabled.
    // Login/Game plans become reachable only after Slice 6 supplies a
    // certificate-validating outer stream. Never downgrade those plans to a
    // direct raw connection.
    if (plan.decision != ClientRouteDecision::PassThrough) {
        static_cast<void>(coordinator_->Reset(proxyId_));
        SetLastError(ERROR_NOT_SUPPORTED);
        return false;
    }

    if (!legacyClient_->Connect()) {
        static_cast<void>(coordinator_->Reset(proxyId_));
        return false;
    }

    if (coordinator_->MarkConnected(plan) !=
        NativeCoordinatorResult::Success) {
        legacyClient_->DisConnect();
        static_cast<void>(coordinator_->Reset(proxyId_));
        return false;
    }

    return true;
}

void NetClientProxy::DisConnect() {
    avatarPreviewGate_.Reset();
    if (coordinator_ != nullptr) {
        static_cast<void>(coordinator_->Reset(proxyId_));
    }
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
