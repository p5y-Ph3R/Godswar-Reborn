#pragma once

#include <cstdint>

namespace godswar::network {

// Binary contract recovered from the shipped x86 Net.dll/Net.map.
// Do not add a virtual destructor or reorder methods: Origin.exe calls these
// slots directly through the Microsoft x86 C++ ABI.
class ILegacyNetClient {
public:
    virtual std::uint32_t Release() = 0;
    virtual void SetHost(const char* host, std::uint16_t port) = 0;
    virtual bool Connect() = 0;
    virtual void DisConnect() = 0;
    virtual void Process() = 0;
    virtual std::uint32_t GetStatus() const = 0;
    virtual void* PickMsg() = 0;
    virtual bool SendMsg(const void* data, int size) = 0;
    virtual long GetMsgNum() = 0;

protected:
    ~ILegacyNetClient() = default;
};

} // namespace godswar::network
