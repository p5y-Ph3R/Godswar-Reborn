#include "NetClientProxy.h"

#include <new>

namespace godswar::network {

NetClientProxy::NetClientProxy(
    ILegacyNetClient* legacyClient,
    NativeClientCoordinator* coordinator,
    SecureClientRuntime* secureRuntime,
    NativeProxyId proxyId,
    bool enableAvatarGate,
    AvatarReadinessProbe readinessProbe,
    LegacyMessageDisposer messageDisposer,
    AvatarPreloadRequester preloadRequester) noexcept
    : legacyClient_(legacyClient),
      coordinator_(coordinator),
      secureRuntime_(secureRuntime),
      proxyId_(proxyId),
      avatarPreviewGate_(
          enableAvatarGate,
          readinessProbe,
          messageDisposer,
          preloadRequester) {
}

ILegacyNetClient* NetClientProxy::Create(
    ILegacyNetClient* legacyClient) noexcept {
    return CreateWithRuntimeForTesting(
        legacyClient,
        &ProcessNativeClientCoordinator(),
        &ProcessSecureClientRuntime());
}

ILegacyNetClient* NetClientProxy::CreateWithCoordinatorForTesting(
    ILegacyNetClient* legacyClient,
    NativeClientCoordinator* coordinator) noexcept {
    return CreateWithRuntimeForTesting(
        legacyClient,
        coordinator,
        nullptr);
}

ILegacyNetClient* NetClientProxy::CreateWithRuntimeForTesting(
    ILegacyNetClient* legacyClient,
    NativeClientCoordinator* coordinator,
    SecureClientRuntime* secureRuntime) noexcept {
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
        secureRuntime,
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
    avatarPreviewGate_.Reset();
    StopSecureSession();
    legacyClient_ = nullptr;
    if (coordinator_ != nullptr) {
        static_cast<void>(coordinator_->Unregister(proxyId_));
    }
    coordinator_ = nullptr;
    secureRuntime_ = nullptr;
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

        NativeClientSnapshot snapshot{};
        if (!coordinator_->TryGetSnapshot(
                proxyId_,
                &snapshot)) {
            static_cast<void>(coordinator_->Reset(proxyId_));
            return;
        }
        if (snapshot.decision !=
            ClientRouteDecision::PassThrough) {
            // Secure and rejected logical endpoints are never handed to the
            // stock DLL. A secure bridge supplies only a loopback endpoint.
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
    const auto beginResult =
        coordinator_->BeginConnect(proxyId_, &plan);
    if (beginResult != NativeCoordinatorResult::Success) {
        SetLastError(
            beginResult == NativeCoordinatorResult::RouteRejected
                ? ERROR_ACCESS_DENIED
                : ERROR_INVALID_STATE);
        return false;
    }

    if (plan.decision == ClientRouteDecision::Login ||
        plan.decision == ClientRouteDecision::Game) {
        return ConnectSecure(plan);
    }

    if (plan.decision != ClientRouteDecision::PassThrough) {
        static_cast<void>(coordinator_->Reset(proxyId_));
        SetLastError(ERROR_ACCESS_DENIED);
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
    if (secureSession_ != nullptr) {
        StopSecureSession();
    } else {
        legacyClient_->DisConnect();
    }
    if (coordinator_ != nullptr) {
        static_cast<void>(coordinator_->Reset(proxyId_));
    }
}

void NetClientProxy::Process() {
    if (secureSession_ != nullptr &&
        !secureSession_->Poll()) {
        StopSecureSession();
        if (coordinator_ != nullptr) {
            static_cast<void>(coordinator_->Reset(proxyId_));
        }
    }
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
    if (secureSession_ != nullptr) {
        const auto routed =
            secureSession_->RouteLegacyMovement(data, size);
        if (routed ==
            SecureRealtimeMovementRouteResult::Accepted) {
            return true;
        }
        if (routed ==
            SecureRealtimeMovementRouteResult::Rejected) {
            return false;
        }
    }
    return legacyClient_->SendMsg(data, size);
}

long NetClientProxy::GetMsgNum() {
    return avatarPreviewGate_.AdjustMessageCount(
        legacyClient_->GetMsgNum());
}

bool NetClientProxy::ConnectSecure(
    const ClientBridgePlan& plan) noexcept {
    SecureClientSessionConfiguration configuration{};
    if (secureSession_ != nullptr ||
        !TryBuildSecureConfiguration(&configuration)) {
        static_cast<void>(coordinator_->Reset(proxyId_));
        SetLastError(ERROR_ACCESS_DENIED);
        return false;
    }

    auto* session = new (std::nothrow)
        SecureClientSession(configuration);
    SecureZeroMemory(
        configuration.clientInstanceId,
        sizeof(configuration.clientInstanceId));
    SecureZeroMemory(
        configuration.originSha256,
        sizeof(configuration.originSha256));
    if (session == nullptr) {
        static_cast<void>(coordinator_->Reset(proxyId_));
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return false;
    }
    if (!session->Connect(legacyClient_, plan)) {
        delete session;
        static_cast<void>(coordinator_->Reset(proxyId_));
        SetLastError(ERROR_CONNECTION_REFUSED);
        return false;
    }
    if (coordinator_->MarkConnected(plan) !=
        NativeCoordinatorResult::Success) {
        session->Disconnect();
        delete session;
        static_cast<void>(coordinator_->Reset(proxyId_));
        SetLastError(ERROR_OPERATION_ABORTED);
        return false;
    }

    secureSession_ = session;
    return true;
}

bool NetClientProxy::TryBuildSecureConfiguration(
    SecureClientSessionConfiguration* configuration) noexcept {
    if (configuration == nullptr) {
        return false;
    }
    *configuration = SecureClientSessionConfiguration{};
    if (secureRuntime_ == nullptr ||
        !secureRuntime_->TryCopyManifest(
            &configuration->manifest) ||
        !secureRuntime_->TryCopyClientInstanceId(
            configuration->clientInstanceId,
            sizeof(configuration->clientInstanceId)) ||
        !secureRuntime_->TryCopyOriginSha256(
            configuration->originSha256,
            sizeof(configuration->originSha256))) {
        return false;
    }

    configuration->grantRegistry =
        secureRuntime_->GrantRegistry();
    configuration->snapshotContext = secureRuntime_;
    configuration->snapshotRecorder =
        [](void* context,
           const SecureClientSessionSnapshot& snapshot) noexcept {
            auto* runtime =
                static_cast<SecureClientRuntime*>(context);
            if (runtime != nullptr) {
                runtime->RetainSessionSnapshot(snapshot);
            }
        };
    return configuration->grantRegistry != nullptr;
}

void NetClientProxy::StopSecureSession() noexcept {
    if (secureSession_ == nullptr) {
        return;
    }
    secureSession_->Disconnect();
    delete secureSession_;
    secureSession_ = nullptr;
}

} // namespace godswar::network
