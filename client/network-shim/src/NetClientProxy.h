#pragma once

#include "LegacyClientApi.h"

namespace godswar::network {

class NetClientProxy final : public ILegacyNetClient {
public:
    static ILegacyNetClient* Create(ILegacyNetClient* legacyClient) noexcept;

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
    explicit NetClientProxy(ILegacyNetClient* legacyClient) noexcept;
    ~NetClientProxy() = default;

    ILegacyNetClient* legacyClient_;
};

} // namespace godswar::network
