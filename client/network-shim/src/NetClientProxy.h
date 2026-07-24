#pragma once

#include "AvatarPreviewGate.h"
#include "LegacyClientApi.h"
#include "NativeClientCoordinator.h"

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
        NativeProxyId proxyId,
        bool enableAvatarGate,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer,
        AvatarPreloadRequester preloadRequester) noexcept;
    ~NetClientProxy() = default;

    ILegacyNetClient* legacyClient_;
    NativeClientCoordinator* coordinator_;
    NativeProxyId proxyId_;
    AvatarPreviewGate avatarPreviewGate_;
};

} // namespace godswar::network
