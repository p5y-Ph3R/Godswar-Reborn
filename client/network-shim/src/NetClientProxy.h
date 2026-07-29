#pragma once

#include "AvatarPreviewGate.h"
#include "LegacyClientApi.h"
#include "NativeClientCoordinator.h"
#include "SecureClientRuntime.h"
#include "SecureClientSession.h"

#include <Windows.h>

namespace godswar::network {

// Mirrors the stock ABI's single-owner lifecycle. Release is the exclusive
// final call and must not overlap another virtual method.
class NetClientProxy final : public ILegacyNetClient {
public:
    static ILegacyNetClient* Create(ILegacyNetClient* legacyClient) noexcept;
    static ILegacyNetClient* CreateForTesting(
        ILegacyNetClient* legacyClient,
        bool enableAvatarGate,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer,
        AvatarPreloadRequester preloadRequester = nullptr) noexcept;
    static ILegacyNetClient* CreateWithCoordinatorForTesting(
        ILegacyNetClient* legacyClient,
        NativeClientCoordinator* coordinator) noexcept;
    static ILegacyNetClient* CreateWithRuntimeForTesting(
        ILegacyNetClient* legacyClient,
        NativeClientCoordinator* coordinator,
        SecureClientRuntime* secureRuntime) noexcept;

    std::uint32_t Release() override;
    void SetHost(const char* host, std::uint16_t port) override;
    bool Connect() override;
    void DisConnect() override;
    void Process() override;
    std::uint32_t GetStatus() const override;
    void* PickMsg() override;
    bool SendMsg(const void* data, int size) override;
    long GetMsgNum() override;

private:
    NetClientProxy(
        ILegacyNetClient* legacyClient,
        NativeClientCoordinator* coordinator,
        SecureClientRuntime* secureRuntime,
        NativeProxyId proxyId,
        bool enableAvatarGate,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer,
        AvatarPreloadRequester preloadRequester) noexcept;
    ~NetClientProxy() = default;

    bool ConnectSecure(const ClientBridgePlan& plan) noexcept;
    bool TryBuildSecureConfiguration(
        SecureClientSessionConfiguration* configuration) noexcept;
    void StopSecureSession() noexcept;

    ILegacyNetClient* legacyClient_;
    NativeClientCoordinator* coordinator_;
    SecureClientRuntime* secureRuntime_;
    SecureClientSession* secureSession_ = nullptr;
    NativeProxyId proxyId_;
    AvatarPreviewGate avatarPreviewGate_;
    SRWLOCK secureSendLock_{};
};

} // namespace godswar::network
