#include "NetClientProxy.h"

#include <new>

namespace godswar::network {

NetClientProxy::NetClientProxy(ILegacyNetClient* legacyClient) noexcept
    : legacyClient_(legacyClient) {
}

ILegacyNetClient* NetClientProxy::Create(
    ILegacyNetClient* legacyClient) noexcept {
    if (legacyClient == nullptr) {
        return nullptr;
    }

    auto* proxy = new (std::nothrow) NetClientProxy(legacyClient);
    if (proxy == nullptr) {
        legacyClient->Release();
    }

    return proxy;
}

std::uint32_t NetClientProxy::Release() {
    auto* legacyClient = legacyClient_;
    legacyClient_ = nullptr;

    const auto result = legacyClient->Release();
    delete this;
    return result;
}

void NetClientProxy::SetHost(const char* host, std::uint16_t port) {
    legacyClient_->SetHost(host, port);
}

bool NetClientProxy::Connect() {
    return legacyClient_->Connect();
}

void NetClientProxy::DisConnect() {
    legacyClient_->DisConnect();
}

void NetClientProxy::Process() {
    legacyClient_->Process();
}

std::uint32_t NetClientProxy::GetStatus() const {
    return legacyClient_->GetStatus();
}

void* NetClientProxy::PickMsg() {
    return legacyClient_->PickMsg();
}

bool NetClientProxy::SendMsg(const void* data, int size) {
    return legacyClient_->SendMsg(data, size);
}

long NetClientProxy::GetMsgNum() {
    return legacyClient_->GetMsgNum();
}

} // namespace godswar::network
