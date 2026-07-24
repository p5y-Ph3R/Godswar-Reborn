#pragma once

#include "AvatarPreviewGate.h"
#include "LegacyClientApi.h"

namespace godswar::network {

class NetClientProxy final : public ILegacyNetClient {
public:
    static ILegacyNetClient* Create(ILegacyNetClient* legacyClient) noexcept;
    static ILegacyNetClient* CreateForTesting(
        ILegacyNetClient* legacyClient,
        bool enableAvatarGate,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer,
        AvatarMonotonicClock monotonicClock =
            ReadAvatarMonotonicMilliseconds,
        std::uint64_t waitTimeoutMilliseconds =
            AvatarPreviewWaitTimeoutMilliseconds) noexcept;

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
        bool enableAvatarGate,
        AvatarReadinessProbe readinessProbe,
        LegacyMessageDisposer messageDisposer,
        AvatarMonotonicClock monotonicClock,
        std::uint64_t waitTimeoutMilliseconds) noexcept;
    ~NetClientProxy() = default;

    bool ExpireAvatarPreviewIfNeeded() noexcept;

    ILegacyNetClient* legacyClient_;
    AvatarPreviewGate avatarPreviewGate_;
};

} // namespace godswar::network
